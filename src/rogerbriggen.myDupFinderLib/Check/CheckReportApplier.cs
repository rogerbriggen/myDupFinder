// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Applies a check CSV back to the database referenced in the CSV header.
/// Users edit the CSV (deleting rows they do not want applied) before invoking apply.
/// The rows are applied in transactions of <see cref="RowsPerTransaction"/> report rows. A crash loses at most the open transaction, and because
/// applying is idempotent (inserts look for the path first, updates set fixed values, a removed row is skipped the second time) the same report can be applied again.
/// A hash in the database always belongs to the size and modification time stored with it: size and modification time are only written together with the hash from the same row.
/// </summary>
public sealed class CheckReportApplier
{
    /// <summary>Number of report rows applied per database transaction (one commit per group instead of one per row).</summary>
    internal const int RowsPerTransaction = 500;

    private readonly ILogger<CheckReportApplier> _logger;
    private readonly IServiceProvider _serviceProvider;

    public CheckReportApplier(ILogger<CheckReportApplier>? logger, IServiceProvider serviceProvider)
    {
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<CheckReportApplier>();
        _serviceProvider = serviceProvider;
    }

    public int UpdatedCount { get; private set; }
    public int InsertedCount { get; private set; }
    public int RemovedCount { get; private set; }

    /// <summary>
    /// Rows that were skipped (not applied, nothing written), for any reason: empty id, missing fields, a MissingOnDisk row whose row is gone,
    /// a NewOnDisk row whose path exists with other values, a row without hash that is not a pure path move.
    /// </summary>
    public int SkippedCount { get; private set; }

    /// <summary>
    /// Rows without a hash (report of a check with SkipHashCheck) whose change of size or modification time was NOT applied, because a hash
    /// that belongs to the new state is not in the report. Also counted in <see cref="SkippedCount"/>.
    /// </summary>
    public int SkippedNoHashCount { get; private set; }

    /// <summary>
    /// NewOnDisk rows whose file is already in the database with the same path, hash, size and modification time (the report was applied before). Nothing was written.
    /// </summary>
    public int AlreadyAppliedCount { get; private set; }

    /// <summary>
    /// Rows that failed or whose transaction could not be committed.
    /// </summary>
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Transactions that could not be committed. Their rows are counted in <see cref="ErrorCount"/> and are not in the other counters.
    /// </summary>
    public int FailedCommitCount { get; private set; }

    /// <summary>What a group of rows did, only added to the counters when the group was committed.</summary>
    private sealed class BatchTally
    {
        public int Updated;
        public int Inserted;
        public int Removed;
        public int AlreadyApplied;

        public int Writes => Updated + Inserted + Removed;
    }

    public void Apply(string reportFilePath)
    {
        var (header, rows) = CheckReportCsv.Read(reportFilePath);
        if (header.SchemaVersion != CheckReportHeader.CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Report schema version {header.SchemaVersion} is not supported by this build (expected {CheckReportHeader.CurrentSchemaVersion}).");
        }
        if (!System.IO.File.Exists(header.DatabaseFile))
        {
            throw new InvalidOperationException($"DatabaseFile from report header does not exist: {header.DatabaseFile}");
        }

        using var db = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        db.SetupDB(header.DatabaseFile);

        _logger.LogInformation("applyCheck: applying {file} to {db}", reportFilePath, header.DatabaseFile);

        var applyDate = DateTime.UtcNow;
        var tally = new BatchTally();
        int rowsInBatch = 0;
        db.BeginBatch();
        foreach (var row in rows)
        {
            try
            {
                ApplyRow(db, row, header, applyDate, tally);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                // The failed save was rolled back to its savepoint, the other rows of the transaction stay valid
                _logger.LogError(ex, "applyCheck: failed to apply row Category={cat} Id={id}", row.Category, row.ScanItemId);
                ErrorCount++;
            }
            rowsInBatch++;
            if (rowsInBatch >= RowsPerTransaction)
            {
                CommitBatch(db, tally, rowsInBatch);
                tally = new BatchTally();
                rowsInBatch = 0;
                db.BeginBatch();
            }
        }
        CommitBatch(db, tally, rowsInBatch);

        _logger.LogInformation(
            "applyCheck finished. Updated={upd} Inserted={ins} Removed={rm} AlreadyApplied={already} Skipped={skip} (of which without hash={noHash}) Errors={err} FailedCommits={failedCommits}",
            UpdatedCount, InsertedCount, RemovedCount, AlreadyAppliedCount, SkippedCount, SkippedNoHashCount, ErrorCount, FailedCommitCount);
    }

    /// <summary>
    /// Commits the open transaction. The counters of its rows only advance when the commit worked. A failed commit is an error that
    /// names the number of rows it covered; its writes are lost (rolled back) and counted as errors, the run goes on with the next group.
    /// </summary>
    private void CommitBatch(ScanJobDBInserts db, BatchTally tally, int rowsInBatch)
    {
        try
        {
            db.CommitBatch();
        }
        catch (BatchWriteException ex)
        {
            FailedCommitCount++;
            ErrorCount += tally.Writes;
            _logger.LogError(ex, "applyCheck: committing a transaction that covered {rows} report rows failed. {writes} changes of these rows were rolled back and are NOT in the database; apply the report again to retry them.", rowsInBatch, tally.Writes);
            return;
        }
        UpdatedCount += tally.Updated;
        InsertedCount += tally.Inserted;
        RemovedCount += tally.Removed;
        AlreadyAppliedCount += tally.AlreadyApplied;
    }

    private void ApplyRow(ScanJobDBInserts db, CheckReportRow row, CheckReportHeader header, DateTime applyDate, BatchTally tally)
    {
        switch (row.Category)
        {
            case CheckCategory.BitRotSuspect:
            case CheckCategory.Modified:
            case CheckCategory.ModifiedNoHashChange:
                ApplyUpdate(db, row, applyDate, tally);
                break;
            case CheckCategory.MissingOnDisk:
                if (row.ScanItemId == Guid.Empty)
                {
                    _logger.LogWarning("applyCheck: MissingOnDisk row had empty ScanItemId, skipping.");
                    SkippedCount++;
                    break;
                }
                if (db.RemoveItemById(row.ScanItemId))
                {
                    tally.Removed++;
                }
                else
                {
                    _logger.LogWarning("applyCheck: MissingOnDisk row {id} not found in DB, skipping.", row.ScanItemId);
                    SkippedCount++;
                }
                break;
            case CheckCategory.NewOnDisk:
                ApplyInsert(db, row, header, applyDate, tally);
                break;
            default:
                _logger.LogWarning("applyCheck: unknown category {cat}, skipping.", row.Category);
                SkippedCount++;
                break;
        }
    }

    /// <summary>
    /// True when a row without hash only describes a moved file: PathMoved, and the size and modification time on disk (Disk columns) are the ones the
    /// database row has now. The check writes exactly this (category ModifiedNoHashChange, HashDisk empty) with SkipHashCheck when size and time did not
    /// change; when one of them changed it writes category Modified and the Disk columns differ from the row.
    /// The Disk columns are compared with the live database row, not with the report's DB columns: in reports written before the fix of the time columns the MTime_DB
    /// column was written from a value that was read back from SQLite without a time zone kind, so it is shifted by the local UTC offset and cannot be compared with
    /// MTime_Disk. Reports written now have the correct value, but older reports can still be applied, so the comparison against the live row stays.
    /// </summary>
    private static bool IsPathMoveOnly(ScanJobDBInserts db, CheckReportRow row)
    {
        if (!row.PathMoved || row.FilenameAndPathDisk is null || row.PathBaseDisk is null || row.FileSizeDisk is null || row.MTimeDisk is null)
        {
            return false;
        }
        var current = db.GetItemById(row.ScanItemId) ?? throw new InvalidOperationException($"No ScanItem with Id {row.ScanItemId} found.");
        return current.FileSize == row.FileSizeDisk.Value && current.FileLastModificationUTC == row.MTimeDisk.Value;
    }

    private void ApplyUpdate(ScanJobDBInserts db, CheckReportRow row, DateTime applyDate, BatchTally tally)
    {
        if (row.ScanItemId == Guid.Empty)
        {
            _logger.LogWarning("applyCheck: {cat} row had empty ScanItemId, skipping.", row.Category);
            SkippedCount++;
            return;
        }

        long? newSize = null;
        DateTime? newMTime = null;
        string? newHash = null;
        bool hashWasRecomputed = false;
        // Path rewrite only when the report flags it - applyCheck must not silently move rows.
        string? newPath = null;
        string? newBase = null;
        bool pathRewrite = row.PathMoved && row.FilenameAndPathDisk is not null && row.PathBaseDisk is not null;

        if (row.HashDisk is not null)
        {
            // A hash in the database must belong to the size and time stored with it: both come from the same row, or nothing is applied
            if (row.FileSizeDisk is null || row.MTimeDisk is null)
            {
                _logger.LogWarning("applyCheck: {cat} row for {file} carries a hash but no size or modification time on disk, skipping.", row.Category, row.FilenameAndPathDisk ?? row.FilenameAndPathDB);
                SkippedCount++;
                return;
            }
            newSize = row.FileSizeDisk;
            newMTime = row.MTimeDisk;
            newHash = row.HashDisk;
            hashWasRecomputed = true;
        }
        else if (IsPathMoveOnly(db, row))
        {
            // Only the location changed, size, time and hash stay as they are (nothing is written for them)
            pathRewrite = true;
        }
        else
        {
            // Size and/or time changed (or nothing to apply) and the report has no hash for the new state (check with SkipHashCheck):
            // writing the new size and time with the old hash would make refresh believe that the stored hash is current. Nothing of this row is applied, also no path move.
            _logger.LogWarning(
                "applyCheck: {cat} row for {file} was NOT applied: the change of size or modification time cannot be applied because the report has no hash for it (SkipHashCheck). Run a check with hashing (or a refresh) and apply that report.",
                row.Category,
                row.FilenameAndPathDisk ?? row.FilenameAndPathDB);
            SkippedCount++;
            SkippedNoHashCount++;
            return;
        }

        if (pathRewrite)
        {
            newPath = row.FilenameAndPathDisk;
            newBase = row.PathBaseDisk;
        }

        db.ApplyUpdateById(
            row.ScanItemId,
            newSize,
            newMTime,
            newHash,
            applyDate,
            hashWasRecomputed,
            newPath,
            newBase);
        tally.Updated++;
    }

    private void ApplyInsert(ScanJobDBInserts db, CheckReportRow row, CheckReportHeader header, DateTime applyDate, BatchTally tally)
    {
        if (row.FilenameAndPathDisk is null || row.FileSizeDisk is null || row.MTimeDisk is null)
        {
            _logger.LogWarning("applyCheck: NewOnDisk row missing required disk fields, skipping.");
            SkippedCount++;
            return;
        }
        if (row.HashDisk is null)
        {
            _logger.LogWarning("applyCheck: NewOnDisk row {file} has no hash (SkipHashCheck=true at check time); skipping insert.", row.FilenameAndPathDisk);
            SkippedCount++;
            return;
        }

        var item = new ScanItemDto
        {
            FilenameAndPath = row.FilenameAndPathDisk,
            PathBase = row.PathBaseDisk ?? header.BasePath,
            ScanExecutionComputer = Environment.MachineName,
            OriginComputer = header.OriginComputer,
            ScanName = header.ScanName,
            FileSize = row.FileSizeDisk.Value,
            FileCreationUTC = row.CreationTimeDisk ?? row.MTimeDisk.Value,
            FileLastModificationUTC = row.MTimeDisk.Value,
            FileSha512Hash = row.HashDisk,
            FirstScanDateUTC = applyDate,
            LastScanDateUTC = applyDate,
            LastSha512ScanDateUTC = applyDate,
        };
        // Matches on the path only: the report describes one base path of one scan, so a row with this path is this file
        switch (db.InsertItemIfNew(item))
        {
            case ScanJobDBInserts.InsertIfNewResult.Inserted:
                tally.Inserted++;
                break;
            case ScanJobDBInserts.InsertIfNewResult.AlreadyApplied:
                _logger.LogTrace("applyCheck: NewOnDisk {file} is already in the database with the same hash, size and modification time, nothing to do.", row.FilenameAndPathDisk);
                tally.AlreadyApplied++;
                break;
            default:
                _logger.LogWarning("applyCheck: NewOnDisk {file} was NOT inserted: the database already has a row for this path with a different hash, size or modification time. The report is out of date for this file, run check again.", row.FilenameAndPathDisk);
                SkippedCount++;
                break;
        }
    }
}
