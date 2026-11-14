// Roger Briggen license this file to you under the MIT license.
//

using System;
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
/// Runs a scan job in the calling thread, for tests and the benchmark.
/// </summary>
internal static class ScanTestRunner
{
    /// <summary>
    /// Runs the job to the end and returns the runner (its state and Throughput can be looked at afterwards).
    /// The database is closed again when this returns.
    /// </summary>
    public static ScanRunnerSingleThread Run(MyDupFinderScanJobDTO job, ILoggerProvider? loggerProvider = null, LogLevel minimumLevel = LogLevel.Information)
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
        using var serviceProvider = services.BuildServiceProvider();
        var runner = new ScanRunnerSingleThread(job, serviceProvider.GetService<ILogger<ScanRunnerSingleThread>>(), serviceProvider);
        runner.Start(CancellationToken.None);
        return runner;
    }
}

public class ScanTreeTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _treeDir;
    private readonly string _dbPath;

    public ScanTreeTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScanTreeTest_" + Guid.NewGuid().ToString("N"));
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

    private static readonly TestTreeOptions SmallTree = new TestTreeOptions
    {
        FileCount = 40,
        DuplicateShare = 0.25,
        DirectoryCount = 6,
        SizeBuckets = new[] { new SizeBucket(1.0, 64, 2048) },
    };

    private MyDupFinderScanJobDTO CreateJob() => new MyDupFinderScanJobDTO
    {
        JobName = "ScanTreeTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _treeDir,
        DatabaseFile = _dbPath,
        ReportPath = _tempDir,
    };

    [Fact]
    public void Scan_SmallTree_StoresOneRowPerFile()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, SmallTree);
        Assert.Equal(40, tree.Files.Count);
        Assert.Equal(10, tree.DuplicateFileCount);

        var runner = ScanTestRunner.Run(CreateJob());

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        Assert.Equal(tree.Files.Count, context.ScanItems!.Count());
        Assert.Equal(0, context.ScanErrorItems!.Count());
        Assert.Equal(tree.UniqueContentCount, context.ScanItems!.Select(s => s.FileSha512Hash).Distinct().Count());
        Assert.Equal(tree.Files.Count, runner.Throughput.Files);
        Assert.Equal(tree.TotalBytes, runner.Throughput.BytesHashed);
    }

    [Fact]
    public void Scan_SameTreeTwice_SecondRunSkipsEverything()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, SmallTree);
        ScanTestRunner.Run(CreateJob());

        var runner = ScanTestRunner.Run(CreateJob());

        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        Assert.Equal(tree.Files.Count, context.ScanItems!.Count());
        // Skipped files count as files but add no hashed bytes
        Assert.Equal(tree.Files.Count, runner.Throughput.Files);
        Assert.Equal(0, runner.Throughput.BytesHashed);
    }

    [Fact]
    public void Scan_DefaultLogLevel_WritesNoPerFileMessagesButStateAndSummary()
    {
        TestTreeGenerator.Generate(_treeDir, SmallTree);
        var log = new ListLoggerProvider();

        ScanTestRunner.Run(CreateJob(), log, LogLevel.Information);

        var messages = log.Entries.Select(e => e.Message).ToList();
        Assert.DoesNotContain(messages, m => m.Contains("successfull finished", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Database state:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Scanning finished. 40 files", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_TraceLogLevel_WritesPerFileMessages()
    {
        TestTreeGenerator.Generate(_treeDir, SmallTree);
        var log = new ListLoggerProvider();

        ScanTestRunner.Run(CreateJob(), log, LogLevel.Trace);

        Assert.Equal(40, log.Entries.Count(e => e.Level == LogLevel.Trace && e.Message.Contains("successfull finished", StringComparison.Ordinal)));
    }

    [Fact]
    public void Generator_SameOptions_GivesSameTreeWithSameContent()
    {
        var first = TestTreeGenerator.Generate(Path.Combine(_tempDir, "first"), SmallTree);
        var second = TestTreeGenerator.Generate(Path.Combine(_tempDir, "second"), SmallTree);

        Assert.Equal(first.Files, second.Files);
        foreach (var file in first.Files)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(first.Root, file.RelativePath)),
                File.ReadAllBytes(Path.Combine(second.Root, file.RelativePath)));
        }
    }
}
