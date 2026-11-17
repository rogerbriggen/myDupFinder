// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The runners with the shared file hasher: a cancel inside a big file, the size and time that are stored or reported with a hash (the values read before
/// hashing), and the files that must not be opened at all. The hasher reads in blocks of 1 KB here so that every file has several blocks, and its test hook
/// changes the file after the runner read its metadata and before the hasher opens it.
/// </summary>
public class FileHasherRunnerTest : IDisposable
{
    private const int Block = 1024;
    private const int FileCount = 6;
    private static readonly DateTime OldTime = new DateTime(2015, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddTicks(123456);

    private readonly string _tempDir;
    private readonly string _treeDir;
    private readonly string _dbPath;
    private readonly string _reportDir;

    public FileHasherRunnerTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FileHasherRunnerTest_" + Guid.NewGuid().ToString("N"));
        _treeDir = Path.Combine(_tempDir, "tree");
        _dbPath = Path.Combine(_tempDir, "db", "scan.db");
        _reportDir = Path.Combine(_tempDir, "reports");
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

    // ---- helpers ----

    private static ServiceProvider BuildServices(ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        return services.BuildServiceProvider();
    }

    private MyDupFinderScanJobDTO ScanJob(bool trust = false) => new MyDupFinderScanJobDTO
    {
        JobName = "HasherTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _treeDir,
        DatabaseFile = _dbPath,
        ReportPath = _reportDir,
        TrustDirectoryMetadata = trust,
    };

    private ScanRunner RunScan(FileHasher? hasher, bool trust = false, ListLoggerProvider? log = null, CancellationToken token = default)
    {
        using var serviceProvider = BuildServices(log);
        var runner = new ScanRunner(ScanJob(trust), serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, null, hasher);
        runner.Start(token);
        SqliteConnection.ClearAllPools();
        return runner;
    }

    private RefreshRunner RunRefresh(FileHasher? hasher, bool trust = false, ListLoggerProvider? log = null, CancellationToken token = default)
    {
        using var serviceProvider = BuildServices(log);
        var job = new MyDupFinderRefreshJobDTO
        {
            JobName = "HasherTest",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _treeDir,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
            TrustDirectoryMetadata = trust,
        };
        var runner = new RefreshRunner(job, serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider, null, null, hasher);
        runner.Start(token);
        SqliteConnection.ClearAllPools();
        return runner;
    }

    private (CheckRunner Runner, CheckReportRow[] Rows) RunCheck(FileHasher? hasher, bool trust = false, bool skipHash = false, ListLoggerProvider? log = null, CancellationToken token = default)
    {
        using var serviceProvider = BuildServices(log);
        var job = new MyDupFinderCheckJobDTO { ScanJobDTO = ScanJob(trust), SkipHashCheck = skipHash };
        var runner = new CheckRunner(job, serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider, null, hasher);
        runner.Start(token);
        SqliteConnection.ClearAllPools();
        string report = Directory.GetFiles(_reportDir, "check-*.csv").Single();
        var (_, rows) = CheckReportCsv.Read(report);
        var rowArray = rows.ToArray();
        File.Delete(report);
        return (runner, rowArray);
    }

    private List<ScanItemDto> ReadItems()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanItems!.AsNoTracking().ToList();
    }

    private List<ScanErrorItemDto> ReadErrorItems()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanErrorItems!.AsNoTracking().ToList();
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Generates the tree (all files several blocks long, all with the same old last write time) and returns the files in enumeration order.</summary>
    private List<string> MakeTree()
    {
        TestTreeGenerator.Generate(_treeDir, new TestTreeOptions
        {
            FileCount = FileCount,
            DuplicateShare = 0,
            DirectoryCount = 2,
            SizeBuckets = new[] { new SizeBucket(1.0, 8 * Block, 12 * Block) },
        });
        var list = new List<string>();
        using (var enumeration = new FileTreeEnumeration(_treeDir))
        {
            while (enumeration.MoveNext())
            {
                list.Add(enumeration.Current.FullPath);
            }
        }
        foreach (string file in list)
        {
            File.SetLastWriteTimeUtc(file, OldTime);
        }
        return list;
    }

    private List<string> MakeScannedTree()
    {
        List<string> files = MakeTree();
        Assert.Equal(IService.EServiceState.finished, RunScan(null).RunnerState);
        Assert.Equal(FileCount, ReadItems().Count);
        return files;
    }

    /// <summary>A hasher that appends bytes to the target right before it opens it, i.e. after the runner has read the metadata of the file.</summary>
    private static FileHasher AppendingHasher(string target) =>
        new FileHasher(Block)
        {
            BeforeOpen = path =>
            {
                if (string.Equals(path, target, StringComparison.Ordinal))
                {
                    FileMutator.Append(target, 1);
                }
            },
        };

    private static FileHasher CancellingHasher(string target, CancellationTokenSource cts) =>
        new FileHasher(Block)
        {
            AfterBufferRead = (path, blockNumber) =>
            {
                if (blockNumber == 2 && string.Equals(path, target, StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            },
        };

    private sealed class OpenCounter
    {
        public int Opens;

        public FileHasher Hasher() => new FileHasher(Block) { BeforeOpen = path => Interlocked.Increment(ref Opens) };
    }

    private static string Reference(string path) => ReferenceFileHash.Compute(path);

    // ================= scan =================

    [Fact]
    public void Scan_CancelInsideAMultiBufferFile_EndsAsCancelled_NothingStoredForThatFile_NoError()
    {
        List<string> files = MakeTree();
        string target = files[3];
        using var cts = new CancellationTokenSource();
        var log = new ListLoggerProvider();

        var runner = RunScan(CancellingHasher(target, cts), log: log, token: cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        // The three files before it were flushed by the cancel (the default batch of 500 never filled), nothing for the cancelled one
        Assert.Equal(files.Take(3).OrderBy(f => f, StringComparer.Ordinal), ReadItems().Select(r => r.FilenameAndPath).OrderBy(f => f, StringComparer.Ordinal));
        Assert.Empty(ReadErrorItems());
        Assert.Equal(3, runner.Throughput.Files);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Scanning was cancelled by user", StringComparison.Ordinal));

        // A new run completes the scan, the cancelled file gets its correct hash
        Assert.Equal(IService.EServiceState.finished, RunScan(null).RunnerState);
        var rows = ReadItems();
        Assert.Equal(FileCount, rows.Count);
        Assert.Equal(Reference(target), rows.Single(r => r.FilenameAndPath == target).FileSha512Hash);
        Assert.Empty(ReadErrorItems());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_StoresTheSizeAndTimeThatWereReadBeforeHashing_NotAnythingFromTheHasher(bool trust)
    {
        List<string> files = MakeTree();
        string target = files[2];
        long sizeBefore = new FileInfo(target).Length;

        var runner = RunScan(AppendingHasher(target), trust: trust);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.NotEqual(sizeBefore, new FileInfo(target).Length);
        var row = ReadItems().Single(r => r.FilenameAndPath == target);
        Assert.Equal(Reference(target), row.FileSha512Hash);
        Assert.Equal(sizeBefore, row.FileSize);
        Assert.Equal(OldTime.Ticks, row.FileLastModificationUTC.Ticks);
    }

    [Fact]
    public void Scan_Resume_DoesNotOpenFilesThatAreAlreadyInTheDatabase()
    {
        MakeScannedTree();
        var counter = new OpenCounter();

        var runner = RunScan(counter.Hasher());

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(0, counter.Opens);
        Assert.Equal(FileCount, runner.Throughput.Files);
    }

    [Fact]
    public void Scan_EachFile_IsOpenedExactlyOnce()
    {
        MakeTree();
        var counter = new OpenCounter();

        RunScan(counter.Hasher());

        Assert.Equal(FileCount, counter.Opens);
    }

    // ================= refresh =================

    private static (Guid Id, long Size, long MTime, string Hash, long LastScan, long LastHash, long Created) Snapshot(ScanItemDto row) =>
        (row.Id, row.FileSize, row.FileLastModificationUTC.Ticks, row.FileSha512Hash, row.LastScanDateUTC.Ticks, row.LastSha512ScanDateUTC.Ticks, row.FileCreationUTC.Ticks);

    [Fact]
    public void Refresh_CancelInsideAMultiBufferFile_EndsAsCancelled_KeepsEarlierUpdates_NoErrorForThatFile()
    {
        List<string> files = MakeScannedTree();
        string first = files[1];
        string second = files[4];
        File.WriteAllBytes(first, RandomBytes(9 * Block, 21));
        File.WriteAllBytes(second, RandomBytes(9 * Block, 22));
        var before = ReadItems().ToDictionary(r => r.FilenameAndPath, StringComparer.Ordinal);
        using var cts = new CancellationTokenSource();
        var log = new ListLoggerProvider();

        var runner = RunRefresh(CancellingHasher(second, cts), log: log, token: cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Equal(1, runner.UpdatedCount);
        Assert.Equal(0, runner.ErrorCount);
        Assert.Equal(0, runner.RemovedCount);
        var after = ReadItems().ToDictionary(r => r.FilenameAndPath, StringComparer.Ordinal);
        Assert.Equal(FileCount, after.Count);
        Assert.Equal(Reference(first), after[first].FileSha512Hash);
        Assert.Equal(Snapshot(before[second]), Snapshot(after[second]));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);

        // A new refresh finishes the job
        var next = RunRefresh(null);
        Assert.Equal(IService.EServiceState.finished, next.RunnerState);
        Assert.Equal(1, next.UpdatedCount);
        Assert.Equal(Reference(second), ReadItems().Single(r => r.FilenameAndPath == second).FileSha512Hash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_UpdatesTheRowWithTheSizeAndTimeThatWereReadBeforeHashing_NotAnythingFromTheHasher(bool trust)
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        File.WriteAllBytes(target, RandomBytes(9 * Block, 33));
        File.SetLastWriteTimeUtc(target, OldTime.AddDays(1));
        long sizeBefore = new FileInfo(target).Length;

        var runner = RunRefresh(AppendingHasher(target), trust: trust);

        Assert.Equal(1, runner.UpdatedCount);
        Assert.NotEqual(sizeBefore, new FileInfo(target).Length);
        var row = ReadItems().Single(r => r.FilenameAndPath == target);
        Assert.Equal(Reference(target), row.FileSha512Hash);
        Assert.Equal(sizeBefore, row.FileSize);
        Assert.Equal(OldTime.AddDays(1).Ticks, row.FileLastModificationUTC.Ticks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_AddsANewFileWithTheSizeAndTimeThatWereReadBeforeHashing_AndItsCreationTime(bool trust)
    {
        MakeScannedTree();
        string added = Path.Combine(_treeDir, "added.bin");
        File.WriteAllBytes(added, RandomBytes(9 * Block, 42));
        File.SetCreationTimeUtc(added, new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc).AddTicks(99));
        File.SetLastWriteTimeUtc(added, OldTime.AddDays(2));
        long sizeBefore = new FileInfo(added).Length;

        var runner = RunRefresh(AppendingHasher(added), trust: trust);

        Assert.Equal(1, runner.AddedCount);
        var row = ReadItems().Single(r => r.FilenameAndPath == added);
        Assert.Equal(Reference(added), row.FileSha512Hash);
        Assert.Equal(sizeBefore, row.FileSize);
        Assert.Equal(OldTime.AddDays(2).Ticks, row.FileLastModificationUTC.Ticks);
        Assert.Equal(new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc).AddTicks(99).Ticks, row.FileCreationUTC.Ticks);
    }

    [Fact]
    public void Refresh_UntouchedTree_DoesNotOpenAnyFile()
    {
        MakeScannedTree();
        var counter = new OpenCounter();

        var runner = RunRefresh(counter.Hasher());

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(FileCount, runner.UnchangedCount);
        Assert.Equal(0, counter.Opens);
        Assert.Equal(0, runner.Throughput.BytesHashed);
    }

    [Fact]
    public void Refresh_ChangedFile_IsOpenedExactlyOnce()
    {
        List<string> files = MakeScannedTree();
        File.WriteAllBytes(files[0], RandomBytes(9 * Block, 34));
        var counter = new OpenCounter();

        RunRefresh(counter.Hasher());

        Assert.Equal(1, counter.Opens);
    }

    // ================= check =================

    [Fact]
    public void Check_CancelInsideAMultiBufferFile_EndsAsCancelled_NoRowAndNoErrorForThatFile()
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        using var cts = new CancellationTokenSource();
        var log = new ListLoggerProvider();

        var (runner, rows) = RunCheck(CancellingHasher(target, cts), log: log, token: cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Equal(3, runner.OkCount);
        Assert.Equal(0, runner.ErrorCount);
        Assert.Empty(rows);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Check was cancelled by user", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Check_ComparesAndReportsTheSizeAndTimeThatWereReadBeforeHashing_NotAnythingFromTheHasher(bool trust)
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        long sizeBefore = new FileInfo(target).Length;

        var (runner, rows) = RunCheck(AppendingHasher(target), trust: trust);

        Assert.Equal(0, runner.ErrorCount);
        Assert.NotEqual(sizeBefore, new FileInfo(target).Length);
        var row = Assert.Single(rows);
        // Size and time before hashing equal the database, only the hash differs
        Assert.Equal(CheckCategory.BitRotSuspect, row.Category);
        Assert.Equal(Reference(target), row.HashDisk);
        Assert.Equal(sizeBefore, row.FileSizeDisk);
        Assert.Equal(OldTime.Ticks, row.MTimeDisk!.Value.Ticks);
    }

    [Fact]
    public void Check_NewFile_ReportsTheSizeAndTimeThatWereReadBeforeHashing_WithTheHash()
    {
        MakeScannedTree();
        string added = Path.Combine(_treeDir, "added.bin");
        File.WriteAllBytes(added, RandomBytes(9 * Block, 51));
        File.SetLastWriteTimeUtc(added, OldTime.AddDays(3));
        long sizeBefore = new FileInfo(added).Length;

        var (runner, rows) = RunCheck(AppendingHasher(added));

        Assert.Equal(1, runner.NewOnDiskCount);
        var row = Assert.Single(rows);
        Assert.Equal(CheckCategory.NewOnDisk, row.Category);
        Assert.Equal(Reference(added), row.HashDisk);
        Assert.Equal(sizeBefore, row.FileSizeDisk);
        Assert.Equal(OldTime.AddDays(3).Ticks, row.MTimeDisk!.Value.Ticks);
    }

    [Fact]
    public void Check_SkipHashCheck_DoesNotOpenAnyFile()
    {
        MakeScannedTree();
        var counter = new OpenCounter();

        var (runner, rows) = RunCheck(counter.Hasher(), skipHash: true);

        Assert.Equal(FileCount, runner.OkCount);
        Assert.Empty(rows);
        Assert.Equal(0, counter.Opens);
    }

    [Fact]
    public void Check_WithHashing_OpensEveryFileExactlyOnce()
    {
        MakeScannedTree();
        var counter = new OpenCounter();

        var (runner, rows) = RunCheck(counter.Hasher());

        Assert.Equal(FileCount, runner.OkCount);
        Assert.Empty(rows);
        Assert.Equal(FileCount, counter.Opens);
    }
}
