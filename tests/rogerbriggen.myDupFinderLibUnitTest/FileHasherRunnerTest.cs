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
/// The runners with the shared file hasher: a cancel inside a big file, a file that changes while it is read (scan, refresh, check), and the
/// files that must not be opened at all. The hasher reads in blocks of 1 KB here so that every file has several blocks, and its test hook changes
/// the file between two blocks at an exact place (with FileShare.ReadWrite: with the default FileShare.Read Windows would refuse the writer).
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
        File.Delete(report);
        return (runner, rows.ToArray());
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

    private static FileHasher MutatingHasher(string target, FileMutation mutation, bool onlyFirstAttempt, int block = 2) =>
        new FileHasher(Block, FileShare.ReadWrite)
        {
            AfterBufferRead = (path, attempt, blockNumber) =>
            {
                if (blockNumber == block && string.Equals(path, target, StringComparison.Ordinal) && (!onlyFirstAttempt || attempt == 1))
                {
                    FileMutator.Apply(target, mutation, attempt);
                }
            },
        };

    private static FileHasher CancellingHasher(string target, CancellationTokenSource cts) =>
        new FileHasher(Block)
        {
            AfterBufferRead = (path, attempt, blockNumber) =>
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

        public FileHasher Hasher() => new FileHasher(Block) { BeforeAttempt = (path, attempt) => Interlocked.Increment(ref Opens) };
    }

    private static string Reference(string path) => ReferenceFileHash.Compute(path);

    private static void AssertWarningAbout(ListLoggerProvider log, string file) =>
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(file, StringComparison.Ordinal) && e.Message.Contains("changed while it was read", StringComparison.Ordinal));

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
        Assert.Equal(0, runner.UnstableCount);
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
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Scan_FileChangesDuringBothAttempts_StoresNoItem_ButAnErrorItem_AndContinues(FileMutation mutation)
    {
        List<string> files = MakeTree();
        string target = files[2];
        var log = new ListLoggerProvider();

        var runner = RunScan(MutatingHasher(target, mutation, onlyFirstAttempt: false), log: log);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(1, runner.UnstableCount);
        var rows = ReadItems();
        Assert.Equal(FileCount - 1, rows.Count);
        Assert.DoesNotContain(rows, r => r.FilenameAndPath == target);
        var error = Assert.Single(ReadErrorItems());
        Assert.Equal(target, error.FilenameAndPath);
        Assert.Contains("changed while it was read", error.MyException, StringComparison.Ordinal);
        AssertWarningAbout(log, target);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Scanning finished", StringComparison.Ordinal) && e.Message.Contains("UnstableFiles=1", StringComparison.Ordinal));
        foreach (string file in files.Where(f => f != target))
        {
            Assert.Equal(Reference(file), rows.Single(r => r.FilenameAndPath == file).FileSha512Hash);
        }
    }

    [Theory]
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Scan_FileChangesOnlyDuringTheFirstAttempt_StoresTheFinalHashWithTheFinalSizeAndTime(FileMutation mutation)
    {
        List<string> files = MakeTree();
        string target = files[2];

        var runner = RunScan(MutatingHasher(target, mutation, onlyFirstAttempt: true));

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(0, runner.UnstableCount);
        Assert.Empty(ReadErrorItems());
        var row = ReadItems().Single(r => r.FilenameAndPath == target);
        Assert.Equal(Reference(target), row.FileSha512Hash);
        Assert.Equal(new FileInfo(target).Length, row.FileSize);
        Assert.Equal(File.GetLastWriteTimeUtc(target).Ticks, row.FileLastModificationUTC.Ticks);
        // The metadata that was read before the hash had the old time: the stored time is the one of the file that was hashed
        Assert.NotEqual(OldTime.Ticks, row.FileLastModificationUTC.Ticks);
    }

    [Fact]
    public void Scan_TrustDirectoryMetadata_KeepsTheListedSizeAndTime_ButStoresTheHashOfTheFinalContent()
    {
        List<string> files = MakeTree();
        string target = files[2];
        long listedSize = new FileInfo(target).Length;

        var runner = RunScan(MutatingHasher(target, FileMutation.Append, onlyFirstAttempt: true), trust: true);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        var row = ReadItems().Single(r => r.FilenameAndPath == target);
        Assert.Equal(Reference(target), row.FileSha512Hash);
        // Documented difference: with TrustDirectoryMetadata the listing values are stored (the next refresh compares the listing with the database)
        Assert.Equal(listedSize, row.FileSize);
        Assert.Equal(OldTime.Ticks, row.FileLastModificationUTC.Ticks);
        Assert.NotEqual(listedSize, new FileInfo(target).Length);
    }

    [Fact]
    public void Scan_TrustDirectoryMetadata_StillDetectsAnUnstableFile()
    {
        List<string> files = MakeTree();
        string target = files[2];

        var runner = RunScan(MutatingHasher(target, FileMutation.Append, onlyFirstAttempt: false), trust: true);

        Assert.Equal(1, runner.UnstableCount);
        Assert.DoesNotContain(ReadItems(), r => r.FilenameAndPath == target);
        Assert.Single(ReadErrorItems());
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
    public void Scan_AStableFile_IsOpenedExactlyOnce()
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
        Assert.Equal(0, runner.UnstableCount);
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
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Refresh_ExistingRowWhoseFileChangesDuringBothAttempts_StaysExactlyAsItWas(FileMutation mutation)
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        File.WriteAllBytes(target, RandomBytes(9 * Block, 31));
        var before = ReadItems().ToDictionary(r => r.FilenameAndPath, StringComparer.Ordinal);
        var log = new ListLoggerProvider();

        var runner = RunRefresh(MutatingHasher(target, mutation, onlyFirstAttempt: false), log: log);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(1, runner.ErrorCount);
        Assert.Equal(1, runner.UnstableCount);
        Assert.Equal(0, runner.UpdatedCount);
        Assert.Equal(0, runner.AddedCount);
        Assert.Equal(0, runner.RemovedCount);
        Assert.Equal(FileCount - 1, runner.UnchangedCount);
        var after = ReadItems().ToDictionary(r => r.FilenameAndPath, StringComparer.Ordinal);
        Assert.Equal(FileCount, after.Count);
        Assert.Equal(Snapshot(before[target]), Snapshot(after[target]));
        AssertWarningAbout(log, target);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Refresh finished.", StringComparison.Ordinal) && e.Message.Contains("Unstable=1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Refresh_NewFileThatChangesDuringBothAttempts_IsNotAdded(FileMutation mutation)
    {
        MakeScannedTree();
        string added = Path.Combine(_treeDir, "added.bin");
        File.WriteAllBytes(added, RandomBytes(9 * Block, 41));
        var log = new ListLoggerProvider();

        var runner = RunRefresh(MutatingHasher(added, mutation, onlyFirstAttempt: false), log: log);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(0, runner.AddedCount);
        Assert.Equal(1, runner.ErrorCount);
        Assert.Equal(1, runner.UnstableCount);
        Assert.Equal(FileCount, runner.UnchangedCount);
        Assert.DoesNotContain(ReadItems(), r => r.FilenameAndPath == added);
        AssertWarningAbout(log, added);
    }

    [Theory]
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Refresh_FileChangesOnlyDuringTheFirstAttempt_UpdatesWithTheFinalHashSizeAndTime(FileMutation mutation)
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        File.WriteAllBytes(target, RandomBytes(9 * Block, 32));

        var runner = RunRefresh(MutatingHasher(target, mutation, onlyFirstAttempt: true));

        Assert.Equal(1, runner.UpdatedCount);
        Assert.Equal(0, runner.ErrorCount);
        Assert.Equal(0, runner.UnstableCount);
        var row = ReadItems().Single(r => r.FilenameAndPath == target);
        Assert.Equal(Reference(target), row.FileSha512Hash);
        Assert.Equal(new FileInfo(target).Length, row.FileSize);
        Assert.Equal(File.GetLastWriteTimeUtc(target).Ticks, row.FileLastModificationUTC.Ticks);
    }

    [Fact]
    public void Refresh_NewFileThatChangesDuringTheFirstAttempt_IsAddedWithTheFinalValues_AndItsCreationTimeAsRead()
    {
        MakeScannedTree();
        string added = Path.Combine(_treeDir, "added.bin");
        File.WriteAllBytes(added, RandomBytes(9 * Block, 42));
        File.SetCreationTimeUtc(added, new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc).AddTicks(99));

        var runner = RunRefresh(MutatingHasher(added, FileMutation.Append, onlyFirstAttempt: true));

        Assert.Equal(1, runner.AddedCount);
        var row = ReadItems().Single(r => r.FilenameAndPath == added);
        Assert.Equal(Reference(added), row.FileSha512Hash);
        Assert.Equal(new FileInfo(added).Length, row.FileSize);
        Assert.Equal(File.GetLastWriteTimeUtc(added).Ticks, row.FileLastModificationUTC.Ticks);
        Assert.Equal(new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc).AddTicks(99).Ticks, row.FileCreationUTC.Ticks);
    }

    [Fact]
    public void Refresh_TrustDirectoryMetadata_StoresTheListedValuesWithTheHashOfTheFinalContent()
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        File.WriteAllBytes(target, RandomBytes(9 * Block, 33));
        File.SetLastWriteTimeUtc(target, OldTime.AddDays(1));
        long listedSize = new FileInfo(target).Length;

        var runner = RunRefresh(MutatingHasher(target, FileMutation.Append, onlyFirstAttempt: true), trust: true);

        Assert.Equal(1, runner.UpdatedCount);
        var row = ReadItems().Single(r => r.FilenameAndPath == target);
        Assert.Equal(Reference(target), row.FileSha512Hash);
        Assert.Equal(listedSize, row.FileSize);
        Assert.Equal(OldTime.AddDays(1).Ticks, row.FileLastModificationUTC.Ticks);
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
        Assert.Equal(0, runner.UnstableCount);
        Assert.Empty(rows);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Check was cancelled by user", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Check_FileChangesDuringBothAttempts_WritesNoRow_DoesNotReportItAsMissing_AndCountsIt(FileMutation mutation)
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        var log = new ListLoggerProvider();

        var (runner, rows) = RunCheck(MutatingHasher(target, mutation, onlyFirstAttempt: false), log: log);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Empty(rows);
        Assert.Equal(0, runner.MissingOnDiskCount);
        Assert.Equal(0, runner.NewOnDiskCount);
        Assert.Equal(1, runner.ErrorCount);
        Assert.Equal(1, runner.UnstableCount);
        Assert.Equal(FileCount - 1, runner.OkCount);
        AssertWarningAbout(log, target);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Check finished.", StringComparison.Ordinal) && e.Message.Contains("Unstable=1", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_NewFileThatChangesDuringBothAttempts_WritesNoRow()
    {
        MakeScannedTree();
        string added = Path.Combine(_treeDir, "added.bin");
        File.WriteAllBytes(added, RandomBytes(9 * Block, 51));
        var log = new ListLoggerProvider();

        var (runner, rows) = RunCheck(MutatingHasher(added, FileMutation.Append, onlyFirstAttempt: false), log: log);

        Assert.Empty(rows);
        Assert.Equal(0, runner.NewOnDiskCount);
        Assert.Equal(1, runner.ErrorCount);
        Assert.Equal(1, runner.UnstableCount);
        Assert.Equal(FileCount, runner.OkCount);
        AssertWarningAbout(log, added);
    }

    [Fact]
    public void Check_FileThatGrowsDuringTheFirstAttempt_ReportsTheFinalHashSizeAndTime()
    {
        List<string> files = MakeScannedTree();
        string target = files[3];

        var (runner, rows) = RunCheck(MutatingHasher(target, FileMutation.Append, onlyFirstAttempt: true));

        Assert.Equal(0, runner.ErrorCount);
        var row = Assert.Single(rows);
        Assert.Equal(CheckCategory.Modified, row.Category);
        Assert.Equal(Reference(target), row.HashDisk);
        Assert.Equal(new FileInfo(target).Length, row.FileSizeDisk);
        Assert.Equal(File.GetLastWriteTimeUtc(target).Ticks, row.MTimeDisk!.Value.Ticks);
    }

    [Fact]
    public void Check_FileWhoseTimeChangesDuringTheFirstAttempt_ReportsTheNewTime_WithTheSameHash()
    {
        List<string> files = MakeScannedTree();
        string target = files[3];

        var (runner, rows) = RunCheck(MutatingHasher(target, FileMutation.Touch, onlyFirstAttempt: true));

        Assert.Equal(0, runner.ErrorCount);
        var row = Assert.Single(rows);
        Assert.Equal(CheckCategory.ModifiedNoHashChange, row.Category);
        Assert.Equal(File.GetLastWriteTimeUtc(target).Ticks, row.MTimeDisk!.Value.Ticks);
        Assert.NotEqual(OldTime.Ticks, row.MTimeDisk!.Value.Ticks);
    }

    [Fact]
    public void Check_TrustDirectoryMetadata_ReportsTheListedValues_WithTheHashOfTheFinalContent()
    {
        List<string> files = MakeScannedTree();
        string target = files[3];
        long listedSize = new FileInfo(target).Length;

        var (runner, rows) = RunCheck(MutatingHasher(target, FileMutation.Append, onlyFirstAttempt: true), trust: true);

        Assert.Equal(0, runner.ErrorCount);
        var row = Assert.Single(rows);
        // The listing (and the database) say nothing changed, only the hash differs
        Assert.Equal(CheckCategory.BitRotSuspect, row.Category);
        Assert.Equal(Reference(target), row.HashDisk);
        Assert.Equal(listedSize, row.FileSizeDisk);
        Assert.Equal(OldTime.Ticks, row.MTimeDisk!.Value.Ticks);
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
