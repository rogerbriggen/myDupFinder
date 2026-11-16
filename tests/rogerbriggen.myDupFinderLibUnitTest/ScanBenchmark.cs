// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Diagnostics;
using System.Globalization;
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
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// A [Fact] that is skipped unless the environment variable MYDUPFINDER_BENCHMARK is set to 1.
/// </summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "MYDUPFINDER_BENCHMARK";

    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Benchmark: set {EnvironmentVariable}=1 to run it (see docs/benchmark.md)";
        }
    }
}

/// <summary>
/// Opt-in benchmark: generates a tree, scans it into a fresh database and prints the timings from the ThroughputTracker.
/// Run it with MYDUPFINDER_BENCHMARK=1, see docs/benchmark.md.
/// </summary>
public class ScanBenchmark : IDisposable
{
    private const string FileCountVariable = "MYDUPFINDER_BENCHMARK_FILES";
    private const int DefaultFileCount = 5000;

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public ScanBenchmark(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "ScanBenchmark_" + Guid.NewGuid().ToString("N"));
        // cache_size / temp_store from MYDUPFINDER_BENCHMARK_SQLITE (synchronous goes through the job setting)
        BenchmarkSqliteSettings.Apply();
    }

    public void Dispose()
    {
        BenchmarkSqliteSettings.Reset();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private const string TrustVariable = "MYDUPFINDER_BENCHMARK_TRUST";

    /// <summary>MYDUPFINDER_BENCHMARK_TRUST=1 runs the jobs with TrustDirectoryMetadata (no call per file), otherwise the default (metadata read from each file).</summary>
    private static bool TrustDirectoryMetadata => Environment.GetEnvironmentVariable(TrustVariable) == "1";

    private const string WorkersVariable = "MYDUPFINDER_BENCHMARK_WORKERS";

    /// <summary>MYDUPFINDER_BENCHMARK_WORKERS=n runs the scan jobs of the benchmarks with HashWorkers = n (default 1: the sequential scan; more: the pipeline).</summary>
    private static int HashWorkers =>
        int.TryParse(Environment.GetEnvironmentVariable(WorkersVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= 1 && parsed <= MyDupFinderScanJobDTO.MaxHashWorkers ? parsed : 1;

    private void WriteMode() => _output.WriteLine(
        "Mode: " + (TrustDirectoryMetadata ? "TrustDirectoryMetadata=true (directory listing values)" : "default (metadata read from each file)")
        + $", HashWorkers={HashWorkers}, {Environment.ProcessorCount} logical processors, {BenchmarkSqliteSettings.Description}");

    private static int GetFileCount() =>
        int.TryParse(Environment.GetEnvironmentVariable(FileCountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : DefaultFileCount;

    [BenchmarkFact]
    public void Scan_GeneratedTree_ReportsTimings()
    {
        WriteMode();
        int fileCount = GetFileCount();
        var options = new TestTreeOptions { FileCount = fileCount, DuplicateShare = 0.2, DirectoryCount = Math.Max(1, fileCount / 20), MaxDirectoryDepth = 4 };

        var stopwatch = Stopwatch.StartNew();
        var tree = TestTreeGenerator.Generate(Path.Combine(_tempDir, "tree"), options);
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Tree: {0} files, {1:F1} MB, {2} unique contents, {3} duplicate files, generated in {4:F1} s (not part of the scan time)",
            tree.Files.Count,
            tree.TotalBytes / (1024.0 * 1024.0),
            tree.UniqueContentCount,
            tree.DuplicateFileCount,
            stopwatch.Elapsed.TotalSeconds));

        var job = new MyDupFinderScanJobDTO
        {
            JobName = "Benchmark",
            OriginComputer = "BenchmarkPC",
            ScanName = "Benchmark",
            BasePath = tree.Root,
            DatabaseFile = Path.Combine(_tempDir, "db", "benchmark.db"),
            ReportPath = _tempDir,
            TrustDirectoryMetadata = TrustDirectoryMetadata,
            HashWorkers = HashWorkers,
            DatabaseSynchronous = BenchmarkSqliteSettings.Synchronous,
        };
        var runner = ScanTestRunner.Run(job);

        _output.WriteLine("Scan: " + runner.Throughput.BuildSummary());
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(tree.Files.Count, runner.Throughput.Files);

        // Failed files still count as handled files, so verify the scan really was complete
        using (var context = DubFinderContextFactory.CreateDubFinderContext(job.DatabaseFile))
        {
            Assert.Equal(0, context.ScanErrorItems!.Count());
            Assert.Equal(tree.Files.Count, context.ScanItems!.Count());
        }
        Assert.Equal(tree.TotalBytes, runner.Throughput.BytesHashed);
    }

    /// <summary>
    /// Many tiny files only (64 B to 512 B, no duplicates), so the scan is bound by the database and the per-file calls, not by hashing:
    /// shows where more workers stop helping (step 7). Default: 20000 files (<c>MYDUPFINDER_BENCHMARK_FILES</c>). Run only this one with
    /// <c>--filter "FullyQualifiedName~Scan_ManyTinyFiles"</c>.
    /// </summary>
    [BenchmarkFact]
    public void Scan_ManyTinyFiles_ReportsTimings()
    {
        WriteMode();
        int fileCount = GetFileCount();
        var options = new TestTreeOptions
        {
            FileCount = fileCount,
            DuplicateShare = 0,
            DirectoryCount = Math.Max(1, fileCount / 20),
            MaxDirectoryDepth = 4,
            SizeBuckets = new[] { new SizeBucket(1.0, 64, 512) },
        };
        var tree = TestTreeGenerator.Generate(Path.Combine(_tempDir, "tree"), options);
        _output.WriteLine(string.Format(CultureInfo.InvariantCulture, "Tree: {0} files, {1:F1} MB (not part of the scan time)", tree.Files.Count, tree.TotalBytes / (1024.0 * 1024.0)));

        var job = new MyDupFinderScanJobDTO
        {
            JobName = "Benchmark",
            OriginComputer = "BenchmarkPC",
            ScanName = "Benchmark",
            BasePath = tree.Root,
            DatabaseFile = Path.Combine(_tempDir, "db", "benchmark.db"),
            ReportPath = _tempDir,
            TrustDirectoryMetadata = TrustDirectoryMetadata,
            HashWorkers = HashWorkers,
            DatabaseSynchronous = BenchmarkSqliteSettings.Synchronous,
        };
        var runner = ScanTestRunner.Run(job);

        _output.WriteLine("Scan: " + runner.Throughput.BuildSummary());
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        using var context = DubFinderContextFactory.CreateDubFinderContext(job.DatabaseFile);
        Assert.Equal(0, context.ScanErrorItems!.Count());
        Assert.Equal(tree.Files.Count, context.ScanItems!.Count());
    }

    /// <summary>
    /// Resume benchmark: scans a generated tree once, then scans it again. In the second run every file is already in the
    /// database and is skipped by the resume filter (one IsAlreadyInDB query per file), so its timing is the cost of that filter.
    /// Uses small files only, because the files are not hashed in the second run and the size does not matter for it.
    /// </summary>
    [BenchmarkFact]
    public void Rescan_GeneratedTree_ReportsResumeTimings()
    {
        WriteMode();
        int fileCount = GetFileCount();
        var options = new TestTreeOptions
        {
            FileCount = fileCount,
            DuplicateShare = 0.2,
            DirectoryCount = Math.Max(1, fileCount / 20),
            MaxDirectoryDepth = 4,
            SizeBuckets = new[] { new SizeBucket(0.9, 64, 4 * 1024), new SizeBucket(0.1, 4 * 1024, 64 * 1024) },
        };

        var stopwatch = Stopwatch.StartNew();
        var tree = TestTreeGenerator.Generate(Path.Combine(_tempDir, "tree"), options);
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Tree: {0} files, {1:F1} MB, generated in {2:F1} s (not part of the scan time)",
            tree.Files.Count,
            tree.TotalBytes / (1024.0 * 1024.0),
            stopwatch.Elapsed.TotalSeconds));

        var job = new MyDupFinderScanJobDTO
        {
            JobName = "Benchmark",
            OriginComputer = "BenchmarkPC",
            ScanName = "Benchmark",
            BasePath = tree.Root,
            DatabaseFile = Path.Combine(_tempDir, "db", "benchmark.db"),
            ReportPath = _tempDir,
            TrustDirectoryMetadata = TrustDirectoryMetadata,
            HashWorkers = HashWorkers,
            DatabaseSynchronous = BenchmarkSqliteSettings.Synchronous,
        };
        var firstRun = ScanTestRunner.Run(job);
        _output.WriteLine("First scan:  " + firstRun.Throughput.BuildSummary());
        Assert.Equal(IService.EServiceState.finished, firstRun.RunnerState);
        Assert.Equal(tree.Files.Count, firstRun.Throughput.Files);

        var secondRun = ScanTestRunner.Run(job);
        _output.WriteLine("Scan: " + secondRun.Throughput.BuildSummary());
        Assert.Equal(IService.EServiceState.finished, secondRun.RunnerState);
        Assert.Equal(tree.Files.Count, secondRun.Throughput.Files);
        Assert.Equal(0, secondRun.Throughput.BytesHashed);

        using var context = DubFinderContextFactory.CreateDubFinderContext(job.DatabaseFile);
        Assert.Equal(0, context.ScanErrorItems!.Count());
        Assert.Equal(tree.Files.Count, context.ScanItems!.Count());
    }

    /// <summary>
    /// Check benchmark for IgnoreBasePath (step 4 of the plan): scans a generated tree, renames the tree directory and checks it
    /// with IgnoreBasePath, so no path matches exactly and every file has to be found by its relative path. The hash check is
    /// switched off (the files are not read), so the time is mostly the lookup of the rows. Run only this one with
    /// <c>--filter "FullyQualifiedName~Check_IgnoreBasePath"</c>. Default: 20000 files.
    /// </summary>
    [BenchmarkFact]
    public void Check_IgnoreBasePath_MovedTree_ReportsTimings()
    {
        WriteMode();
        int fileCount = int.TryParse(Environment.GetEnvironmentVariable(FileCountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : 20000;
        var options = new TestTreeOptions
        {
            FileCount = fileCount,
            DuplicateShare = 0.2,
            DirectoryCount = Math.Max(1, fileCount / 20),
            MaxDirectoryDepth = 4,
            SizeBuckets = new[] { new SizeBucket(0.9, 64, 4 * 1024), new SizeBucket(0.1, 4 * 1024, 64 * 1024) },
        };
        string originalRoot = Path.Combine(_tempDir, "tree");
        string movedRoot = Path.Combine(_tempDir, "tree_moved");
        var tree = TestTreeGenerator.Generate(originalRoot, options);
        var scanJob = new MyDupFinderScanJobDTO
        {
            JobName = "Benchmark",
            OriginComputer = "BenchmarkPC",
            ScanName = "Benchmark",
            BasePath = originalRoot,
            DatabaseFile = Path.Combine(_tempDir, "db", "benchmark.db"),
            ReportPath = _tempDir,
            TrustDirectoryMetadata = TrustDirectoryMetadata,
            HashWorkers = HashWorkers,
            DatabaseSynchronous = BenchmarkSqliteSettings.Synchronous,
        };
        var scanRunner = ScanTestRunner.Run(scanJob);
        Assert.Equal(IService.EServiceState.finished, scanRunner.RunnerState);
        Assert.Equal(tree.Files.Count, scanRunner.Throughput.Files);
        Directory.Move(originalRoot, movedRoot);

        var checkJob = new MyDupFinderCheckJobDTO
        {
            ScanJobDTO = new MyDupFinderScanJobDTO
            {
                JobName = "Benchmark",
                OriginComputer = "BenchmarkPC",
                ScanName = "Benchmark",
                BasePath = movedRoot,
                DatabaseFile = scanJob.DatabaseFile,
                ReportPath = _tempDir,
                TrustDirectoryMetadata = TrustDirectoryMetadata,
                DatabaseSynchronous = BenchmarkSqliteSettings.Synchronous,
            },
            IgnoreBasePath = true,
            SkipHashCheck = true,
        };
        var services = new ServiceCollection();
        services.AddLogging();
        using var serviceProvider = services.BuildServiceProvider();
        using var runner = new CheckRunner(checkJob, serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider);
        var stopwatch = Stopwatch.StartNew();
        runner.Start(CancellationToken.None);
        stopwatch.Stop();

        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Check IgnoreBasePath, {0} files in a moved tree: {1:F2} s total. {2}",
            tree.Files.Count,
            stopwatch.Elapsed.TotalSeconds,
            runner.Throughput.BuildSummary()));
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        // Every file was found by its relative path: nothing is new, nothing is missing, all are reported as moved
        Assert.Equal(0, runner.NewOnDiskCount);
        Assert.Equal(0, runner.MissingOnDiskCount);
        Assert.Equal(tree.Files.Count, runner.ModifiedNoHashChangeCount);
        Assert.Equal(0, runner.ErrorCount);
    }
}
