// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Check with several rows for one path (a state that older versions of the scan produced): the primary row is compared as usual, every other row of the
/// path is reported as MissingOnDisk with its own id, so that applyCheck removes exactly that row. Also the time columns of the report.
/// </summary>
public class CheckRowsPerPathTest : IDisposable
{
    private static readonly DateTime s_t0 = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567);

    private readonly string _tempDir;
    private readonly string _base;
    private readonly string _dbPath;
    private readonly string _reportDir;

    public CheckRowsPerPathTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CheckRowsPerPath_" + Guid.NewGuid().ToString("N"));
        _base = Path.Combine(_tempDir, "base");
        _dbPath = Path.Combine(_tempDir, "db", "test.db");
        _reportDir = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(_base);
        Directory.CreateDirectory(_reportDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string P(string relative) => Path.Combine(_base, relative);

    private string WriteFile(string relative, byte[] content, DateTime mtime, string? root = null)
    {
        string full = Path.Combine(root ?? _base, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        File.SetLastWriteTimeUtc(full, mtime);
        return full;
    }

    private static byte[] Content(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private ScanItemDto Row(string path, string hash, long size, DateTime mtime, DateTime lastScan, string? pathBase = null) =>
        RowsPerPathSupport.Row(path, pathBase ?? _base, hash, size, mtime, lastScan);

    private MyDupFinderCheckJobDTO Job(bool skipHash = false, bool ignoreBasePath = false, bool trust = false, string? basePath = null) => new MyDupFinderCheckJobDTO
    {
        ScanJobDTO = new MyDupFinderScanJobDTO
        {
            JobName = "RowsPerPath",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = basePath ?? _base,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
            TrustDirectoryMetadata = trust,
        },
        SkipHashCheck = skipHash,
        IgnoreBasePath = ignoreBasePath,
    };

    private (CheckRunner Runner, CheckReportRow[] Rows, string ReportFile, ListLoggerProvider Log) RunCheck(MyDupFinderCheckJobDTO job, FakeFileEnumerationSource? source = null, bool keepReport = false)
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = PipelineTestSupport.BuildServices(log, LogLevel.Trace);
        var runner = new CheckRunner(job, serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider, source);
        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        string report = Directory.GetFiles(_reportDir, "check-*.csv").Single();
        var (_, rows) = CheckReportCsv.Read(report);
        var rowArray = rows.ToArray();
        if (!keepReport)
        {
            File.Delete(report);
        }
        return (runner, rowArray, report, log);
    }

    // ---- the tests that show a stale row surviving must fail on the code before the change ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TwoRows_FileUnchangedRelativeToOne_ReportsOnlyTheOtherRowAsMissing(bool skipHash, bool ignoreBasePath)
    {
        byte[] content = Content(1, 100);
        string file = WriteFile("a.bin", content, s_t0);
        // The stale row is the later one in the database and has the later scan date: the row that matches the file still is the primary row
        var matching = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        var stale = Row(file, "STALEHASH", 101, s_t0.AddDays(-1), s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, matching, stale);

        var (runner, rows, _, log) = RunCheck(Job(skipHash, ignoreBasePath));

        var reported = Assert.Single(rows);
        Assert.Equal(CheckCategory.MissingOnDisk, reported.Category);
        Assert.Equal(stale.Id, reported.ScanItemId);
        Assert.Equal(file, reported.FilenameAndPathDB);
        Assert.Equal(1, runner.OkCount);
        Assert.Equal(1, runner.MissingOnDiskCount);
        Assert.Equal(1, runner.SurplusRowsReportedMissingCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1 surplus rows were reported as MissingOnDisk", StringComparison.Ordinal));
        Assert.Equal(0, runner.ModifiedCount + runner.BitRotSuspectCount + runner.ModifiedNoHashChangeCount + runner.NewOnDiskCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TwoRows_FileChangedRelativeToBoth_ReportsThePrimaryRowAsModified_AndTheOtherAsMissing(bool skipHash, bool ignoreBasePath)
    {
        byte[] content = Content(2, 100);
        string file = WriteFile("a.bin", content, s_t0.AddDays(1));
        var older = Row(file, "OLDHASH", 100, s_t0, s_t0);
        var newer = Row(file, "NEWERSTALE", 90, s_t0.AddHours(-5), s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, newer, older);

        var (runner, rows, _, _) = RunCheck(Job(skipHash, ignoreBasePath));

        var modified = Assert.Single(rows, r => r.Category == CheckCategory.Modified);
        Assert.Equal(newer.Id, modified.ScanItemId);
        Assert.Equal(skipHash ? null : RowsPerPathSupport.Sha512Hex(content), modified.HashDisk);
        Assert.Equal(1, runner.ModifiedCount);
        if (skipHash)
        {
            // No hash for the primary row: applyCheck would skip it, so the surplus row is not reported (it asserted that it is, before the rule "no surplus rows without a hash")
            Assert.Single(rows);
            Assert.Equal(0, runner.MissingOnDiskCount);
            Assert.Equal(1, runner.PathsWithSurplusRowsNotReportedCount);
        }
        else
        {
            var missing = Assert.Single(rows, r => r.Category == CheckCategory.MissingOnDisk);
            Assert.Equal(older.Id, missing.ScanItemId);
            Assert.Equal(1, runner.MissingOnDiskCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoRows_FileDeleted_ReportsBothRowsAsMissing_EachWithItsOwnId(bool ignoreBasePath)
    {
        string file = P("gone.bin");
        var first = Row(file, "H1", 10, s_t0, s_t0);
        var second = Row(file, "H2", 11, s_t0, s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, first, second);

        var (runner, rows, _, _) = RunCheck(Job(skipHash: true, ignoreBasePath: ignoreBasePath));

        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal(CheckCategory.MissingOnDisk, r.Category));
        Assert.Equal(new[] { first.Id, second.Id }.OrderBy(i => i), rows.Select(r => r.ScanItemId).OrderBy(i => i));
        Assert.Equal(2, runner.MissingOnDiskCount);
    }

    [Fact]
    public void ThreeRows_FileUnchangedRelativeToOne_ReportsTheOtherTwoAsMissing()
    {
        byte[] content = Content(4, 100);
        string file = WriteFile("a.bin", content, s_t0);
        var staleLatest = Row(file, "STALE1", 101, s_t0, s_t0.AddHours(3));
        var matching = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        var staleOld = Row(file, "STALE2", 100, s_t0.AddHours(-1), s_t0.AddHours(-2));
        RowsPerPathSupport.Insert(_dbPath, staleLatest, matching, staleOld);

        var (runner, rows, _, _) = RunCheck(Job());

        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal(CheckCategory.MissingOnDisk, r.Category));
        Assert.Equal(new[] { staleLatest.Id, staleOld.Id }.OrderBy(i => i), rows.Select(r => r.ScanItemId).OrderBy(i => i));
        Assert.Equal(1, runner.OkCount);
    }

    [Fact]
    public void IgnoreBasePath_RowsOfAMovedPath_ThePrimaryRowIsReportedAsMoved_TheOtherOneAsMissing()
    {
        // The tree was scanned under _base, now it lies under another directory. Two rows (an old state) for the old path: the relative path decides which path
        // belongs to the file, the primary row inside it is the one that matches the file (here the older row, whatever the scan dates), the other is surplus.
        string moved = Path.Combine(_tempDir, "moved");
        byte[] content = Content(5, 100);
        WriteFile(Path.Combine("sub", "a.bin"), content, s_t0, moved);
        string oldPath = Path.Combine(_base, "sub", "a.bin");
        var older = Row(oldPath, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        var newer = Row(oldPath, "STALEHASH", 99, s_t0.AddDays(-1), s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, older, newer);

        var (runner, rows, _, _) = RunCheck(Job(ignoreBasePath: true, basePath: moved));

        Assert.Equal(2, rows.Length);
        var movedRow = Assert.Single(rows, r => r.PathMoved);
        Assert.Equal(older.Id, movedRow.ScanItemId);
        var missing = Assert.Single(rows, r => r.Category == CheckCategory.MissingOnDisk);
        Assert.Equal(newer.Id, missing.ScanItemId);
        Assert.False(missing.PathMoved);
        Assert.Equal(0, runner.NewOnDiskCount);
    }

    [Fact]
    public void TwoRows_BelowAnUnreadableDirectory_AreNotReportedAsMissing()
    {
        string locked = P("locked");
        string file = Path.Combine(locked, "x.bin");
        RowsPerPathSupport.Insert(_dbPath, Row(file, "H1", 10, s_t0, s_t0), Row(file, "H2", 11, s_t0, s_t0.AddHours(1)));
        var source = new FakeFileEnumerationSource();
        source.UnreadableDirectories.Add(locked);

        var (runner, rows, _, _) = RunCheck(Job(skipHash: true, trust: true), source);

        Assert.Empty(rows);
        Assert.Equal(0, runner.MissingOnDiskCount);
        // Every row of the path is protected, not only one
        Assert.Equal(2, runner.UnreadableProtectedCount);
    }

    [Fact]
    public void TwoRows_WhenTheFileCannotBeProcessed_NoRowOfThePathIsReportedAsMissing()
    {
        // The file is in the listing but its metadata cannot be read (it does not exist): an error of the file. The file is there, so none of its rows is missing.
        string file = P("broken.bin");
        RowsPerPathSupport.Insert(_dbPath, Row(file, "H1", 10, s_t0, s_t0), Row(file, "H2", 11, s_t0, s_t0.AddHours(1)));
        var source = new FakeFileEnumerationSource();
        source.Files.Add(FakeFileEnumerationSource.Entry(file, 10, s_t0));

        var (runner, rows, _, _) = RunCheck(Job(), source);

        Assert.Equal(1, runner.ErrorCount);
        Assert.Empty(rows);
        Assert.Equal(0, runner.MissingOnDiskCount);
    }

    // ---- rows of another identity (OriginComputer / ScanName) are never compared, reported or changed ----

    private ScanItemDto Foreign(string path, string hash, long size, DateTime mtime, DateTime lastScan) => RowsPerPathSupport.ForeignRow(path, _base, hash, size, mtime, lastScan);

    /// <summary>Checks, asserts that no report row names a row of the other identity, applies the report and asserts that those rows are unchanged.</summary>
    private CheckReportRow[] CheckApplyAndAssertForeignIntact(bool skipHash, bool ignoreBasePath)
    {
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);
        var foreignIds = RowsPerPathSupport.ReadRows(_dbPath).Where(r => r.OriginComputer == RowsPerPathSupport.OtherOrigin).Select(r => r.Id).ToHashSet();
        Assert.NotEmpty(foreignIds);

        var (_, rows, reportFile, _) = RunCheck(Job(skipHash, ignoreBasePath), keepReport: true);

        Assert.DoesNotContain(rows, r => foreignIds.Contains(r.ScanItemId));
        using (var serviceProvider = PipelineTestSupport.BuildServices())
        {
            new CheckReportApplier(null, serviceProvider).Apply(reportFile);
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        return rows;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OtherIdentity_FileUnchanged_ForeignRowIsNotReported_AndSurvivesApplyCheck(bool skipHash, bool ignoreBasePath)
    {
        byte[] content = Content(41, 100);
        string file = WriteFile("a.bin", content, s_t0);
        var own = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        RowsPerPathSupport.Insert(_dbPath, own, Foreign(file, "FOREIGN", 999, s_t0.AddDays(-1), s_t0.AddHours(5)));

        var rows = CheckApplyAndAssertForeignIntact(skipHash, ignoreBasePath);

        Assert.Empty(rows);
        Assert.Equal(own.Id, Assert.Single(RowsPerPathSupport.ReadRows(_dbPath), r => r.OriginComputer == "TestPC").Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OtherIdentity_FileChanged_TheOwnRowIsCompared_TheForeignRowIsNot(bool skipHash, bool ignoreBasePath)
    {
        byte[] content = Content(42, 100);
        string file = WriteFile("a.bin", content, s_t0.AddDays(1));
        var own = Row(file, "OLDHASH", 90, s_t0, s_t0);
        RowsPerPathSupport.Insert(_dbPath, own, Foreign(file, "FOREIGN", 100, s_t0.AddDays(1), s_t0.AddHours(5)));

        var rows = CheckApplyAndAssertForeignIntact(skipHash, ignoreBasePath);

        var modified = Assert.Single(rows);
        Assert.Equal(CheckCategory.Modified, modified.Category);
        Assert.Equal(own.Id, modified.ScanItemId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OtherIdentity_FileDeleted_OnlyTheOwnRowIsReported_AndForeignRowsSurviveApplyCheck(bool skipHash, bool ignoreBasePath)
    {
        string gone = P("gone.bin");
        var own = Row(gone, "H1", 10, s_t0, s_t0);
        RowsPerPathSupport.Insert(_dbPath, own, Foreign(gone, "FOREIGN1", 10, s_t0, s_t0), Foreign(P("goneToo.bin"), "FOREIGN2", 11, s_t0, s_t0));

        var rows = CheckApplyAndAssertForeignIntact(skipHash, ignoreBasePath);

        var missing = Assert.Single(rows);
        Assert.Equal(own.Id, missing.ScanItemId);
        Assert.Equal(CheckCategory.MissingOnDisk, missing.Category);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OtherIdentity_SeveralOwnRowsForOnePath_OnlyTheOwnSurplusRowIsReported(bool skipHash, bool ignoreBasePath)
    {
        byte[] content = Content(43, 100);
        string file = WriteFile("a.bin", content, s_t0);
        var matching = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        var stale = Row(file, "STALE", 101, s_t0.AddDays(-1), s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, matching, stale, Foreign(file, "FOREIGN", 100, s_t0, s_t0.AddHours(9)));

        var rows = CheckApplyAndAssertForeignIntact(skipHash, ignoreBasePath);

        var missing = Assert.Single(rows);
        Assert.Equal(stale.Id, missing.ScanItemId);
        Assert.Equal(matching.Id, Assert.Single(RowsPerPathSupport.ReadRows(_dbPath), r => r.OriginComputer == "TestPC").Id);
    }

    [Fact]
    public void OtherIdentity_OnlyForeignRowsUnderTheBasePath_CheckLogsAWarning_AndReportsTheFilesAsNew()
    {
        byte[] content = Content(44, 100);
        string file = WriteFile("a.bin", content, s_t0);
        RowsPerPathSupport.Insert(_dbPath, Foreign(file, "FOREIGN", 100, s_t0, s_t0));

        var (runner, rows, _, log) = RunCheck(Job(skipHash: true));

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("OriginComputer", StringComparison.Ordinal)).Message;
        Assert.Contains("TestPC", warning, StringComparison.Ordinal);
        Assert.Contains("TestScan", warning, StringComparison.Ordinal);
        Assert.Contains(RowsPerPathSupport.OtherOrigin, warning, StringComparison.Ordinal);
        Assert.Equal(CheckCategory.NewOnDisk, Assert.Single(rows).Category);
        Assert.Equal(0, runner.MissingOnDiskCount);
    }

    // ---- the primary row of a moved path is chosen with the metadata of the file; no surplus rows without a hash for the primary row ----

    private (string Root, string RowPath, string FilePath) Place(bool moved, string name, byte[] content, DateTime mtime)
    {
        string root = moved ? Path.Combine(_tempDir, "moved") : _base;
        string file = WriteFile(name, content, mtime, root);
        return (root, P(name), file);
    }

    private (CheckRunner Runner, CheckReportRow[] Rows) CheckAndApply(MyDupFinderCheckJobDTO job)
    {
        var (runner, rows, reportFile, _) = RunCheck(job, keepReport: true);
        using (var serviceProvider = PipelineTestSupport.BuildServices())
        {
            new CheckReportApplier(null, serviceProvider).Apply(reportFile);
        }
        SqliteConnection.ClearAllPools();
        return (runner, rows);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovedPath_TheOlderRowMatchesTheFile_SkipHashCheck_KeepsTheMatchingRowAndItsHash(bool skipHash)
    {
        // The older row matches the file, the newer one is stale. The matching row is the primary row, whatever the scan order, and the stale one goes.
        byte[] content = Content(51, 100);
        string hash = RowsPerPathSupport.Sha512Hex(content);
        var (root, oldPath, file) = Place(moved: true, "sub.bin", content, s_t0);
        var older = Row(oldPath, hash, 100, s_t0, s_t0);
        var newer = Row(oldPath, "STALEHASH", 99, s_t0.AddDays(-1), s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, older, newer);

        CheckAndApply(Job(skipHash: skipHash, ignoreBasePath: true, basePath: root));

        var row = Assert.Single(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(older.Id, row.Id);
        Assert.Equal(hash, row.FileSha512Hash);
        Assert.Equal(file, row.FilenameAndPath);
        Assert.Equal(root, row.PathBase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedFileWithTwoStaleRows_SkipHashCheck_ReportsNoSurplusRow_AndBothRowsStay(bool moved)
    {
        byte[] content = Content(52, 100);
        var (root, rowPath, _) = Place(moved, "a.bin", content, s_t0.AddDays(3));
        var first = Row(rowPath, "STALE1", 90, s_t0, s_t0);
        var second = Row(rowPath, "STALE2", 91, s_t0, s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, first, second);

        var (runner, rows) = CheckAndApply(Job(skipHash: true, ignoreBasePath: moved, basePath: root));

        Assert.Equal(1, runner.PathsWithSurplusRowsNotReportedCount);
        Assert.Equal(0, runner.SurplusRowsReportedMissingCount);
        Assert.DoesNotContain(rows, r => r.Category == CheckCategory.MissingOnDisk);
        Assert.Equal(CheckCategory.Modified, Assert.Single(rows).Category);
        var left = RowsPerPathSupport.ReadRows(_dbPath);
        Assert.Equal(2, left.Count);
        Assert.Contains(left, r => r.Id == first.Id && r.FileSha512Hash == "STALE1");
        Assert.Contains(left, r => r.Id == second.Id && r.FileSha512Hash == "STALE2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedFileWithTwoStaleRows_WithHashing_LeavesOneRowWithTheNewHash(bool moved)
    {
        byte[] content = Content(53, 100);
        var (root, rowPath, file) = Place(moved, "a.bin", content, s_t0.AddDays(3));
        RowsPerPathSupport.Insert(_dbPath, Row(rowPath, "STALE1", 90, s_t0, s_t0), Row(rowPath, "STALE2", 91, s_t0, s_t0.AddHours(1)));

        var (runner, _) = CheckAndApply(Job(skipHash: false, ignoreBasePath: moved, basePath: root));

        Assert.Equal(0, runner.PathsWithSurplusRowsNotReportedCount);
        Assert.Equal(1, runner.SurplusRowsReportedMissingCount);
        var row = Assert.Single(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(RowsPerPathSupport.Sha512Hex(content), row.FileSha512Hash);
        Assert.Equal(file, row.FilenameAndPath);
        Assert.Equal(100, row.FileSize);
    }

    // ---- check, then applyCheck, then the duplicate finder ----

    [Fact]
    public void EndToEnd_CheckThenApplyCheck_LeavesOneRowPerPath_AndTheFileIsNoDuplicateOfItself()
    {
        // f1: the file is unchanged; the old state has a second row with the same hash but another size and time (the file looks like a duplicate of itself)
        byte[] c1 = Content(11, 100);
        string f1 = WriteFile("f1.bin", c1, s_t0);
        string h1 = RowsPerPathSupport.Sha512Hex(c1);
        // f2: unchanged, the second row has a wrong hash
        byte[] c2 = Content(12, 110);
        string f2 = WriteFile("f2.bin", c2, s_t0);
        string h2 = RowsPerPathSupport.Sha512Hex(c2);
        // f3: changed after both rows were written
        byte[] c3 = Content(13, 120);
        string f3 = WriteFile("f3.bin", c3, s_t0.AddDays(2));
        string h3 = RowsPerPathSupport.Sha512Hex(c3);
        var f1Current = Row(f1, h1, 100, s_t0, s_t0);
        var f2Current = Row(f2, h2, 110, s_t0, s_t0);
        var f3Newest = Row(f3, "STALE3B", 119, s_t0, s_t0.AddHours(2));
        RowsPerPathSupport.Insert(
            _dbPath,
            f1Current,
            Row(f1, h1, 90, s_t0.AddDays(-4), s_t0.AddHours(1)),
            Row(f2, "WRONGHASH", 111, s_t0.AddDays(-1), s_t0.AddHours(1)),
            f2Current,
            Row(f3, "STALE3A", 118, s_t0, s_t0),
            f3Newest);
        Assert.Contains(DuplicatePaths(), p => p == f1);

        var (runner, rows, reportFile, _) = RunCheck(Job(), keepReport: true);

        Assert.Equal(3, runner.MissingOnDiskCount);
        using (var serviceProvider = PipelineTestSupport.BuildServices())
        {
            var applier = new CheckReportApplier(null, serviceProvider);
            applier.Apply(reportFile);
            Assert.Equal(0, applier.ErrorCount);
            Assert.Equal(3, applier.RemovedCount);
            Assert.Equal(1, applier.UpdatedCount);
        }
        SqliteConnection.ClearAllPools();

        var all = RowsPerPathSupport.ReadRows(_dbPath);
        Assert.Equal(3, all.Count);
        var r1 = Assert.Single(all, r => r.FilenameAndPath == f1);
        Assert.Equal(f1Current.Id, r1.Id);
        Assert.Equal(h1, r1.FileSha512Hash);
        var r2 = Assert.Single(all, r => r.FilenameAndPath == f2);
        Assert.Equal(f2Current.Id, r2.Id);
        Assert.Equal(h2, r2.FileSha512Hash);
        var r3 = Assert.Single(all, r => r.FilenameAndPath == f3);
        Assert.Equal(f3Newest.Id, r3.Id);
        Assert.Equal(h3, r3.FileSha512Hash);
        Assert.Equal(120, r3.FileSize);
        Assert.Equal(s_t0.AddDays(2).Ticks, r3.FileLastModificationUTC.Ticks);
        Assert.Empty(DuplicatePaths());
    }

    /// <summary>The paths of the rows that the duplicate finder reports as duplicates (rows with a hash that another row has too).</summary>
    private List<string> DuplicatePaths()
    {
        using var db = new DubFinderDB(null);
        db.SetupDB(_dbPath);
        var dups = db.FindDupsInSameDB() ?? new List<ScanItemDto>();
        var paths = dups.Select(d => d.FilenameAndPath).ToList();
        db.Dispose();
        SqliteConnection.ClearAllPools();
        return paths;
    }

    // ---- the time columns of the report ----

    [Fact]
    public void ReportOfAnUnchangedFile_HasTheSameTextInMTimeDbAndMTimeDisk()
    {
        // A bit rot suspect row has both time columns for a file whose size and time did not change. The DB value is read back from SQLite without a time zone kind:
        // it must not be shifted by the offset of the local time zone (the machine of the author is in a time zone that is not UTC)
        byte[] content = Content(21, 100);
        string file = WriteFile("a.bin", content, s_t0);
        RowsPerPathSupport.Insert(_dbPath, Row(file, "NOTTHEHASH", 100, s_t0, s_t0));

        var (_, rows, reportFile, _) = RunCheck(Job(), keepReport: true);

        Assert.Equal(CheckCategory.BitRotSuspect, Assert.Single(rows).Category);
        string dataLine = File.ReadAllLines(reportFile).Single(l => l.StartsWith("BitRotSuspect,", StringComparison.Ordinal));
        string[] columns = dataLine.Split(',');
        string mtimeDb = columns[9];
        string mtimeDisk = columns[10];
        Assert.Equal(s_t0.ToString("o", System.Globalization.CultureInfo.InvariantCulture), mtimeDisk);
        Assert.Equal(mtimeDisk, mtimeDb);
    }

    // ---- report file names and hash errors of new files ----

    [Fact]
    public void ReportFile_IsNeverOverwritten_ASuffixIsAdded()
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = PipelineTestSupport.BuildServices(log, LogLevel.Trace);
        using var runner = new CheckRunner(Job(), serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider, null);
        string taken = Path.Combine(_reportDir, "check-x-20260101-000000.csv");
        File.WriteAllText(taken, "old");

        using (var writer = runner.CreateReportWriter(taken))
        {
            writer.Write("new");
        }
        string second = Path.Combine(_reportDir, "check-x-20260101-000000-2.csv");
        Assert.Equal(second, runner.ReportFilePath);
        using (var writer = runner.CreateReportWriter(taken))
        {
            writer.Write("new");
        }
        Assert.Equal(Path.Combine(_reportDir, "check-x-20260101-000000-3.csv"), runner.ReportFilePath);

        Assert.Equal("old", File.ReadAllText(taken));
        Assert.Equal("new", File.ReadAllText(second));
        // No byte order mark
        Assert.Equal((byte)'n', File.ReadAllBytes(second)[0]);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void NewFileThatCannotBeHashed_IsReportedAndCountedAsError(bool skipHash, int expectedErrors)
    {
        // The file is in the listing but does not exist: it has no row (NewOnDisk) and hashing fails
        string file = P("new-unreadable.bin");
        RowsPerPathSupport.Insert(_dbPath, Row(P("other.bin"), "H1", 10, s_t0, s_t0));
        var source = new FakeFileEnumerationSource();
        source.Files.Add(FakeFileEnumerationSource.Entry(file, 10, s_t0));

        var (runner, rows, _, _) = RunCheck(Job(skipHash: skipHash, trust: true), source);

        Assert.Equal(expectedErrors, runner.ErrorCount);
        Assert.Equal(1, runner.NewOnDiskCount);
        var newRow = Assert.Single(rows, r => r.Category == CheckCategory.NewOnDisk);
        Assert.Null(newRow.HashDisk);
    }
}
