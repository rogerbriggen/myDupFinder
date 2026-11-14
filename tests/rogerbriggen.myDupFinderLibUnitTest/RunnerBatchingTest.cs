// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
/// Cancels a token when a log message that contains the marker was written the given number of times.
/// The runners log from the thread that runs the job, so this cancels at an exact, repeatable place.
/// </summary>
internal sealed class CancelAfterLogProvider : ILoggerProvider
{
    private readonly string _marker;
    private readonly int _count;
    private readonly CancellationTokenSource _cts;
    private int _seen;

    public CancelAfterLogProvider(string marker, int count, CancellationTokenSource cts)
    {
        _marker = marker;
        _count = count;
        _cts = cts;
    }

    public ILogger CreateLogger(string categoryName) => new CancelLogger(this);

    public void Dispose()
    {
    }

    private sealed class CancelLogger : ILogger
    {
        private readonly CancelAfterLogProvider _provider;

        public CancelLogger(CancelAfterLogProvider provider) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(_provider._marker, StringComparison.Ordinal) && (++_provider._seen == _provider._count))
            {
                _provider._cts.Cancel();
            }
        }
    }
}

/// <summary>
/// Runs the scan and refresh runners against real files and a real SQLite file, with the database
/// batching switched to the interesting cases.
/// </summary>
public class RunnerBatchingTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _treeDir;
    private readonly string _dbPath;

    public RunnerBatchingTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RunnerBatchingTest_" + Guid.NewGuid().ToString("N"));
        _treeDir = Path.Combine(_tempDir, "tree");
        _dbPath = Path.Combine(_tempDir, "db", "scan.db");
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

    private static TestTreeOptions Tree(int fileCount) => new TestTreeOptions
    {
        FileCount = fileCount,
        DuplicateShare = 0.1,
        DirectoryCount = 6,
        SizeBuckets = new[] { new SizeBucket(1.0, 64, 256) },
    };

    private MyDupFinderScanJobDTO ScanJob() => new MyDupFinderScanJobDTO
    {
        JobName = "RunnerBatchingTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _treeDir,
        DatabaseFile = _dbPath,
        ReportPath = _tempDir,
    };

    private MyDupFinderRefreshJobDTO RefreshJob() => new MyDupFinderRefreshJobDTO
    {
        JobName = "RunnerBatchingTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _treeDir,
        DatabaseFile = _dbPath,
        ReportPath = _tempDir,
    };

    private static ServiceProvider BuildServices(ILoggerProvider? loggerProvider, LogLevel minimumLevel)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        return services.BuildServiceProvider();
    }

    private ScanRunnerSingleThread RunScan(ILoggerProvider? loggerProvider = null, LogLevel minimumLevel = LogLevel.Information, CancellationToken token = default, int? batchSize = null)
    {
        using var serviceProvider = BuildServices(loggerProvider, minimumLevel);
        var dbInserts = batchSize is null ? null : new ScanJobDBInserts(serviceProvider.GetService<ILogger<ScanJobDBInserts>>(), batchSize.Value, TimeSpan.FromHours(1));
        var runner = new ScanRunnerSingleThread(ScanJob(), serviceProvider.GetService<ILogger<ScanRunnerSingleThread>>(), serviceProvider, dbInserts);
        runner.Start(token);
        return runner;
    }

    private RefreshRunner RunRefresh(ILoggerProvider? loggerProvider = null, LogLevel minimumLevel = LogLevel.Information, CancellationToken token = default, int? batchSize = null)
    {
        using var serviceProvider = BuildServices(loggerProvider, minimumLevel);
        var dbInserts = batchSize is null ? null : new ScanJobDBInserts(serviceProvider.GetService<ILogger<ScanJobDBInserts>>(), batchSize.Value, TimeSpan.FromHours(1));
        var runner = new RefreshRunner(RefreshJob(), serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider, dbInserts);
        runner.Start(token);
        return runner;
    }

    private List<ScanItemDto> ReadItems()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanItems!.AsNoTracking().ToList();
    }

    private int ReadErrorCount()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanErrorItems!.Count();
    }

    private static string Sha512Hex(string path) => Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Scan_DefaultBatchSize_StoresEveryFile_AlsoWhenCountIsNoMultipleOfTheBatch()
    {
        // 1203 files with the default batch of 500: two full batches and a rest of 203 that only the final flush writes
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(1203));

        var runner = RunScan();

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        var rows = ReadItems();
        Assert.Equal(tree.Files.Count, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.FilenameAndPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, ReadErrorCount());
    }

    [Fact]
    public void Scan_SmallBatchSize_StoresEveryFile()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(103));

        RunScan(batchSize: 10);

        Assert.Equal(tree.Files.Count, ReadItems().Count);
    }

    [Fact]
    public void Scan_CancelledMidRun_KeepsEverythingHashedSoFar_AndRestartCompletes()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(60));
        using var cts = new CancellationTokenSource();
        // Cancel when the 25th file is done. The default batch of 500 means nothing was flushed before the cancel.
        var cancelLog = new CancelAfterLogProvider("successfull finished", 25, cts);

        var runner = RunScan(cancelLog, LogLevel.Trace, cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Equal(25, ReadItems().Count);

        // Start again: the filter skips what is there, the rest is added
        var second = RunScan();

        Assert.Equal(IService.EServiceState.finished, second.RunnerState);
        var rows = ReadItems();
        Assert.Equal(tree.Files.Count, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.FilenameAndPath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Scan_CancelledMidRunWithSmallBatches_KeepsEverythingHashedSoFar()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(60));
        using var cts = new CancellationTokenSource();
        var cancelLog = new CancelAfterLogProvider("successfull finished", 27, cts);

        var runner = RunScan(cancelLog, LogLevel.Trace, cts.Token, batchSize: 10);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Equal(27, ReadItems().Count);

        RunScan(batchSize: 10);
        Assert.Equal(tree.Files.Count, ReadItems().Count);
    }

    [Fact]
    public void Scan_FatalWriteFailure_EndsTheRun_AndIsNotRecordedAsFileError()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(60));
        // Create the database and let SQLite refuse the insert of one file name (a real SQLite error at commit time)
        using (var prepare = new ScanJobDBInserts(null))
        {
            prepare.SetupDB(_dbPath);
        }
        string poisoned = Path.GetFileName(tree.Files[40].RelativePath);
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER poison BEFORE INSERT ON ScanItems WHEN NEW.Filename = '{poisoned}' BEGIN SELECT RAISE(ABORT, 'poisoned'); END;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var log = new ListLoggerProvider();

        var runner = RunScan(log, LogLevel.Information, batchSize: 10);

        // The run ended early: not all files were written, and the failed file is no hashing error
        var rows = ReadItems();
        Assert.True(rows.Count < tree.Files.Count, $"Expected an early end but {rows.Count} of {tree.Files.Count} rows were written");
        Assert.DoesNotContain(rows, r => r.Filename == poisoned);
        Assert.Equal(0, ReadErrorCount());
        // Whole batches only: a failed batch writes nothing
        Assert.Equal(0, rows.Count % 10);
        Assert.NotEqual(IService.EServiceState.finished, runner.RunnerState);
        Assert.NotEqual(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("database write failure", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Hashing of", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Scanning finished", StringComparison.Ordinal));
    }

    [Fact]
    public void Refresh_EndToEnd_UnchangedChangedNewAndDeletedFiles()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(40));
        RunScan();
        string[] files = tree.Files.Select(f => Path.Combine(_treeDir, f.RelativePath)).ToArray();
        // 0-9 changed (content and size), 10-14 deleted, 15-39 unchanged, plus 8 new
        string[] changed = files.Take(10).ToArray();
        string[] deleted = files.Skip(10).Take(5).ToArray();
        string[] unchanged = files.Skip(15).ToArray();
        foreach (string file in changed)
        {
            File.WriteAllBytes(file, Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).Concat(File.ReadAllBytes(file).Take(5)).ToArray());
        }
        foreach (string file in deleted)
        {
            File.Delete(file);
        }
        var added = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            string path = Path.Combine(_treeDir, $"newfile{i}.bin");
            File.WriteAllBytes(path, Enumerable.Range(0, 100 + i).Select(b => (byte)(b + i)).ToArray());
            added.Add(path);
        }
        var before = ReadItems().ToDictionary(r => r.FilenameAndPath, StringComparer.Ordinal);

        var runner = RunRefresh(batchSize: 7);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(10, runner.UpdatedCount);
        Assert.Equal(5, runner.RemovedCount);
        Assert.Equal(25, runner.UnchangedCount);
        Assert.Equal(8, runner.AddedCount);
        Assert.Equal(0, runner.ErrorCount);
        var after = ReadItems().ToDictionary(r => r.FilenameAndPath, StringComparer.Ordinal);
        Assert.Equal(25 + 10 + 8, after.Count);
        foreach (string file in changed)
        {
            Assert.Equal(Sha512Hex(file), after[file].FileSha512Hash);
            Assert.Equal(new FileInfo(file).Length, after[file].FileSize);
            Assert.Equal(before[file].Id, after[file].Id);
        }
        foreach (string file in deleted)
        {
            Assert.False(after.ContainsKey(file));
        }
        foreach (string file in unchanged)
        {
            Assert.Equal(before[file].FileSha512Hash, after[file].FileSha512Hash);
            Assert.True(after[file].LastScanDateUTC > before[file].LastScanDateUTC);
        }
        foreach (string file in added)
        {
            Assert.Equal(Sha512Hex(file), after[file].FileSha512Hash);
        }
    }

    [Fact]
    public void Refresh_CancelledMidRun_KeepsTheAddsAndUpdatesDoneBeforeTheCancel()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(20));
        RunScan();
        string[] files = tree.Files.Select(f => Path.Combine(_treeDir, f.RelativePath)).ToArray();
        string[] changed = files.Take(6).ToArray();
        foreach (string file in changed)
        {
            File.WriteAllBytes(file, Enumerable.Range(0, 400).Select(i => (byte)(i * 3)).ToArray());
        }
        for (int i = 0; i < 20; i++)
        {
            File.WriteAllBytes(Path.Combine(_treeDir, $"newfile{i:D2}.bin"), Enumerable.Range(0, 100 + i).Select(b => (byte)(b + i)).ToArray());
        }
        using var cts = new CancellationTokenSource();
        var cancelLog = new CancelAfterLogProvider("Refresh: Added new file", 5, cts);

        // Default batch size: everything done before the cancel is still pending when the cancel happens
        var runner = RunRefresh(cancelLog, LogLevel.Trace, cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Equal(5, runner.AddedCount);
        var rows = ReadItems();
        Assert.Equal(20 + runner.AddedCount, rows.Count);
        // Every update that was counted is in the database
        int updatedInDb = changed.Count(file => rows.Single(r => r.FilenameAndPath == file).FileSha512Hash == Sha512Hex(file));
        Assert.Equal(runner.UpdatedCount, updatedInDb);
        // Nothing was removed
        Assert.Equal(0, runner.RemovedCount);
    }

    [Fact]
    public void Refresh_CancelledInTheRemovalPhase_KeepsTheRemovalsDoneBeforeTheCancel()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, Tree(20));
        RunScan();
        string[] files = tree.Files.Select(f => Path.Combine(_treeDir, f.RelativePath)).ToArray();
        foreach (string file in files.Take(10))
        {
            File.Delete(file);
        }
        using var cts = new CancellationTokenSource();
        var cancelLog = new CancelAfterLogProvider("Refresh: Removed deleted file", 4, cts);

        var runner = RunRefresh(cancelLog, LogLevel.Trace, cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Equal(4, runner.RemovedCount);
        Assert.Equal(20 - 4, ReadItems().Count);

        // A second refresh finishes the job
        var second = RunRefresh();
        Assert.Equal(IService.EServiceState.finished, second.RunnerState);
        Assert.Equal(10, ReadItems().Count);
    }
}
