// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using static RogerBriggen.MyDupFinderLibUnitTest.ApplyTestSupport;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// applyCheck: no size or time without the hash of that state (finding 1), applying a report twice (finding 4), transactions of 500 rows (finding 9).
/// </summary>
public class CheckReportApplierTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _basePath;
    private readonly string _dbPath;
    private readonly string _reportDir;
    private readonly List<IDisposable> _disposables = new List<IDisposable>();

    public CheckReportApplierTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CheckReportApplierTest_" + Guid.NewGuid().ToString("N"));
        _basePath = Path.Combine(_tempDir, "data") + Path.DirectorySeparatorChar;
        _reportDir = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(_reportDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private string P(string name) => Path.Combine(_basePath, name);

    private static string Sha512Hex(byte[] content) => Convert.ToHexString(SHA512.HashData(content));

    private (CheckReportApplier Applier, ListLoggerProvider Log, ServiceProvider Services) CreateApplier()
    {
        var log = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(log);
        });
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        return (new CheckReportApplier(provider.GetService<ILogger<CheckReportApplier>>(), provider), log, provider);
    }

    private CheckReportApplier Apply(string reportPath, out ListLoggerProvider log)
    {
        var (applier, logProvider, _) = CreateApplier();
        applier.Apply(reportPath);
        log = logProvider;
        return applier;
    }

    private string Report(string name, IEnumerable<CheckReportRow> rows, bool skipHash = false) => WriteReport(Path.Combine(_reportDir, name), _dbPath, _basePath, rows, skipHash);

    // ---------- finding 1: no size or time without the hash of that state ----------

    [Fact]
    public void ModifiedRowWithoutHash_IsSkipped_AndTheRowIsUnchanged()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var other = Seed(_dbPath, P("b.dat"), _basePath);
        var before = Snapshot(_dbPath);
        var report = Report("r.csv", new[] { ModifiedRow(item, 99, Mtime.AddDays(3), hashDisk: null) }, skipHash: true);

        var applier = Apply(report, out var log);

        Assert.Equal(0, applier.UpdatedCount);
        Assert.Equal(1, applier.SkippedCount);
        Assert.Equal(1, applier.SkippedNoHashCount);
        Assert.Equal(0, applier.ErrorCount);
        Assert.Equal(before, Snapshot(_dbPath));
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("NOT applied", StringComparison.Ordinal));
        Assert.Contains(P("a.dat"), warning.Message, StringComparison.Ordinal);
        Assert.Contains("no hash", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(other);
    }

    [Fact]
    public void PathMoveOnlyRowWithoutHash_IsApplied_ButSizeTimeAndHashStayAsTheyWere()
    {
        string oldBase = Path.Combine(_tempDir, "OLD") + Path.DirectorySeparatorChar;
        var item = Seed(_dbPath, Path.Combine(oldBase, "m.dat"), oldBase);
        var row = new CheckReportRow
        {
            Category = CheckCategory.ModifiedNoHashChange,
            PathMoved = true,
            ScanItemId = item.Id,
            FilenameAndPathDB = item.FilenameAndPath,
            FilenameAndPathDisk = P("m.dat"),
            PathBaseDB = oldBase,
            PathBaseDisk = _basePath,
            FileSizeDB = item.FileSize,
            FileSizeDisk = item.FileSize,
            MTimeDB = item.FileLastModificationUTC,
            MTimeDisk = item.FileLastModificationUTC,
            HashDB = item.FileSha512Hash,
            HashDisk = null,
        };

        var applier = Apply(Report("r.csv", new[] { row }, skipHash: true), out _);

        Assert.Equal(1, applier.UpdatedCount);
        Assert.Equal(0, applier.SkippedCount);
        Assert.Equal(0, applier.SkippedNoHashCount);
        var stored = Assert.Single(ReadRows(_dbPath));
        Assert.Equal(P("m.dat"), stored.FilenameAndPath);
        Assert.Equal(_basePath, stored.PathBase);
        Assert.Equal(item.FileSize, stored.FileSize);
        Assert.Equal(item.FileLastModificationUTC, stored.FileLastModificationUTC);
        Assert.Equal(item.FileSha512Hash, stored.FileSha512Hash);
        Assert.Equal(item.LastSha512ScanDateUTC, stored.LastSha512ScanDateUTC);
        Assert.True(stored.LastScanDateUTC > item.LastScanDateUTC);
    }

    /// <summary>What the check job writes for a moved tree with SkipHashCheck, with the real check: pure moves and moves of changed files.</summary>
    [Fact]
    public void RealCheckWithSkipHashAndIgnoreBasePath_MovedFileIsApplied_MovedAndChangedFileIsSkippedCompletely()
    {
        string oldBase = Path.Combine(_tempDir, "OLD") + Path.DirectorySeparatorChar;
        var content = new byte[] { 7, 7, 7 };
        var movedOnly = Seed(_dbPath, Path.Combine(oldBase, "sub", "moved.dat"), oldBase, size: content.Length);
        var movedAndChanged = Seed(_dbPath, Path.Combine(oldBase, "sub", "changed.dat"), oldBase, size: 3);
        Directory.CreateDirectory(Path.Combine(_basePath, "sub"));
        string movedOnlyNew = P(Path.Combine("sub", "moved.dat"));
        string movedAndChangedNew = P(Path.Combine("sub", "changed.dat"));
        File.WriteAllBytes(movedOnlyNew, content);
        File.SetLastWriteTimeUtc(movedOnlyNew, Mtime);
        File.WriteAllBytes(movedAndChangedNew, new byte[] { 1, 2, 3, 4, 5 });
        File.SetLastWriteTimeUtc(movedAndChangedNew, Mtime.AddDays(2));

        var report = RunCheck(skipHash: true, ignoreBasePath: true);
        var (_, rows) = CheckReportCsv.Read(report);
        var rowList = rows.ToList();
        var moveRow = Assert.Single(rowList, r => r.ScanItemId == movedOnly.Id);
        Assert.Equal(CheckCategory.ModifiedNoHashChange, moveRow.Category);
        Assert.True(moveRow.PathMoved);
        Assert.Null(moveRow.HashDisk);
        var changedRow = Assert.Single(rowList, r => r.ScanItemId == movedAndChanged.Id);
        Assert.Equal(CheckCategory.Modified, changedRow.Category);
        Assert.True(changedRow.PathMoved);
        Assert.Null(changedRow.HashDisk);
        var changedBefore = Describe(ReadRows(_dbPath).Single(r => r.Id == movedAndChanged.Id));

        var applier = Apply(report, out var applyLog);

        Assert.True(applier.UpdatedCount == 1, string.Join("\n", applyLog.Entries.Select(e => e.Level + ": " + e.Message)));
        Assert.Equal(1, applier.SkippedCount);
        Assert.Equal(1, applier.SkippedNoHashCount);
        var rowsAfter = ReadRows(_dbPath);
        var movedAfter = rowsAfter.Single(r => r.Id == movedOnly.Id);
        Assert.Equal(movedOnlyNew, movedAfter.FilenameAndPath);
        Assert.Equal(_basePath, movedAfter.PathBase);
        Assert.Equal(OldHash, movedAfter.FileSha512Hash);
        // The moved and changed file: nothing at all (not even the path), the next check with hashing reports it completely
        Assert.Equal(changedBefore, Describe(rowsAfter.Single(r => r.Id == movedAndChanged.Id)));
    }

    [Fact]
    public void RowsWithHash_ApplySizeTimeAndHashTogether_AsBefore()
    {
        var modified = Seed(_dbPath, P("mod.dat"), _basePath);
        var rot = Seed(_dbPath, P("rot.dat"), _basePath);
        var rotRow = ModifiedRow(rot, rot.FileSize, rot.FileLastModificationUTC, "BBBB");
        rotRow.Category = CheckCategory.BitRotSuspect;
        var report = Report("r.csv", new[] { ModifiedRow(modified, 77, Mtime.AddDays(1), "CCCC"), rotRow });

        var applier = Apply(report, out _);

        Assert.Equal(2, applier.UpdatedCount);
        Assert.Equal(0, applier.SkippedCount);
        var rows = ReadRows(_dbPath);
        var m = rows.Single(r => r.Id == modified.Id);
        Assert.Equal(77, m.FileSize);
        Assert.Equal(Mtime.AddDays(1), m.FileLastModificationUTC);
        Assert.Equal("CCCC", m.FileSha512Hash);
        Assert.True(m.LastSha512ScanDateUTC > modified.LastSha512ScanDateUTC);
        var r2 = rows.Single(r => r.Id == rot.Id);
        Assert.Equal(rot.FileSize, r2.FileSize);
        Assert.Equal(rot.FileLastModificationUTC, r2.FileLastModificationUTC);
        Assert.Equal("BBBB", r2.FileSha512Hash);
    }

    [Fact]
    public void RowWithHashButWithoutDiskSizeOrTime_IsSkipped()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var before = Snapshot(_dbPath);
        var noSize = ModifiedRow(item, 1, Mtime.AddDays(1), "CCCC");
        noSize.FileSizeDisk = null;
        var noTime = ModifiedRow(item, 1, Mtime.AddDays(1), "CCCC");
        noTime.MTimeDisk = null;

        var applier = Apply(Report("r.csv", new[] { noSize, noTime }), out _);

        Assert.Equal(0, applier.UpdatedCount);
        Assert.Equal(2, applier.SkippedCount);
        Assert.Equal(before, Snapshot(_dbPath));
    }

    /// <summary>The scenario of the finding: change a file, check without hashing, apply, refresh. The refresh must hash the file again.</summary>
    [Fact]
    public void EndToEnd_ChangeFile_CheckWithSkipHash_Apply_Refresh_StoresTheNewHash()
    {
        string tree = Path.Combine(_tempDir, "tree");
        Directory.CreateDirectory(tree);
        string file = Path.Combine(tree, "doc.txt");
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        File.WriteAllBytes(file, original);
        File.SetLastWriteTimeUtc(file, Mtime);
        var scanJob = new MyDupFinderScanJobDTO { JobName = "E2E", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = tree, DatabaseFile = _dbPath, ReportPath = _reportDir };
        Assert.Equal(IService.EServiceState.finished, ScanTestRunner.Run(scanJob).RunnerState);
        Assert.Equal(Sha512Hex(original), Assert.Single(ReadRows(_dbPath)).FileSha512Hash, ignoreCase: true);

        // The file changes: another size and another time
        var changed = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
        File.WriteAllBytes(file, changed);
        File.SetLastWriteTimeUtc(file, Mtime.AddDays(5));

        var checkJob = new MyDupFinderCheckJobDTO { ScanJobDTO = scanJob, SkipHashCheck = true };
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var check = new CheckRunner(checkJob, provider.GetService<ILogger<CheckRunner>>(), provider);
        check.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var (_, checkRows) = CheckReportCsv.Read(check.ReportFilePath!);
        var row = Assert.Single(checkRows);
        Assert.Equal(CheckCategory.Modified, row.Category);
        Assert.Null(row.HashDisk);

        var applier = Apply(check.ReportFilePath!, out _);
        Assert.Equal(1, applier.SkippedNoHashCount);
        SqliteConnection.ClearAllPools();

        var refresh = new RefreshRunner(new MyDupFinderRefreshJobDTO { JobName = "E2E", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = tree, DatabaseFile = _dbPath, ReportPath = _reportDir }, provider.GetService<ILogger<RefreshRunner>>(), provider);
        refresh.Start(CancellationToken.None);

        Assert.Equal(1, refresh.UpdatedCount);
        Assert.Equal(0, refresh.UnchangedCount);
        var stored = Assert.Single(ReadRows(_dbPath));
        Assert.Equal(Sha512Hex(changed), stored.FileSha512Hash, ignoreCase: true);
        Assert.Equal(changed.Length, stored.FileSize);
        Assert.Equal(Mtime.AddDays(5), stored.FileLastModificationUTC);
    }

    private string RunCheck(bool skipHash, bool ignoreBasePath)
    {
        var job = new MyDupFinderCheckJobDTO
        {
            ScanJobDTO = new MyDupFinderScanJobDTO { JobName = "CheckJob", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _basePath, DatabaseFile = _dbPath, ReportPath = _reportDir + Path.DirectorySeparatorChar },
            SkipHashCheck = skipHash,
            IgnoreBasePath = ignoreBasePath,
        };
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var check = new CheckRunner(job, provider.GetService<ILogger<CheckRunner>>(), provider);
        check.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        return check.ReportFilePath!;
    }

    // ---------- finding 4: applying a report twice ----------

    [Fact]
    public void ApplyingTheSameReportTwice_AllCategories_GivesTheSameRows_AndInsertsNothingTheSecondTime()
    {
        var rotContent = new byte[] { 1, 2, 3 };
        var rotPath = P("rot.dat");
        File.WriteAllBytes(rotPath, rotContent);
        File.SetLastWriteTimeUtc(rotPath, Mtime);
        Seed(_dbPath, rotPath, _basePath, Sha512Hex(rotContent), rotContent.Length);
        File.WriteAllBytes(rotPath, new byte[] { 9, 9, 9 });
        File.SetLastWriteTimeUtc(rotPath, Mtime);

        var editPath = P("edit.dat");
        File.WriteAllBytes(editPath, new byte[] { 1, 2 });
        File.SetLastWriteTimeUtc(editPath, Mtime);
        Seed(_dbPath, editPath, _basePath, Sha512Hex(new byte[] { 1, 2 }), 2);
        File.WriteAllBytes(editPath, new byte[] { 1, 2, 3, 4 });
        File.SetLastWriteTimeUtc(editPath, Mtime.AddDays(1));

        var touchedPath = P("touched.dat");
        File.WriteAllBytes(touchedPath, new byte[] { 4, 4 });
        File.SetLastWriteTimeUtc(touchedPath, Mtime);
        Seed(_dbPath, touchedPath, _basePath, Sha512Hex(new byte[] { 4, 4 }), 2);
        File.SetLastWriteTimeUtc(touchedPath, Mtime.AddDays(2));

        var freshPath = P("fresh.dat");
        File.WriteAllBytes(freshPath, new byte[] { 5, 5 });
        File.SetLastWriteTimeUtc(freshPath, Mtime);
        Seed(_dbPath, P("ghost.dat"), _basePath, Sha512Hex(new byte[] { 8 }), 1);

        var report = RunCheck(skipHash: false, ignoreBasePath: false);
        Assert.Equal(5, CheckReportCsv.Read(report).Rows.Count());

        var first = Apply(report, out _);
        Assert.Equal(3, first.UpdatedCount);
        Assert.Equal(1, first.InsertedCount);
        Assert.Equal(1, first.RemovedCount);
        Assert.Equal(0, first.ErrorCount);
        var afterFirst = Snapshot(_dbPath, withoutScanDates: true);
        var rowsAfterFirst = ReadRows(_dbPath);
        Assert.Equal(4, rowsAfterFirst.Count);

        var second = Apply(report, out _);

        Assert.Equal(0, second.InsertedCount);
        Assert.Equal(1, second.AlreadyAppliedCount);
        Assert.Equal(0, second.RemovedCount);
        Assert.Equal(1, second.SkippedCount); // the ghost row is gone already
        Assert.Equal(3, second.UpdatedCount); // the same values are written again
        Assert.Equal(0, second.ErrorCount);
        Assert.Equal(afterFirst, Snapshot(_dbPath, withoutScanDates: true));
        // One row per file: no file is a copy of itself
        Assert.Equal(rowsAfterFirst.Count, ReadRows(_dbPath).Select(r => r.FilenameAndPath).Distinct().Count());
        // Inserted rows are not touched at all by the second run
        var freshFirst = rowsAfterFirst.Single(r => r.FilenameAndPath == freshPath);
        Assert.Equal(Describe(freshFirst), Describe(ReadRows(_dbPath).Single(r => r.FilenameAndPath == freshPath)));
    }

    [Fact]
    public void PartialApplyThenFullApply_InsertsEachFileOnce()
    {
        string a = P("a.dat");
        string b = P("b.dat");
        EnsureDb(_dbPath);
        var rowA = NewOnDiskRow(a, _basePath, "AB12");
        var rowB = NewOnDiskRow(b, _basePath, "CD34", size: 20);

        var partial = Apply(Report("partial.csv", new[] { rowA }), out _);
        Assert.Equal(1, partial.InsertedCount);
        var afterPartial = ReadRows(_dbPath).Single();

        // The full report carries the hash of the first file in lower case: a hash is compared ignoring the case
        var rowALower = NewOnDiskRow(a, _basePath, "ab12");
        var full = Apply(Report("full.csv", new[] { rowALower, rowB }), out _);

        Assert.Equal(1, full.InsertedCount);
        Assert.Equal(1, full.AlreadyAppliedCount);
        Assert.Equal(0, full.SkippedCount);
        var rows = ReadRows(_dbPath);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Describe(afterPartial), Describe(rows.Single(r => r.FilenameAndPath == a)));
    }

    [Fact]
    public void TheSameNewFileTwiceInOneReport_IsInsertedOnce()
    {
        EnsureDb(_dbPath);
        var row = NewOnDiskRow(P("a.dat"), _basePath, "AB12");

        var applier = Apply(Report("r.csv", new[] { row, row }), out _);

        Assert.Equal(1, applier.InsertedCount);
        Assert.Equal(1, applier.AlreadyAppliedCount);
        Assert.Single(ReadRows(_dbPath));
    }

    [Theory]
    [InlineData("OTHER", 10, 0)]
    [InlineData("AAAA", 11, 0)]
    [InlineData("AAAA", 10, 1)]
    public void NewOnDiskRow_WhosePathExistsWithOtherValues_IsSkippedWithAWarning(string hash, long size, int mtimeDays)
    {
        string path = P("a.dat");
        Seed(_dbPath, path, _basePath);
        var before = Snapshot(_dbPath);
        var row = NewOnDiskRow(path, _basePath, hash, size, Mtime.AddDays(mtimeDays));

        var applier = Apply(Report("r.csv", new[] { row }), out var log);

        Assert.Equal(0, applier.InsertedCount);
        Assert.Equal(0, applier.AlreadyAppliedCount);
        Assert.Equal(1, applier.SkippedCount);
        Assert.Equal(0, applier.SkippedNoHashCount);
        Assert.Equal(before, Snapshot(_dbPath));
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("out of date", StringComparison.Ordinal));
        Assert.Contains(path, warning.Message, StringComparison.Ordinal);
        Assert.Contains("run check again", warning.Message, StringComparison.Ordinal);
    }

    // ---------- finding 9: transactions ----------

    [Fact]
    public void ARowThatFailsAtTheSave_IsCountedAsError_AndTheOtherRowsOfTheTransactionAreApplied()
    {
        var u1 = Seed(_dbPath, P("u1.dat"), _basePath);
        var u2 = Seed(_dbPath, P("poison-u.dat"), _basePath);
        var u3 = Seed(_dbPath, P("u3.dat"), _basePath);
        var gone = Seed(_dbPath, P("gone.dat"), _basePath);
        InstallFailingInsertTrigger(_dbPath);
        InstallFailingUpdateTrigger(_dbPath);
        var rows = new List<CheckReportRow>
        {
            NewOnDiskRow(P("n1.dat"), _basePath, "N1"),
            ModifiedRow(u1, 50, Mtime.AddDays(1), "H1"),
            NewOnDiskRow(P("poison-n.dat"), _basePath, "N2"),
            ModifiedRow(u2, 60, Mtime.AddDays(1), "H2"),
            ModifiedRow(u3, 70, Mtime.AddDays(1), "H3"),
            new CheckReportRow { Category = CheckCategory.MissingOnDisk, ScanItemId = gone.Id },
            NewOnDiskRow(P("n3.dat"), _basePath, "N3"),
        };

        var applier = Apply(Report("r.csv", rows), out var log);

        Assert.Equal(2, applier.InsertedCount);
        Assert.Equal(2, applier.UpdatedCount);
        Assert.Equal(1, applier.RemovedCount);
        Assert.Equal(2, applier.ErrorCount);
        Assert.Equal(0, applier.FailedCommitCount);
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains("failed to apply row", StringComparison.Ordinal)));
        var stored = ReadRows(_dbPath);
        Assert.Equal(new[] { "n1.dat", "n3.dat", "u1.dat", "poison-u.dat", "u3.dat" }.OrderBy(n => n, StringComparer.Ordinal), stored.Select(r => Path.GetFileName(r.FilenameAndPath)).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("H1", stored.Single(r => r.Id == u1.Id).FileSha512Hash);
        Assert.Equal(OldHash, stored.Single(r => r.Id == u2.Id).FileSha512Hash);
        Assert.Equal("H3", stored.Single(r => r.Id == u3.Id).FileSha512Hash);
    }

    [Fact]
    public void AFailedCommit_IsAnError_WithTheNumberOfRows_ItsChangesAreNotCounted_AndALaterApplyRepairsIt()
    {
        InstallFailingCommitTrigger(_dbPath);
        const int total = 1100; // transactions of 500, 500 and 100 rows
        var rows = Enumerable.Range(0, total)
            .Select(i => NewOnDiskRow(P(i == 10 ? "poisoncommit.dat" : $"f{i:D4}.dat"), _basePath, "H" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        var report = Report("r.csv", rows);

        var applier = Apply(report, out var log);

        Assert.Equal(1, applier.FailedCommitCount);
        Assert.Equal(CheckReportApplier.RowsPerTransaction, applier.ErrorCount);
        Assert.Equal(total - CheckReportApplier.RowsPerTransaction, applier.InsertedCount);
        Assert.Equal(0, applier.AlreadyAppliedCount);
        Assert.Equal(total - CheckReportApplier.RowsPerTransaction, ReadRows(_dbPath).Count);
        Assert.DoesNotContain(ReadRows(_dbPath), r => r.FilenameAndPath.Contains("poisoncommit", StringComparison.Ordinal));
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("committing", StringComparison.Ordinal));
        Assert.Contains("500 report rows", error.Message, StringComparison.Ordinal);

        // The cause is gone: applying the same report again stores exactly what is missing
        DropTrigger(_dbPath, "poison_commit");
        var again = Apply(report, out _);

        Assert.Equal(0, again.FailedCommitCount);
        Assert.Equal(0, again.ErrorCount);
        Assert.Equal(CheckReportApplier.RowsPerTransaction, again.InsertedCount);
        Assert.Equal(total - CheckReportApplier.RowsPerTransaction, again.AlreadyAppliedCount);
        Assert.Equal(total, ReadRows(_dbPath).Count);
        Assert.Equal(total, ReadRows(_dbPath).Select(r => r.FilenameAndPath).Distinct().Count());
    }

    [Fact]
    public void ManyRows_AreAppliedInSeveralTransactions_AndAllCountersAdd()
    {
        var updated = Enumerable.Range(0, 700).Select(i => NewItem(P($"u{i}.dat"), _basePath)).ToList();
        SeedMany(_dbPath, updated);
        var rows = updated.Select(u => ModifiedRow(u, 5, Mtime.AddDays(1), "NEW")).ToList();
        rows.AddRange(Enumerable.Range(0, 300).Select(i => NewOnDiskRow(P($"n{i}.dat"), _basePath, "X" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var applier = Apply(Report("r.csv", rows), out _);

        Assert.Equal(700, applier.UpdatedCount);
        Assert.Equal(300, applier.InsertedCount);
        Assert.Equal(0, applier.ErrorCount);
        Assert.Equal(1000, ReadRows(_dbPath).Count);
        Assert.Equal(700, ReadRows(_dbPath).Count(r => r.FileSha512Hash == "NEW"));
    }
}
