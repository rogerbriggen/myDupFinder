// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Files with sizes between <see cref="MinBytes"/> and <see cref="MaxBytes"/> (both inclusive), chosen with the relative <see cref="Weight"/>.
/// </summary>
internal sealed record SizeBucket(double Weight, int MinBytes, int MaxBytes);

internal sealed record TestTreeOptions
{
    /// <summary>Number of files, duplicates included.</summary>
    public int FileCount { get; init; } = 100;

    /// <summary>Seed of all random choices. The same options always give the same tree with the same content.</summary>
    public int Seed { get; init; } = 12345;

    /// <summary>
    /// Share of the files (0 - 1) that have the same content as another file. The number is rounded, so with
    /// 40 files and 0.25 exactly 10 files are copies. At least one file keeps unique content.
    /// </summary>
    public double DuplicateShare { get; init; } = 0.2;

    /// <summary>Number of sub directories below the root.</summary>
    public int DirectoryCount { get; init; } = 10;

    /// <summary>Deepest level of sub directories (the root is level 0).</summary>
    public int MaxDirectoryDepth { get; init; } = 3;

    /// <summary>
    /// Size distribution. Keep MinBytes at 64 or more: very small random files could by chance have the same content.
    /// </summary>
    public IReadOnlyList<SizeBucket> SizeBuckets { get; init; } = new[]
    {
        new SizeBucket(0.70, 64, 4 * 1024),
        new SizeBucket(0.25, 4 * 1024, 256 * 1024),
        new SizeBucket(0.05, 256 * 1024, 4 * 1024 * 1024),
    };
}

/// <param name="RelativePath">Path below the root of the tree.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ContentSeed">Seed of the random content. Files with the same seed (and size) are identical.</param>
internal sealed record GeneratedFile(string RelativePath, long Size, int ContentSeed);

internal sealed record GeneratedTree(string Root, IReadOnlyList<GeneratedFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>Number of different contents (= number of different hashes).</summary>
    public int UniqueContentCount => Files.Select(f => f.ContentSeed).Distinct().Count();

    /// <summary>Number of files that are a copy of another file.</summary>
    public int DuplicateFileCount => Files.Count - UniqueContentCount;
}

/// <summary>
/// Creates a deterministic file tree for tests and benchmarks: nested directories, a mix of file sizes and a fixed share of
/// duplicate contents. No external data is needed, the content comes from a seeded <see cref="Random"/>.
/// </summary>
internal static class TestTreeGenerator
{
    private const int WriteBufferSize = 64 * 1024;

    public static GeneratedTree Generate(string rootDirectory, TestTreeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FileCount < 0 || options.DuplicateShare < 0 || options.DuplicateShare > 1 || options.SizeBuckets.Count == 0)
        {
            throw new ArgumentException("Invalid options", nameof(options));
        }

        var random = new Random(options.Seed);
        Directory.CreateDirectory(rootDirectory);

        // Directories: each new one hangs below a random existing one that is not at the maximum depth yet
        var directories = new List<(string Path, int Depth)> { (string.Empty, 0) };
        for (int i = 0; i < options.DirectoryCount; i++)
        {
            var candidates = directories.Where(d => d.Depth < options.MaxDirectoryDepth).ToList();
            var parent = candidates[random.Next(candidates.Count)];
            directories.Add((Path.Combine(parent.Path, $"dir{i:D3}"), parent.Depth + 1));
            Directory.CreateDirectory(Path.Combine(rootDirectory, directories[^1].Path));
        }

        // Contents: first the unique ones, then copies of randomly chosen unique ones
        int duplicateCount = (int)Math.Round(options.FileCount * options.DuplicateShare);
        int uniqueCount = options.FileCount - duplicateCount;
        if (uniqueCount == 0 && options.FileCount > 0)
        {
            uniqueCount = 1;
            duplicateCount = options.FileCount - 1;
        }
        var contents = new List<(int Seed, int Size)>(options.FileCount);
        for (int i = 0; i < uniqueCount; i++)
        {
            contents.Add((unchecked((options.Seed * 31) + i), PickSize(random, options.SizeBuckets)));
        }
        for (int i = 0; i < duplicateCount; i++)
        {
            contents.Add(contents[random.Next(uniqueCount)]);
        }

        var files = new List<GeneratedFile>(options.FileCount);
        for (int i = 0; i < contents.Count; i++)
        {
            string directory = directories[random.Next(directories.Count)].Path;
            string relativePath = Path.Combine(directory, $"file{i:D6}.bin");
            WriteContent(Path.Combine(rootDirectory, relativePath), contents[i].Seed, contents[i].Size);
            files.Add(new GeneratedFile(relativePath, contents[i].Size, contents[i].Seed));
        }
        return new GeneratedTree(rootDirectory, files);
    }

    private static int PickSize(Random random, IReadOnlyList<SizeBucket> buckets)
    {
        double pick = random.NextDouble() * buckets.Sum(b => b.Weight);
        foreach (var bucket in buckets)
        {
            if (pick < bucket.Weight)
            {
                return random.Next(bucket.MinBytes, bucket.MaxBytes + 1);
            }
            pick -= bucket.Weight;
        }
        var last = buckets[^1];
        return random.Next(last.MinBytes, last.MaxBytes + 1);
    }

    private static void WriteContent(string path, int contentSeed, int size)
    {
        var contentRandom = new Random(contentSeed);
        var buffer = new byte[Math.Min(size, WriteBufferSize)];
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        int remaining = size;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, buffer.Length);
            contentRandom.NextBytes(buffer.AsSpan(0, chunk));
            stream.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }
}
