// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// What a resumed scan does with a file that is already in the database: skip it when size and last write time are the same, otherwise hash it again
/// and UPDATE its row in place (one row per path), never insert a second row for the path. Every test runs for the sequential scan (1 hash worker) and the pipeline (4).
/// </summary>
public class ScanResumeChangeTest : IDisposable
{
    private static readonly DateTime s_t0 = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567);

    private readonly string _tempDir;
    private readonly string _treeDir;
    private readonly string _dbPath;

    public ScanResumeChangeTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScanResumeChange_" + Guid.NewGuid().ToString("N"));
        _treeDir = Path.Combine(_tempDir, "tree");
        _dbPath = Path.Combine(_tempDir, "db", "scan.db");
        Directory.CreateDirectory(_treeDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static byte[] Content(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private string Write(string relative, byte[] content, DateTime mtime)
    {
        string full = Path.Combine(_treeDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        File.SetLastWriteTimeUtc(full, mtime);
        return full;
    }

    /// <summary>Three files (a: 100 bytes, b: 200 bytes, sub/c: 300 bytes), all with the last write time s_t0.</summary>
    private (string A, string B, string C) CreateTree()
    {
        string a = Write("a.bin", Content(1, 100), s_t0);
        string b = Write("b.bin", Content(2, 200), s_t0);
        string c = Write(Path.Combine("sub", "c.bin"), Content(3, 300), s_t0);
        return (a, b, c);
    }

    private (ScanRunner Runner, ConcurrentBag<string> Opens, ListLoggerProvider Log) RunScan(int workers, bool trust = false, IFileEnumerationSource? source = null, CancellationToken token = default, string? databaseFile = null)
    {
        var opens = new ConcurrentBag<string>();
        var hasher = new FileHasher { BeforeOpen = p => opens.Add(p) };
        var log = new ListLoggerProvider();
        using var serviceProvider = PipelineTestSupport.BuildServices(log, LogLevel.Information);
        var job = new MyDupFinderScanJobDTO
        {
            JobName = "ResumeChange",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _treeDir,
            DatabaseFile = databaseFile ?? _dbPath,
            ReportPath = _tempDir,
            TrustDirectoryMetadata = trust,
            HashWorkers = workers,
        };
        var runner = new ScanRunner(job, serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, source, hasher);
        if (workers > 1)
        {
            PipelineTestSupport.StartGuarded(runner, token);
        }
        else
        {
            runner.Start(token);
        }
        SqliteConnection.ClearAllPools();
        return (runner, opens, log);
    }

    private List<ScanItemDto> Rows() => RowsPerPathSupport.ReadRows(_dbPath);

    private ScanItemDto Single(string path) => Assert.Single(RowsPerPathSupport.RowsOfPath(_dbPath, path));

    // ---- the first two tests must fail on the code before the change ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ChangedContentWithTheSameSizeAndANewModificationTime_IsRehashed_AndItsRowUpdatedInPlace(int workers)
    {
        var (a, b, c) = CreateTree();
        Assert.Equal(IService.EServiceState.finished, RunScan(workers).Runner.RunnerState);
        var rowsBefore = Rows().ToDictionary(r => r.FilenameAndPath);
        byte[] newContent = Content(99, 200);
        File.WriteAllBytes(b, newContent);
        DateTime newTime = s_t0.AddDays(1);
        File.SetLastWriteTimeUtc(b, newTime);

        var (runner, opens, _) = RunScan(workers);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        // Only the changed file was opened
        Assert.Equal(new[] { b }, opens.ToArray());
        var rows = Rows();
        Assert.Equal(3, rows.Count);
        var bRow = Single(b);
        Assert.Equal(rowsBefore[b].Id, bRow.Id);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(newContent), bRow.FileSha512Hash);
        Assert.Equal(200, bRow.FileSize);
        Assert.Equal(newTime.Ticks, bRow.FileLastModificationUTC.Ticks);
        // The scan dates moved on
        Assert.True(bRow.LastScanDateUTC > rowsBefore[b].LastScanDateUTC);
        Assert.True(bRow.LastSha512ScanDateUTC > rowsBefore[b].LastSha512ScanDateUTC);
        // The others are untouched
        foreach (string unchanged in new[] { a, c })
        {
            Assert.Equal(rowsBefore[unchanged].Id, Single(unchanged).Id);
            Assert.Equal(rowsBefore[unchanged].FileSha512Hash, Single(unchanged).FileSha512Hash);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ChangedSize_UpdatesTheRowInPlace_AndInsertsNothing(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var rowsBefore = Rows().ToDictionary(r => r.FilenameAndPath);
        byte[] newContent = Content(98, 250);
        File.WriteAllBytes(b, newContent);
        File.SetLastWriteTimeUtc(b, s_t0.AddHours(5));

        var (runner, opens, _) = RunScan(workers);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(new[] { b }, opens.ToArray());
        Assert.Equal(3, Rows().Count);
        var bRow = Single(b);
        Assert.Equal(rowsBefore[b].Id, bRow.Id);
        Assert.Equal(250, bRow.FileSize);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(newContent), bRow.FileSha512Hash);
        Assert.Equal(s_t0.AddHours(5).Ticks, bRow.FileLastModificationUTC.Ticks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Counters_AndSummaryLine_CountUpdatedRowsAndRemovedSurplusRows(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var rowB = Single(b);
        var rowC = Single(c);
        RowsPerPathSupport.Insert(_dbPath, RowsPerPathSupport.Row(b, _treeDir, "S1", 5, s_t0.AddDays(-1), rowB.LastScanDateUTC.AddHours(1), Environment.MachineName),
            RowsPerPathSupport.Row(c, _treeDir, "S2", 6, s_t0.AddDays(-1), rowC.LastScanDateUTC.AddHours(1), Environment.MachineName));
        File.WriteAllBytes(b, Content(77, 200));
        File.SetLastWriteTimeUtc(b, s_t0.AddDays(1));

        var (runner, _, log) = RunScan(workers);

        Assert.Equal(1, runner.ExistingRowsUpdatedCount);
        Assert.Equal(2, runner.SurplusRowsRemovedCount);
        // An update counts as a success (like in refresh), a removed surplus row does not
        string finished = Assert.Single(log.Entries, e => e.Message.StartsWith("Scanning finished...", StringComparison.Ordinal)).Message;
        Assert.Contains("SuccessCount=1 ", finished, StringComparison.Ordinal);
        Assert.Contains("UpdatedExistingRows=1 RemovedSurplusRows=2", finished, StringComparison.Ordinal);
    }

    // ---- rows of another identity (OriginComputer / ScanName) that were scanned by this computer are never updated or removed ----

    private ScanItemDto Foreign(string path, long size, DateTime lastScan) =>
        RowsPerPathSupport.ForeignRow(path, _treeDir, "FOREIGN", size, s_t0.AddDays(-2), lastScan, Environment.MachineName);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OtherIdentity_UnchangedFile_ForeignRowIsLeftAlone(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var own = Single(b);
        RowsPerPathSupport.Insert(_dbPath, Foreign(b, 5, own.LastScanDateUTC.AddHours(3)));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);

        var (runner, opens, _) = RunScan(workers);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Empty(opens);
        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        Assert.Equal(own.Id, Assert.Single(RowsPerPathSupport.RowsOfPath(_dbPath, b), r => r.OriginComputer == "TestPC").Id);
        Assert.Equal(0, runner.SurplusRowsRemovedCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OtherIdentity_ChangedFile_OnlyTheOwnRowIsUpdated(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var own = Single(b);
        RowsPerPathSupport.Insert(_dbPath, Foreign(b, 5, own.LastScanDateUTC.AddHours(3)));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);
        byte[] newContent = Content(61, 200);
        File.WriteAllBytes(b, newContent);
        File.SetLastWriteTimeUtc(b, s_t0.AddDays(1));

        RunScan(workers);

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        var ownNow = Assert.Single(RowsPerPathSupport.RowsOfPath(_dbPath, b), r => r.OriginComputer == "TestPC");
        Assert.Equal(own.Id, ownNow.Id);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(newContent), ownNow.FileSha512Hash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OtherIdentity_ChangedFileWithOnlyForeignRows_InsertsANewRowAndLeavesTheForeignOneAlone(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        string d = Write("d.bin", Content(62, 77), s_t0);
        RowsPerPathSupport.Insert(_dbPath, Foreign(d, 5, DateTime.UtcNow));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);

        RunScan(workers);

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        var rows = RowsPerPathSupport.RowsOfPath(_dbPath, d);
        Assert.Equal(2, rows.Count);
        var inserted = Assert.Single(rows, r => r.OriginComputer == "TestPC");
        Assert.Equal(77, inserted.FileSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OtherIdentity_SeveralOwnRowsForOnePath_ForeignRowIsLeftAlone(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var own = Single(b);
        RowsPerPathSupport.Insert(
            _dbPath,
            RowsPerPathSupport.Row(b, _treeDir, "STALE", 150, s_t0.AddDays(-3), own.LastScanDateUTC.AddHours(1), Environment.MachineName),
            Foreign(b, 5, own.LastScanDateUTC.AddHours(9)));
        var before = RowsPerPathSupport.ForeignFingerprints(_dbPath);
        byte[] newContent = Content(63, 200);
        File.WriteAllBytes(b, newContent);
        File.SetLastWriteTimeUtc(b, s_t0.AddDays(2));

        var (runner, _, _) = RunScan(workers);

        Assert.Equal(before, RowsPerPathSupport.ForeignFingerprints(_dbPath));
        var ownNow = Assert.Single(RowsPerPathSupport.RowsOfPath(_dbPath, b), r => r.OriginComputer == "TestPC");
        Assert.Equal(RowsPerPathSupport.Sha512Hex(newContent), ownNow.FileSha512Hash);
        Assert.Equal(1, runner.SurplusRowsRemovedCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OtherIdentity_FileDeletedOnDisk_NoRowOfThePathIsChanged(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        RowsPerPathSupport.Insert(_dbPath, Foreign(a, 5, DateTime.UtcNow));
        var all = RowsPerPathSupport.Fingerprints(RowsPerPathSupport.RowsOfPath(_dbPath, a));
        File.Delete(a);

        RunScan(workers);

        Assert.Equal(all, RowsPerPathSupport.Fingerprints(RowsPerPathSupport.RowsOfPath(_dbPath, a)));
    }

    // ---- unchanged files, old database states, other computers ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void UnchangedFiles_AreSkipped_AndNotOpened(int workers)
    {
        CreateTree();
        RunScan(workers);
        var rowsBefore = Rows().OrderBy(r => r.FilenameAndPath, StringComparer.Ordinal).ToList();

        var (runner, opens, _) = RunScan(workers);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Empty(opens);
        var rowsAfter = Rows().OrderBy(r => r.FilenameAndPath, StringComparer.Ordinal).ToList();
        Assert.Equal(rowsBefore.Select(r => r.Id), rowsAfter.Select(r => r.Id));
        Assert.Equal(rowsBefore.Select(r => r.LastScanDateUTC), rowsAfter.Select(r => r.LastScanDateUTC));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void PathWithTwoStaleRowsFromAnOldDatabaseState_EndsWithOneRowWithTheCurrentHash(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var original = Single(b);
        // The state that the old code produced: a second row for the path, with an older size, another hash and a later scan date than the first row
        var extra = RowsPerPathSupport.Row(b, _treeDir, "STALEHASH", 150, s_t0.AddDays(-3), original.LastScanDateUTC.AddHours(1), Environment.MachineName);
        RowsPerPathSupport.Insert(_dbPath, extra);
        Assert.Equal(2, RowsPerPathSupport.RowsOfPath(_dbPath, b).Count);
        byte[] newContent = Content(97, 200);
        File.WriteAllBytes(b, newContent);
        File.SetLastWriteTimeUtc(b, s_t0.AddDays(2));

        var (runner, opens, _) = RunScan(workers);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(new[] { b }, opens.ToArray());
        Assert.Equal(3, Rows().Count);
        var bRow = Single(b);
        // The row that is kept and updated is the one with the latest LastScanDateUTC (here the extra row)
        Assert.Equal(extra.Id, bRow.Id);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(newContent), bRow.FileSha512Hash);
        Assert.Equal(200, bRow.FileSize);
        Assert.Equal(s_t0.AddDays(2).Ticks, bRow.FileLastModificationUTC.Ticks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void PathWithAStaleRowNextToTheCurrentRow_LosesTheStaleRow_WithoutHashingTheFile(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var original = Single(b);
        // The stale row has the LATER scan date, but the row that matches the file (size and time) is the one that stays
        var stale = RowsPerPathSupport.Row(b, _treeDir, "STALEHASH", 150, s_t0.AddDays(-3), original.LastScanDateUTC.AddHours(1), Environment.MachineName);
        RowsPerPathSupport.Insert(_dbPath, stale);

        var (runner, opens, _) = RunScan(workers);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Empty(opens);
        Assert.Equal(3, Rows().Count);
        var bRow = Single(b);
        Assert.Equal(original.Id, bRow.Id);
        Assert.Equal(original.FileSha512Hash, bRow.FileSha512Hash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void RowOfAnotherScanExecutionComputer_IsLeftAlone_AndTheFileIsInsertedForThisComputer(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var original = Single(a);
        // The resume identity is path + ScanExecutionComputer: the row of another computer is not "this file" for this scan
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ScanItems SET ScanExecutionComputer = 'OtherPC' WHERE FilenameAndPath = $path";
            command.Parameters.AddWithValue("$path", a);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();
        byte[] newContent = Content(96, 100);
        File.WriteAllBytes(a, newContent);
        File.SetLastWriteTimeUtc(a, s_t0.AddDays(1));

        var (runner, opens, _) = RunScan(workers);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(new[] { a }, opens.ToArray());
        Assert.Equal(4, Rows().Count);
        var rowsOfA = RowsPerPathSupport.RowsOfPath(_dbPath, a);
        Assert.Equal(2, rowsOfA.Count);
        var other = Assert.Single(rowsOfA, r => r.ScanExecutionComputer == "OtherPC");
        Assert.Equal(original.Id, other.Id);
        Assert.Equal(original.FileSha512Hash, other.FileSha512Hash);
        var mine = Assert.Single(rowsOfA, r => r.ScanExecutionComputer == Environment.MachineName);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(newContent), mine.FileSha512Hash);
    }

    // ---- TrustDirectoryMetadata: the decision is made with the values of the listing ----

    private FakeFileEnumerationSource Listing(string changedPath, long changedLength, DateTime changedTime)
    {
        var source = new FakeFileEnumerationSource();
        foreach (var row in Rows().OrderBy(r => r.FilenameAndPath, StringComparer.Ordinal))
        {
            bool isChanged = row.FilenameAndPath == changedPath;
            source.Files.Add(FakeFileEnumerationSource.Entry(row.FilenameAndPath, isChanged ? changedLength : row.FileSize, isChanged ? changedTime : row.FileLastModificationUTC));
        }
        return source;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TrustDirectoryMetadata_AListingWithANewModificationTime_ReHashesAndUpdatesTheRow(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var rowsBefore = Rows().ToDictionary(r => r.FilenameAndPath);
        byte[] newContent = Content(95, 200);
        File.WriteAllBytes(b, newContent);
        DateTime newTime = s_t0.AddDays(7);
        var listing = Listing(b, 200, newTime);

        var (runner, opens, _) = RunScan(workers, trust: true, source: listing);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(new[] { b }, opens.ToArray());
        Assert.Equal(3, Rows().Count);
        var bRow = Single(b);
        Assert.Equal(rowsBefore[b].Id, bRow.Id);
        Assert.Equal(RowsPerPathSupport.Sha512Hex(newContent), bRow.FileSha512Hash);
        // The time of the row is the one of the listing (no call per file)
        Assert.Equal(newTime.Ticks, bRow.FileLastModificationUTC.Ticks);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void TrustDirectoryMetadata_AListingThatShowsTheOldValues_SkipsTheFile_EvenWhenItChanged(int workers)
    {
        var (a, b, c) = CreateTree();
        RunScan(workers);
        var rowsBefore = Rows().ToDictionary(r => r.FilenameAndPath);
        File.WriteAllBytes(b, Content(94, 200));
        File.SetLastWriteTimeUtc(b, s_t0.AddDays(3));
        // The listing is stale (it still shows what the row says): with TrustDirectoryMetadata that is what counts, the same as in refresh and check
        var listing = Listing(b, 200, s_t0);

        var (runner, opens, _) = RunScan(workers, trust: true, source: listing);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Empty(opens);
        Assert.Equal(rowsBefore[b].FileSha512Hash, Single(b).FileSha512Hash);
    }

    // ---- cancel and restart ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void CancelDuringAResumedScan_AndRestart_EndsInTheSameDatabaseAsAnUninterruptedResumedScan(int workers)
    {
        const int FileCount = 14;
        string secondDb = Path.Combine(_tempDir, "db", "second.db");
        var paths = new List<string>();
        for (int i = 0; i < FileCount; i++)
        {
            paths.Add(Write(Path.Combine(i % 2 == 0 ? "even" : "odd", $"f{i:D2}.bin"), Content(100 + i, 60 + i), s_t0));
        }
        RunScan(workers, databaseFile: _dbPath);
        RunScan(workers, databaseFile: secondDb);

        // The same changes for both databases: 8 files get new content (same size, new time or other size), 4 of them also a stale second row from an old state
        for (int i = 0; i < 8; i++)
        {
            File.WriteAllBytes(paths[i], Content(500 + i, 60 + i + (i % 2 == 0 ? 0 : 3)));
            File.SetLastWriteTimeUtc(paths[i], s_t0.AddMinutes(10 + i));
        }
        foreach (string database in new[] { _dbPath, secondDb })
        {
            var existing = RowsPerPathSupport.ReadRows(database);
            var extras = new List<ScanItemDto>();
            for (int i = 0; i < 4; i++)
            {
                var row = existing.Single(r => r.FilenameAndPath == paths[i]);
                extras.Add(RowsPerPathSupport.Row(paths[i], _treeDir, "STALE" + i, 5, s_t0.AddDays(-1), row.LastScanDateUTC.AddMinutes(1 + i), Environment.MachineName));
            }
            RowsPerPathSupport.Insert(database, extras.ToArray());
        }

        // Reference: one uninterrupted resumed scan
        Assert.Equal(IService.EServiceState.finished, RunScan(workers, databaseFile: _dbPath).Runner.RunnerState);

        // The other database: cancelled while the second changed file is opened, then started again
        using var cts = new CancellationTokenSource();
        int openCount = 0;
        var hasher = new FileHasher { BeforeOpen = p => { if (Interlocked.Increment(ref openCount) == 2) { cts.Cancel(); } } };
        RunInterrupted(workers, secondDb, cts.Token, hasher);
        Assert.True(cts.IsCancellationRequested);
        var restarted = RunScan(workers, databaseFile: secondDb);
        Assert.Equal(IService.EServiceState.finished, restarted.Runner.RunnerState);

        var reference = PipelineTestSupport.ReadSnapshot(_dbPath);
        var result = PipelineTestSupport.ReadSnapshot(secondDb);
        Assert.Equal(reference.Rows, result.Rows);
        Assert.Equal(reference.Errors, result.Errors);
        Assert.Equal(FileCount, result.Rows.Count);
        Assert.Equal(FileCount, result.Rows.Select(r => r.FilenameAndPath).Distinct().Count());
        // The changed files have their new hash
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(RowsPerPathSupport.Sha512Hex(File.ReadAllBytes(paths[i])), result.Rows.Single(r => r.FilenameAndPath == paths[i]).Hash);
        }
    }

    private void RunInterrupted(int workers, string databaseFile, CancellationToken token, FileHasher hasher)
    {
        using var serviceProvider = PipelineTestSupport.BuildServices();
        var job = new MyDupFinderScanJobDTO
        {
            JobName = "ResumeChange",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _treeDir,
            DatabaseFile = databaseFile,
            ReportPath = _tempDir,
            HashWorkers = workers,
        };
        var runner = new ScanRunner(job, serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, null, hasher);
        if (workers > 1)
        {
            PipelineTestSupport.StartGuarded(runner, token);
        }
        else
        {
            runner.Start(token);
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
    }
}
