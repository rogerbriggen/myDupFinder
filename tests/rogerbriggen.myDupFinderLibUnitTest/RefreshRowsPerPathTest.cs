// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Refresh with several rows for one path (a state that older versions of the scan produced): all rows of the path are known, one primary row is kept
/// (updated or touched), every other row of the path is removed, and nothing is removed when the file cannot be hashed.
/// </summary>
public class RefreshRowsPerPathTest : IDisposable
{
    private static readonly DateTime s_t0 = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567);

    private readonly string _tempDir;
    private readonly string _base;
    private readonly string _dbPath;
    private readonly string _reportDir;

    public RefreshRowsPerPathTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefreshRowsPerPath_" + Guid.NewGuid().ToString("N"));
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

    private string WriteFile(string relative, byte[] content, DateTime mtime)
    {
        string full = P(relative);
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

    private ScanItemDto Row(string path, string hash, long size, DateTime mtime, DateTime lastScan) => RowsPerPathSupport.Row(path, _base, hash, size, mtime, lastScan);

    private (RefreshRunner Runner, ConcurrentBag<string> Opens, ListLoggerProvider Log) RunRefresh(FakeFileEnumerationSource? source = null, bool trust = false, IService.EServiceState expectedState = IService.EServiceState.finished)
    {
        var opens = new ConcurrentBag<string>();
        var hasher = new FileHasher { BeforeOpen = p => opens.Add(p) };
        var log = new ListLoggerProvider();
        using var serviceProvider = PipelineTestSupport.BuildServices(log, LogLevel.Trace);
        var job = new MyDupFinderRefreshJobDTO
        {
            JobName = "RowsPerPath",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _base,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
            TrustDirectoryMetadata = trust,
        };
        var runner = new RefreshRunner(job, serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider, null, source, hasher);
        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        Assert.Equal(expectedState, runner.RunnerState);
        return (runner, opens, log);
    }

    // ---- the tests that show a stale row surviving must fail on the code before the change ----

    [Fact]
    public void TwoRows_FileUnchangedRelativeToOne_KeepsThatRow_AndRemovesTheOther()
    {
        byte[] content = Content(1, 100);
        string file = WriteFile("a.bin", content, s_t0);
        // The stale row is inserted last and has the later scan date: the row that matches the file still wins
        var matching = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        var stale = Row(file, "STALEHASH", 101, s_t0.AddDays(-1), s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, matching, stale);

        var (runner, opens, _) = RunRefresh();

        Assert.Empty(opens);
        var row = Assert.Single(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(matching.Id, row.Id);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(content), row.FileSha512Hash);
        Assert.True(row.LastScanDateUTC > matching.LastScanDateUTC, "the primary row was touched");
        Assert.Equal(1, runner.UnchangedCount);
        Assert.Equal(0, runner.UpdatedCount);
        Assert.Equal(1, runner.SurplusRowsRemovedCount);
        Assert.Equal(0, runner.RemovedCount);
    }

    [Fact]
    public void SurplusRows_AreCountedAndWarnedAbout_InTheSummary()
    {
        byte[] content = Content(7, 100);
        string file = WriteFile("a.bin", content, s_t0);
        RowsPerPathSupport.Insert(_dbPath, Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0), Row(file, "S", 1, s_t0, s_t0), Row(file, "S2", 2, s_t0, s_t0));

        var (runner, _, log) = RunRefresh();

        Assert.Equal(2, runner.SurplusRowsRemovedCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("2 surplus rows were removed", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("SurplusRowsRemoved=2", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Trace && e.Message.Contains("surplus rows", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreeRows_FileDeleted_CountsEveryRowAsRemoved()
    {
        string file = P("gone.bin");
        RowsPerPathSupport.Insert(_dbPath, Row(file, "H1", 1, s_t0, s_t0), Row(file, "H2", 2, s_t0, s_t0), Row(file, "H3", 3, s_t0, s_t0));

        var (runner, _, _) = RunRefresh();

        Assert.Equal(3, runner.RemovedCount);
        Assert.Equal(0, runner.SurplusRowsRemovedCount);
        Assert.Empty(RowsPerPathSupport.ReadRows(_dbPath));
    }

    [Fact]
    public void TwoRows_FileChangedRelativeToBoth_UpdatesTheRowWithTheLatestScanDate_AndRemovesTheOther()
    {
        byte[] content = Content(2, 100);
        string file = WriteFile("a.bin", content, s_t0.AddDays(1));
        var older = Row(file, "OLDHASH", 100, s_t0, s_t0);
        var newer = Row(file, "NEWERSTALE", 90, s_t0.AddHours(-5), s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, newer, older);

        var (runner, opens, _) = RunRefresh();

        Assert.Equal(new[] { file }, opens.ToArray());
        var row = Assert.Single(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(newer.Id, row.Id);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(content), row.FileSha512Hash);
        Assert.Equal(100, row.FileSize);
        Assert.Equal(s_t0.AddDays(1).Ticks, row.FileLastModificationUTC.Ticks);
        Assert.Equal(1, runner.UpdatedCount);
    }

    [Fact]
    public void TwoRows_FileDeleted_RemovesBothRows()
    {
        string file = P("gone.bin");
        RowsPerPathSupport.Insert(_dbPath, Row(file, "H1", 10, s_t0, s_t0), Row(file, "H2", 11, s_t0, s_t0.AddHours(1)), Row(P("other.bin"), "H3", 12, s_t0, s_t0));
        WriteFile("other.bin", Content(3, 12), s_t0);

        RunRefresh();

        var rows = RowsPerPathSupport.ReadRows(_dbPath);
        Assert.DoesNotContain(rows, r => r.FilenameAndPath == file);
        Assert.Single(rows);
    }

    [Fact]
    public void ThreeRows_FileUnchangedRelativeToOne_KeepsOnlyThatRow()
    {
        byte[] content = Content(4, 100);
        string file = WriteFile("a.bin", content, s_t0);
        var staleLatest = Row(file, "STALE1", 101, s_t0, s_t0.AddHours(3));
        var matching = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        var staleOld = Row(file, "STALE2", 100, s_t0.AddHours(-1), s_t0.AddHours(-2));
        RowsPerPathSupport.Insert(_dbPath, staleLatest, matching, staleOld);

        var (_, opens, _) = RunRefresh();

        Assert.Empty(opens);
        var row = Assert.Single(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(matching.Id, row.Id);
    }

    // ---- rows of another identity (OriginComputer / ScanName) under the same base path are never touched ----

    private ScanItemDto Foreign(string path, string hash, long size, DateTime mtime, DateTime lastScan) => RowsPerPathSupport.ForeignRow(path, _base, hash, size, mtime, lastScan);

    [Fact]
    public void OtherIdentity_FileUnchanged_ForeignRowIsLeftAlone()
    {
        byte[] content = Content(31, 100);
        string file = WriteFile("a.bin", content, s_t0);
        var own = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        // The foreign row has the later scan date and would be taken for a surplus row of the path
        RowsPerPathSupport.Insert(_dbPath, own, Foreign(file, "FOREIGN", 999, s_t0.AddDays(-1), s_t0.AddHours(5)));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);

        var (runner, opens, _) = RunRefresh();

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        Assert.Empty(opens);
        Assert.Equal(own.Id, Assert.Single(RowsPerPathSupport.ReadRows(_dbPath), r => r.OriginComputer == "TestPC").Id);
        Assert.Equal(0, runner.SurplusRowsRemovedCount);
    }

    [Fact]
    public void OtherIdentity_FileChanged_ForeignRowIsLeftAlone_AndOwnRowIsUpdated()
    {
        byte[] content = Content(32, 100);
        string file = WriteFile("a.bin", content, s_t0.AddDays(1));
        var own = Row(file, "OLDHASH", 90, s_t0, s_t0);
        // The foreign row matches the file as it is now: it must not be taken for the row of this job
        RowsPerPathSupport.Insert(_dbPath, own, Foreign(file, "FOREIGN", 100, s_t0.AddDays(1), s_t0.AddHours(5)));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);

        RunRefresh();

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        var ownNow = Assert.Single(RowsPerPathSupport.ReadRows(_dbPath), r => r.OriginComputer == "TestPC");
        Assert.Equal(own.Id, ownNow.Id);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(content), ownNow.FileSha512Hash);
    }

    [Fact]
    public void OtherIdentity_FileDeleted_ForeignRowsAreLeftAlone_AlsoThoseOfPathsThatDoNotExistHere()
    {
        string gone = P("gone.bin");
        string goneToo = P("goneToo.bin");
        byte[] content = Content(33, 50);
        string kept = WriteFile("kept.bin", content, s_t0);
        RowsPerPathSupport.Insert(
            _dbPath,
            Row(gone, "H1", 10, s_t0, s_t0),
            Row(kept, RowsPerPathSupport.Sha512Hex(content), 50, s_t0, s_t0),
            Foreign(gone, "FOREIGN1", 10, s_t0, s_t0),
            Foreign(goneToo, "FOREIGN2", 11, s_t0, s_t0));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);

        var (runner, _, _) = RunRefresh();

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        Assert.Equal(new[] { kept }, RowsPerPathSupport.ReadRows(_dbPath).Where(r => r.OriginComputer == "TestPC").Select(r => r.FilenameAndPath).ToArray());
        Assert.Equal(1, runner.RemovedCount);
    }

    [Fact]
    public void OtherIdentity_SeveralOwnRowsForOnePath_ForeignRowIsLeftAlone()
    {
        byte[] content = Content(34, 100);
        string file = WriteFile("a.bin", content, s_t0);
        var matching = Row(file, RowsPerPathSupport.Sha512Hex(content), 100, s_t0, s_t0);
        RowsPerPathSupport.Insert(_dbPath, matching, Row(file, "STALE", 101, s_t0, s_t0.AddHours(1)), Foreign(file, "FOREIGN", 100, s_t0, s_t0.AddHours(9)));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);

        var (runner, _, _) = RunRefresh();

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        Assert.Equal(matching.Id, Assert.Single(RowsPerPathSupport.ReadRows(_dbPath), r => r.OriginComputer == "TestPC").Id);
        Assert.Equal(1, runner.SurplusRowsRemovedCount);
    }

    [Fact]
    public void OtherIdentity_RowsBelowAnUnreadableDirectory_OfOtherIdentity_AreNotCountedOrTouched()
    {
        string locked = P("locked");
        string file = Path.Combine(locked, "x.bin");
        byte[] content = Content(35, 20);
        string kept = WriteFile("kept.bin", content, s_t0);
        RowsPerPathSupport.Insert(_dbPath, Row(kept, RowsPerPathSupport.Sha512Hex(content), 20, s_t0, s_t0), Foreign(file, "FOREIGN", 5, s_t0, s_t0));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);
        var source = new FakeFileEnumerationSource();
        source.UnreadableDirectories.Add(locked);
        source.Files.Add(FakeFileEnumerationSource.Entry(kept, 20, s_t0));

        var (runner, _, _) = RunRefresh(source, trust: true);

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        Assert.Equal(0, runner.ProtectedCount);
    }

    [Fact]
    public void OtherIdentity_OnlyForeignRowsUnderTheBasePath_RefreshChangesNothing_AndLogsAnError()
    {
        byte[] content = Content(36, 100);
        string file = WriteFile("a.bin", content, s_t0);
        RowsPerPathSupport.Insert(_dbPath, Foreign(file, "FOREIGN", 100, s_t0, s_t0), Foreign(P("b.bin"), "FOREIGN2", 5, s_t0, s_t0));
        var before = RowsPerPathSupport.Fingerprints(RowsPerPathSupport.ReadRows(_dbPath));

        // The run did nothing and says so: failed, not finished
        var (runner, opens, log) = RunRefresh(expectedState: IService.EServiceState.failed);

        Assert.Equal(before, RowsPerPathSupport.Fingerprints(RowsPerPathSupport.ReadRows(_dbPath)));
        Assert.Empty(opens);
        Assert.Equal(0, runner.AddedCount + runner.UpdatedCount + runner.RemovedCount + runner.UnchangedCount);
        string error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error).Message;
        Assert.Contains("TestPC", error, StringComparison.Ordinal);
        Assert.Contains("TestScan", error, StringComparison.Ordinal);
        Assert.Contains(RowsPerPathSupport.OtherOrigin, error, StringComparison.Ordinal);
        Assert.Contains(RowsPerPathSupport.OtherScanName, error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBasePathInTheDatabase_RefreshStillAddsTheFiles()
    {
        string file = WriteFile("a.bin", Content(37, 40), s_t0);
        // Rows of another base path exist, but none under this one: not the case of the guard
        RowsPerPathSupport.Insert(_dbPath, RowsPerPathSupport.Row(Path.Combine(_tempDir, "elsewhere", "x.bin"), Path.Combine(_tempDir, "elsewhere"), "H", 1, s_t0, s_t0, "TestPC", null, "OtherPC", "OtherScan"));

        var (runner, _, log) = RunRefresh();

        Assert.Equal(1, runner.AddedCount);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Single(RowsPerPathSupport.RowsOfPath(_dbPath, file));
    }

    // ---- the rest holds before the change as well (the rows are left alone) or is covered by the counters ----

    [Fact]
    public void TwoRows_BelowAnUnreadableDirectory_AreNotRemoved()
    {
        string locked = P("locked");
        string file = Path.Combine(locked, "x.bin");
        RowsPerPathSupport.Insert(_dbPath, Row(file, "H1", 10, s_t0, s_t0), Row(file, "H2", 11, s_t0, s_t0.AddHours(1)));
        var source = new FakeFileEnumerationSource();
        source.UnreadableDirectories.Add(locked);

        var (runner, _, _) = RunRefresh(source, trust: true);

        Assert.Equal(2, RowsPerPathSupport.RowsOfPath(_dbPath, file).Count);
        Assert.Equal(0, runner.RemovedCount);
        // Every row of the path is protected, not only one
        Assert.Equal(2, runner.ProtectedCount);
    }

    [Fact]
    public void TwoRows_HashingTheChangedFileFails_LeavesAllRowsAsTheyWere()
    {
        // The listing shows a changed file that cannot be opened (it does not exist): the hash fails, so nothing of the path may be changed or removed
        string file = P("broken.bin");
        var first = Row(file, "H1", 10, s_t0, s_t0);
        var second = Row(file, "H2", 11, s_t0, s_t0.AddHours(1));
        RowsPerPathSupport.Insert(_dbPath, first, second);
        var source = new FakeFileEnumerationSource();
        source.Files.Add(FakeFileEnumerationSource.Entry(file, 500, s_t0.AddDays(5)));

        var (runner, opens, _) = RunRefresh(source, trust: true);

        Assert.Equal(1, runner.ErrorCount);
        Assert.Equal(new[] { file }, opens.ToArray());
        var rows = RowsPerPathSupport.RowsOfPath(_dbPath, file).OrderBy(r => r.FileSha512Hash, StringComparer.Ordinal).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(first.Id, rows[0].Id);
        Assert.Equal("H1", rows[0].FileSha512Hash);
        Assert.Equal(10, rows[0].FileSize);
        Assert.Equal(second.Id, rows[1].Id);
        Assert.Equal("H2", rows[1].FileSha512Hash);
        Assert.Equal(11, rows[1].FileSize);
    }
}
