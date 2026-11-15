// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using Xunit.Abstractions;
using static RogerBriggen.MyDupFinderLibUnitTest.ApplyTestSupport;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Opt-in benchmark of applyCheck: a generated report with mixed categories is applied to a copy of a generated database, once the way applyCheck
/// worked before (one commit per row, no lookup before an insert) and once with the real applier (transactions of 500 rows, idempotent insert).
/// Run it with MYDUPFINDER_BENCHMARK=1, see docs/benchmark.md. MYDUPFINDER_BENCHMARK_FILES sets the number of report rows (default 30000).
/// </summary>
public class ApplyCheckBenchmark : IDisposable
{
    private const string RowCountVariable = "MYDUPFINDER_BENCHMARK_FILES";
    private const int DefaultRowCount = 30000;
    private const string BasePath = "/bench/base/";

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public ApplyCheckBenchmark(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "ApplyCheckBenchmark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static int GetRowCount() =>
        int.TryParse(Environment.GetEnvironmentVariable(RowCountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : DefaultRowCount;

    [BenchmarkFact]
    public void Apply_MixedReport_ReportsTimings()
    {
        int rowCount = GetRowCount();
        string seedDb = Path.Combine(_tempDir, "seed.db");

        // Share of the report: 40 % Modified, 10 % BitRotSuspect, 10 % ModifiedNoHashChange, 10 % MissingOnDisk, 30 % NewOnDisk. The first four need a row in the database.
        var existing = new List<ScanItemDto>();
        var rows = new List<CheckReportRow>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            int slot = i % 10;
            string path = BasePath + "f" + i.ToString(CultureInfo.InvariantCulture) + ".dat";
            if (slot >= 7)
            {
                rows.Add(NewOnDiskRow(path, BasePath, "NEW" + i.ToString(CultureInfo.InvariantCulture), 100 + i, Mtime.AddMinutes(i)));
                continue;
            }
            var item = NewItem(path, BasePath, "OLD" + i.ToString(CultureInfo.InvariantCulture), 10 + i);
            existing.Add(item);
            if (slot == 6)
            {
                rows.Add(new CheckReportRow { Category = CheckCategory.MissingOnDisk, ScanItemId = item.Id, FilenameAndPathDB = path, PathBaseDB = BasePath });
            }
            else if (slot >= 4)
            {
                var row = ModifiedRow(item, item.FileSize + 1, Mtime.AddDays(1), "MOD" + i.ToString(CultureInfo.InvariantCulture));
                row.Category = slot == 4 ? CheckCategory.Modified : CheckCategory.ModifiedNoHashChange;
                rows.Add(row);
            }
            else if (slot >= 1)
            {
                rows.Add(ModifiedRow(item, item.FileSize + 1, Mtime.AddDays(1), "MOD" + i.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                var row = ModifiedRow(item, item.FileSize, item.FileLastModificationUTC, "ROT" + i.ToString(CultureInfo.InvariantCulture));
                row.Category = CheckCategory.BitRotSuspect;
                rows.Add(row);
            }
        }
        SeedMany(seedDb, existing);
        SqliteConnection.ClearAllPools();

        string beforeDb = Path.Combine(_tempDir, "before.db");
        string afterDb = Path.Combine(_tempDir, "after.db");
        File.Copy(seedDb, beforeDb);
        File.Copy(seedDb, afterDb);
        string report = WriteReport(Path.Combine(_tempDir, "report.csv"), afterDb, BasePath, rows);
        _output.WriteLine($"Report: {rows.Count} rows (database with {existing.Count} rows): {rows.Count(r => r.Category == CheckCategory.Modified)} Modified, {rows.Count(r => r.Category == CheckCategory.BitRotSuspect)} BitRotSuspect, {rows.Count(r => r.Category == CheckCategory.ModifiedNoHashChange)} ModifiedNoHashChange, {rows.Count(r => r.Category == CheckCategory.MissingOnDisk)} MissingOnDisk, {rows.Count(r => r.Category == CheckCategory.NewOnDisk)} NewOnDisk");

        // Before: one SaveChanges (one commit) per row, as ApplyUpdateById / RemoveItemById / InsertItem did without a batch
        double beforeSeconds;
        using (var db = new ScanJobDBInserts(null))
        {
            db.SetupDB(beforeDb);
            long start = Stopwatch.GetTimestamp();
            foreach (var row in rows)
            {
                ApplyRowPerRowCommit(db, row);
            }
            beforeSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        }
        SqliteConnection.ClearAllPools();

        // After: the real applier
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var applier = new CheckReportApplier(provider.GetService<ILogger<CheckReportApplier>>(), provider);
        long afterStart = Stopwatch.GetTimestamp();
        applier.Apply(report);
        double afterSeconds = Stopwatch.GetElapsedTime(afterStart).TotalSeconds;
        SqliteConnection.ClearAllPools();

        _output.WriteLine($"Before (one commit per row, plain insert): {beforeSeconds:F1} s, {rows.Count / beforeSeconds:F0} rows/s");
        _output.WriteLine($"After (transactions of {CheckReportApplier.RowsPerTransaction} rows, insert looks for the path first): {afterSeconds:F1} s, {rows.Count / afterSeconds:F0} rows/s. Updated={applier.UpdatedCount} Inserted={applier.InsertedCount} Removed={applier.RemovedCount} Skipped={applier.SkippedCount} Errors={applier.ErrorCount}");
        _output.WriteLine($"Speed-up: {beforeSeconds / afterSeconds:F1}x");

        Assert.Equal(0, applier.ErrorCount);
        Assert.Equal(0, applier.SkippedCount);
        Assert.Equal(rows.Count(r => r.Category != CheckCategory.MissingOnDisk && r.Category != CheckCategory.NewOnDisk), applier.UpdatedCount);
        Assert.Equal(rows.Count(r => r.Category == CheckCategory.NewOnDisk), applier.InsertedCount);
        Assert.Equal(rows.Count(r => r.Category == CheckCategory.MissingOnDisk), applier.RemovedCount);
        // Both ways must give the same table, apart from the dates that are set to "now" and the ids of the inserted rows
        Assert.Equal(Normalize(beforeDb), Normalize(afterDb));

        // A second apply of the same report inserts and removes nothing
        var second = new CheckReportApplier(provider.GetService<ILogger<CheckReportApplier>>(), provider);
        long secondStart = Stopwatch.GetTimestamp();
        second.Apply(report);
        _output.WriteLine($"Second apply of the same report: {Stopwatch.GetElapsedTime(secondStart).TotalSeconds:F1} s. Inserted={second.InsertedCount} AlreadyApplied={second.AlreadyAppliedCount} Skipped={second.SkippedCount}");
        Assert.Equal(0, second.InsertedCount);
        Assert.Equal(rows.Count(r => r.Category == CheckCategory.NewOnDisk), second.AlreadyAppliedCount);
    }

    private static List<string> Normalize(string dbPath) =>
        ReadRows(dbPath)
            .Select(i => string.Join("|", i.FilenameAndPath, i.PathBase, i.FileSize, i.FileLastModificationUTC.Ticks, i.FileSha512Hash))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    /// <summary>The applier as it was before: every write is its own transaction.</summary>
    private static void ApplyRowPerRowCommit(ScanJobDBInserts db, CheckReportRow row)
    {
        var now = DateTime.UtcNow;
        switch (row.Category)
        {
            case CheckCategory.MissingOnDisk:
                db.RemoveItemById(row.ScanItemId);
                break;
            case CheckCategory.NewOnDisk:
                db.InsertItem(new ScanItemDto
                {
                    FilenameAndPath = row.FilenameAndPathDisk!,
                    PathBase = row.PathBaseDisk!,
                    ScanExecutionComputer = Environment.MachineName,
                    OriginComputer = "TestPC",
                    ScanName = "TestScan",
                    FileSize = row.FileSizeDisk!.Value,
                    FileCreationUTC = row.CreationTimeDisk ?? row.MTimeDisk!.Value,
                    FileLastModificationUTC = row.MTimeDisk!.Value,
                    FileSha512Hash = row.HashDisk!,
                    FirstScanDateUTC = now,
                    LastScanDateUTC = now,
                    LastSha512ScanDateUTC = now,
                });
                break;
            default:
                db.ApplyUpdateById(row.ScanItemId, row.FileSizeDisk, row.MTimeDisk, row.HashDisk, now, true, null, null);
                break;
        }
    }
}
