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
/// </summary>
public sealed class CheckReportApplier
{
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
    public int SkippedCount { get; private set; }
    public int ErrorCount { get; private set; }

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
        foreach (var row in rows)
        {
            try
            {
                ApplyRow(db, row, header, applyDate);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "applyCheck: failed to apply row Category={cat} Id={id}", row.Category, row.ScanItemId);
                ErrorCount++;
            }
        }

        _logger.LogInformation(
            "applyCheck finished. Updated={upd} Inserted={ins} Removed={rm} Skipped={skip} Errors={err}",
            UpdatedCount, InsertedCount, RemovedCount, SkippedCount, ErrorCount);
    }

    private void ApplyRow(ScanJobDBInserts db, CheckReportRow row, CheckReportHeader header, DateTime applyDate)
    {
        switch (row.Category)
        {
            case CheckCategory.BitRotSuspect:
            case CheckCategory.Modified:
            case CheckCategory.ModifiedNoHashChange:
                ApplyUpdate(db, row, applyDate);
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
                    RemovedCount++;
                }
                else
                {
                    _logger.LogWarning("applyCheck: MissingOnDisk row {id} not found in DB, skipping.", row.ScanItemId);
                    SkippedCount++;
                }
                break;
            case CheckCategory.NewOnDisk:
                ApplyInsert(db, row, header, applyDate);
                break;
            default:
                _logger.LogWarning("applyCheck: unknown category {cat}, skipping.", row.Category);
                SkippedCount++;
                break;
        }
    }

    private void ApplyUpdate(ScanJobDBInserts db, CheckReportRow row, DateTime applyDate)
    {
        if (row.ScanItemId == Guid.Empty)
        {
            _logger.LogWarning("applyCheck: {cat} row had empty ScanItemId, skipping.", row.Category);
            SkippedCount++;
            return;
        }

        // Size / mtime come from the Disk columns; only set when the report actually carries them.
        long? newSize = row.FileSizeDisk;
        DateTime? newMTime = row.MTimeDisk;

        // Hash is only applied when we actually carry one (NewOnDisk + Modified/BitRotSuspect with hashing enabled).
        string? newHash = row.HashDisk;
        bool hashWasRecomputed = newHash is not null;

        // Path rewrite only when the report flags it - applyCheck must not silently move rows.
        string? newPath = null;
        string? newBase = null;
        if (row.PathMoved && row.FilenameAndPathDisk is not null && row.PathBaseDisk is not null)
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
        UpdatedCount++;
    }

    private void ApplyInsert(ScanJobDBInserts db, CheckReportRow row, CheckReportHeader header, DateTime applyDate)
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
        db.InsertItem(item);
        InsertedCount++;
    }
}
