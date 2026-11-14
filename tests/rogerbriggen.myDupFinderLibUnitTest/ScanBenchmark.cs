// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
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
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static int GetFileCount() =>
        int.TryParse(Environment.GetEnvironmentVariable(FileCountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : DefaultFileCount;

    [BenchmarkFact]
    public void Scan_GeneratedTree_ReportsTimings()
    {
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
    /// Resume benchmark: scans a generated tree once, then scans it again. In the second run every file is already in the
    /// database and is skipped by the resume filter (one IsAlreadyInDB query per file), so its timing is the cost of that filter.
    /// Uses small files only, because the files are not hashed in the second run and the size does not matter for it.
    /// </summary>
    [BenchmarkFact]
    public void Rescan_GeneratedTree_ReportsResumeTimings()
    {
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
}
