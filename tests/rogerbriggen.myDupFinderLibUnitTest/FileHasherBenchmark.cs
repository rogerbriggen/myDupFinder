// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Opt-in benchmark of the file hasher (step 6): the old expression against the new hasher with several buffer sizes, on one large file and on many small files.
/// Run it with MYDUPFINDER_BENCHMARK=1, see docs/benchmark.md. The cache of the operating system cannot be emptied from a test, so the numbers are warm cache numbers.
/// </summary>
public class FileHasherBenchmark : IDisposable
{
    private const string LargeFileVariable = "MYDUPFINDER_BENCHMARK_LARGE_MB";
    private const string FileCountVariable = "MYDUPFINDER_BENCHMARK_FILES";
    private const string RunsVariable = "MYDUPFINDER_BENCHMARK_RUNS";

    private static readonly int[] BufferSizes = { 64 * 1024, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024 };

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public FileHasherBenchmark(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "FileHasherBenchmark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static int ReadInt(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : fallback;

    private sealed record Variant(string Name, Func<string, string> Hash);

    // One block per size, reused for all files (the benchmark runs on one thread), so that the FileStream variants do not measure allocating a block per file
    private static readonly Dictionary<int, byte[]> Blocks = new Dictionary<int, byte[]>();

    private static string FileStreamHash(string path, int bufferSize, int fileStreamBufferSize)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, fileStreamBufferSize, FileOptions.SequentialScan);
        if (!Blocks.TryGetValue(bufferSize, out byte[]? buffer))
        {
            buffer = new byte[bufferSize];
            Blocks[bufferSize] = buffer;
        }
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Label(int bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";

    private static List<Variant> Variants()
    {
        var variants = new List<Variant> { new Variant("old: File.OpenRead + ComputeHash", ReferenceFileHash.Compute) };
        foreach (int size in BufferSizes)
        {
            int captured = size;
            var hasher = new FileHasher(captured);
            variants.Add(new Variant($"FileHasher {Label(captured)}", path => hasher.Compute(path).Hash));
        }
        // FileStream with its own buffering (4096, the default) against without (0), read in blocks of 1 MB and 4 MB, SequentialScan in both
        foreach (int size in new[] { 1024 * 1024, 4 * 1024 * 1024 })
        {
            int captured = size;
            variants.Add(new Variant($"FileStream {Label(captured)} blocks, buffered (4096)", path => FileStreamHash(path, captured, 4096)));
            variants.Add(new Variant($"FileStream {Label(captured)} blocks, unbuffered (0)", path => FileStreamHash(path, captured, 0)));
        }
        return variants;
    }

    /// <summary>
    /// Runs every variant <paramref name="runs"/> times, in a different order each round (so that no variant always gets the cache the previous one left),
    /// and returns the seconds per run.
    /// </summary>
    private static Dictionary<string, List<double>> Measure(List<Variant> variants, Func<Variant, double> runOnce, int runs)
    {
        var times = variants.ToDictionary(v => v.Name, v => new List<double>());
        for (int round = 0; round < runs; round++)
        {
            for (int i = 0; i < variants.Count; i++)
            {
                Variant variant = variants[(i + round) % variants.Count];
                times[variant.Name].Add(runOnce(variant));
            }
        }
        return times;
    }

    private void PrintTable(List<Variant> variants, Dictionary<string, List<double>> times, double megabytes, string unitLabel, int fileCount)
    {
        _output.WriteLine($"{"Variant",-48} {"best s",8} {"median s",9} {"MB/s (median)",14} {"files/s (median)",17}");
        foreach (Variant variant in variants)
        {
            var sorted = times[variant.Name].OrderBy(t => t).ToList();
            double best = sorted[0];
            double median = sorted[sorted.Count / 2];
            _output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0,-48} {1,8:F2} {2,9:F2} {3,14:F0} {4,17:F0}",
                variant.Name,
                best,
                median,
                megabytes / median,
                fileCount / median));
        }
        _output.WriteLine(unitLabel);
    }

    [BenchmarkFact]
    public void Hasher_LargeFile_ReportsBufferSizes()
    {
        int megabytes = ReadInt(LargeFileVariable, 1024);
        int runs = ReadInt(RunsVariable, 3);
        string path = Path.Combine(_tempDir, "large.bin");
        var stopwatch = Stopwatch.StartNew();
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
        {
            byte[] chunk = new byte[4 * 1024 * 1024];
            var random = new Random(1);
            for (long written = 0; written < (long)megabytes * 1024 * 1024; written += chunk.Length)
            {
                random.NextBytes(chunk);
                int count = (int)Math.Min(chunk.Length, ((long)megabytes * 1024 * 1024) - written);
                stream.Write(chunk, 0, count);
            }
        }
        _output.WriteLine($"Large file: {megabytes} MB generated in {stopwatch.Elapsed.TotalSeconds:F1} s (not part of the times), {runs} runs per variant, order rotated every run. Warm cache: the file was just written and the cache of the operating system cannot be emptied from here.");
        string expected = ReferenceFileHash.Compute(path);

        var variants = Variants();
        var times = Measure(variants, variant =>
        {
            var timer = Stopwatch.StartNew();
            string hash = variant.Hash(path);
            timer.Stop();
            Assert.Equal(expected, hash);
            return timer.Elapsed.TotalSeconds;
        }, runs);

        PrintTable(variants, times, megabytes, "(MB/s is of the large file; files/s is not meaningful here)", 1);
    }

    [BenchmarkFact]
    public void Hasher_ManySmallFiles_ReportsBufferSizes()
    {
        int fileCount = ReadInt(FileCountVariable, 20000);
        int runs = ReadInt(RunsVariable, 3);
        var tree = TestTreeGenerator.Generate(Path.Combine(_tempDir, "tree"), new TestTreeOptions
        {
            FileCount = fileCount,
            DuplicateShare = 0.2,
            DirectoryCount = Math.Max(1, fileCount / 20),
            MaxDirectoryDepth = 4,
            SizeBuckets = new[] { new SizeBucket(0.9, 64, 4 * 1024), new SizeBucket(0.1, 4 * 1024, 64 * 1024) },
        });
        string[] files = tree.Files.Select(f => Path.Combine(tree.Root, f.RelativePath)).ToArray();
        _output.WriteLine(string.Format(CultureInfo.InvariantCulture, "Small files: {0} files, {1:F1} MB, {2} runs per variant, order rotated every run, warm cache.", files.Length, tree.TotalBytes / (1024.0 * 1024.0), runs));
        string[] expected = files.Select(ReferenceFileHash.Compute).ToArray();

        var variants = Variants();
        var times = Measure(variants, variant =>
        {
            var timer = Stopwatch.StartNew();
            string[] hashes = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                hashes[i] = variant.Hash(files[i]);
            }
            timer.Stop();
            Assert.Equal(expected, hashes);
            return timer.Elapsed.TotalSeconds;
        }, runs);

        PrintTable(variants, times, tree.TotalBytes / (1024.0 * 1024.0), "(files/s is the column to read here)", files.Length);
    }
}
