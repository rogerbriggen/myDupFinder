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
/// applying is idempotent (inserts look for the path first, a row that already has the state of the report is counted as applied, a removed row is skipped the second time) the same report can be applied again.
/// A hash in the database always belongs to the size and modification time stored with it: size and modification time are only written together with the hash from the same row.
/// A report (schema version 2) only changes or removes a row that is still in the state the check saw (the "_DB" columns): a row that was changed after the report was written is skipped as stale,
/// a row that already has the state the report would write counts as already applied.
/// The report is read twice (it is streamed, never held in memory): a validation pass that parses every row before anything is applied, then the apply pass.
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
    /// a NewOnDisk row whose path exists with other values, a row without hash that is not a pure path move, a stale row.
    /// </summary>
    public int SkippedCount { get; private set; }

    /// <summary>
    /// Rows without a hash (report of a check with SkipHashCheck) whose change of size or modification time was NOT applied, because a hash
    /// that belongs to the new state is not in the report. Also counted in <see cref="SkippedCount"/>.
    /// </summary>
    public int SkippedNoHashCount { get; private set; }

    /// <summary>
    /// Rows whose database row was changed after the report was written (it is no longer in the state of the "_DB" columns and not at the state the row would write),
    /// or was removed after the report was written. Nothing was written, check must be run again. Also counted in <see cref="SkippedCount"/>.
    /// </summary>
    public int SkippedStaleCount { get; private set; }

    /// <summary>
    /// Rows that were applied before: a NewOnDisk row whose file is already in the database with the same path, hash, size and modification time,
    /// or an update row whose database row already has the state the row would write. Nothing was written.
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
            if (header.SchemaVersion < CheckReportHeader.CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"Report schema version {header.SchemaVersion} was written by an older version of myDupFinder and cannot be applied by this build (expected {CheckReportHeader.CurrentSchemaVersion}). Run check again with this version and apply the new report.");
            }
            throw new InvalidOperationException($"Report schema version {header.SchemaVersion} is not supported by this build (expected {CheckReportHeader.CurrentSchemaVersion}).");
        }
        if (!System.IO.File.Exists(header.DatabaseFile))
        {
            throw new InvalidOperationException($"DatabaseFile from report header does not exist: {header.DatabaseFile}");
        }

        // Validation pass: a malformed row anywhere in the report throws here, before anything is applied. Nothing is kept, the rows are only counted.
        long rowCount = 0;
        foreach (var _ in rows)
        {
            rowCount++;
        }

        using var db = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        db.SetupDB(header.DatabaseFile);

        _logger.LogInformation("applyCheck: applying {rows} rows of {file} to {db}", rowCount, reportFilePath, header.DatabaseFile);

        var applyDate = DateTime.UtcNow;
        var tally = new BatchTally();
        int rowsInBatch = 0;
        db.BeginBatch();
        try
        {
            // Apply pass: reads the file again, row by row
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
        }
#pragma warning disable CA1031 // Not swallowed: the open transaction is rolled back and the exception is thrown again
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Reading the next row failed (the file changed after the validation pass): the open transaction is not committed
            db.RollbackBatch();
            _logger.LogError(
                ex,
                "applyCheck: the report could not be read to the end (was it changed while it was applied?). The open transaction ({rows} report rows) was rolled back. Transactions that were committed before stay in the database (Updated={upd} Inserted={ins} Removed={rm}); apply the report again after checking the file.",
                rowsInBatch,
                UpdatedCount,
                InsertedCount,
                RemovedCount);
            throw;
        }
        CommitBatch(db, tally, rowsInBatch);

        _logger.LogInformation(
            "applyCheck finished. Updated={upd} Inserted={ins} Removed={rm} AlreadyApplied={already} Skipped={skip} (of which without hash={noHash}, stale={stale}) Errors={err} FailedCommits={failedCommits}",
            UpdatedCount, InsertedCount, RemovedCount, AlreadyAppliedCount, SkippedCount, SkippedNoHashCount, SkippedStaleCount, ErrorCount, FailedCommitCount);
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
            // The rows that were found already applied are errors too: that was judged inside the transaction that was rolled back
            ErrorCount += tally.Writes + tally.AlreadyApplied;
            _logger.LogError(ex, "applyCheck: committing a transaction that covered {rows} report rows failed. {writes} changes of these rows were rolled back and are NOT in the database, and {already} rows that were found already applied are counted as errors too; apply the report again to retry them.", rowsInBatch, tally.Writes, tally.AlreadyApplied);
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
                ApplyRemove(db, row, tally);
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
    /// database row had when the check ran (DB columns). The check writes exactly this (category ModifiedNoHashChange, HashDisk empty) with SkipHashCheck when size and time did not
    /// change; when one of them changed it writes category Modified and the Disk columns differ from the DB columns.
    /// </summary>
    private static bool IsPathMoveOnly(CheckReportRow row) =>
        row.PathMoved
        && row.FilenameAndPathDisk is not null
        && row.PathBaseDisk is not null
        && row.FileSizeDisk is not null
        && row.MTimeDisk is not null
        && row.FileSizeDB == row.FileSizeDisk
        && row.MTimeDB is not null
        && row.MTimeDB.Value.Ticks == row.MTimeDisk.Value.Ticks;

    private static bool SameHash(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the row is still as the check saw it: path, base path, size, modification time (ticks) and hash (ignoring case) of the "_DB" columns.</summary>
    private static bool IsAtReportedState(ScanItemDto current, CheckReportRow row) =>
        row.FilenameAndPathDB is not null
        && row.PathBaseDB is not null
        && row.FileSizeDB is not null
        && row.MTimeDB is not null
        && string.Equals(current.FilenameAndPath, row.FilenameAndPathDB, StringComparison.Ordinal)
        && string.Equals(current.PathBase, row.PathBaseDB, StringComparison.Ordinal)
        && current.FileSize == row.FileSizeDB.Value
        && current.FileLastModificationUTC.Ticks == row.MTimeDB.Value.Ticks
        && SameHash(current.FileSha512Hash, row.HashDB);

    /// <summary>
    /// True when the row has the state the update would produce: the reported state (all five "_DB" values) overridden by what the update writes
    /// (size, time and hash together, path and base path when they are rewritten). All five values are compared.
    /// </summary>
    private static bool IsAtTargetState(ScanItemDto current, CheckReportRow row, long? newSize, DateTime? newMTime, string? newHash, string? newPath, string? newBase)
    {
        var path = newPath ?? row.FilenameAndPathDB;
        var pathBase = newBase ?? row.PathBaseDB;
        var size = newSize ?? row.FileSizeDB;
        var mtime = newMTime ?? row.MTimeDB;
        var hash = newHash ?? row.HashDB;
        return path is not null
            && pathBase is not null
            && size is not null
            && mtime is not null
            && string.Equals(current.FilenameAndPath, path, StringComparison.Ordinal)
            && string.Equals(current.PathBase, pathBase, StringComparison.Ordinal)
            && current.FileSize == size.Value
            && current.FileLastModificationUTC.Ticks == mtime.Value.Ticks
            && SameHash(current.FileSha512Hash, hash);
    }

    private void SkipStale(CheckReportRow row, string what)
    {
        _logger.LogWarning(
            "applyCheck: {cat} row for {file} was NOT applied: {what} after the report was written. The report is out of date for this file, run check again.",
            row.Category,
            row.FilenameAndPathDB ?? row.FilenameAndPathDisk,
            what);
        SkippedCount++;
        SkippedStaleCount++;
    }

    private void ApplyRemove(ScanJobDBInserts db, CheckReportRow row, BatchTally tally)
    {
        if (row.ScanItemId == Guid.Empty)
        {
            _logger.LogWarning("applyCheck: MissingOnDisk row had empty ScanItemId, skipping.");
            SkippedCount++;
            return;
        }
        var current = db.GetItemById(row.ScanItemId);
        if (current is null)
        {
            // The row is gone: the report was applied before (or the row was removed some other way). Nothing to do.
            _logger.LogWarning("applyCheck: MissingOnDisk row {id} not found in DB, skipping.", row.ScanItemId);
            SkippedCount++;
            return;
        }
        if (!IsAtReportedState(current, row))
        {
            SkipStale(row, "the database row changed");
            return;
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
        else if (IsPathMoveOnly(row))
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

        // Only a row that is still as the check saw it is changed
        var current = db.GetItemById(row.ScanItemId);
        if (current is null)
        {
            SkipStale(row, "the database row was removed");
            return;
        }
        if (!IsAtReportedState(current, row))
        {
            if (IsAtTargetState(current, row, newSize, newMTime, newHash, newPath, newBase))
            {
                _logger.LogTrace("applyCheck: {cat} row for {file} is already in the database with the state of the report, nothing to do.", row.Category, row.FilenameAndPathDisk ?? row.FilenameAndPathDB);
                tally.AlreadyApplied++;
            }
            else
            {
                SkipStale(row, "the database row changed");
            }
            return;
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
