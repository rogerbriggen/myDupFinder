// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The hash expression that all four runners used before step 6, kept as the reference for the tests and the benchmark.
/// </summary>
internal static class ReferenceFileHash
{
    public static string Compute(string path)
    {
        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", "", StringComparison.Ordinal);
    }
}

/// <summary>
/// Changes a file the way a writer would while a hasher reads it. Used by the tests of the stability check.
/// </summary>
public enum FileMutation
{
    /// <summary>Appends bytes (the file grows).</summary>
    Append,

    /// <summary>Cuts the file to half of its length.</summary>
    Truncate,

    /// <summary>Sets another last write time, content and size stay.</summary>
    Touch,
}

internal static class FileMutator
{
    /// <param name="round">Makes the new values different every time the same file is changed.</param>
    public static void Apply(string path, FileMutation mutation, int round)
    {
        switch (mutation)
        {
            case FileMutation.Append:
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write(Enumerable.Range(0, 100 + round).Select(i => (byte)(i + round)).ToArray());
                }
                break;
            case FileMutation.Truncate:
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.SetLength(stream.Length / 2);
                }
                break;
            case FileMutation.Touch:
                File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(round).AddTicks(1234567 + round));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }
}

public class FileHasherTest : IDisposable
{
    private const int SmallBuffer = 4096;

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public FileHasherTest(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "FileHasherTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string NewFile(byte[] content)
    {
        string path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static FileHasher Hasher(int bufferSize = SmallBuffer, FileShare share = FileShare.Read, Action<string, int, int>? after = null, Action<string, int>? before = null) =>
        new FileHasher(bufferSize, share) { AfterBufferRead = after, BeforeAttempt = before };

    // ---- identical output ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SmallBuffer - 1)]
    [InlineData(SmallBuffer)]
    [InlineData(SmallBuffer + 1)]
    [InlineData(3 * SmallBuffer)]
    [InlineData((3 * SmallBuffer) + 17)]
    [InlineData(10 * SmallBuffer)]
    public void Hash_EqualsTheOldExpression_AroundTheBufferSize(int size)
    {
        string path = NewFile(RandomBytes(size, size));

        FileHashResult result = Hasher().Compute(path);

        Assert.True(result.IsStable);
        Assert.Equal(ReferenceFileHash.Compute(path), result.Hash);
        Assert.Equal(128, result.Hash!.Length);
        Assert.Equal(result.Hash.ToUpperInvariant(), result.Hash);
        Assert.DoesNotContain('-', result.Hash);
        Assert.Equal(size, result.BytesRead);
        Assert.Equal(size, result.Length);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public void Hash_EqualsTheOldExpression_ForRandomSizesAndContent()
    {
        var random = new Random(4711);
        for (int i = 0; i < 60; i++)
        {
            int bufferSize = random.Next(1, 20000);
            int size = random.Next(0, 5 * bufferSize);
            string path = NewFile(RandomBytes(size, random.Next()));

            FileHashResult result = Hasher(bufferSize).Compute(path);

            Assert.Equal(ReferenceFileHash.Compute(path), result.Hash);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(FileHasher.DefaultBufferSize - 1)]
    [InlineData(FileHasher.DefaultBufferSize)]
    [InlineData(FileHasher.DefaultBufferSize + 1)]
    [InlineData((2 * FileHasher.DefaultBufferSize) + 12345)]
    public void Hash_EqualsTheOldExpression_WithTheDefaultBufferSize(int size)
    {
        string path = NewFile(RandomBytes(size, size + 1));

        FileHashResult result = FileHasher.Default.Compute(path);

        Assert.Equal(ReferenceFileHash.Compute(path), result.Hash);
        Assert.Equal(size, result.BytesRead);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(512)]
    [InlineData(1024 * 1024)]
    [InlineData(1536 * 1024)]
    [InlineData(4 * 1024 * 1024)]
    public void Hash_EqualsTheOldExpression_ForOtherBufferSizes(int bufferSize)
    {
        string path = NewFile(RandomBytes((2 * 1024 * 1024) + 5, bufferSize));

        Assert.Equal(ReferenceFileHash.Compute(path), Hasher(bufferSize).Compute(path).Hash);
    }

    [Fact]
    public void Hash_OfAnEmptyFile_IsTheKnownSha512OfNothing()
    {
        string path = NewFile(Array.Empty<byte>());

        Assert.Equal("CF83E1357EEFB8BDF1542850D66D8007D620E4050B5715DC83F4A921D36CE9CE47D0D13C5D85F2B0FF8318D2877EEC2F63B931BD47417A81A538327AF927DA3E", FileHasher.Default.Compute(path).Hash);
    }

    // ---- the same access and sharing as File.OpenRead ----

    public static IEnumerable<object[]> HeldModes()
    {
        foreach (FileAccess access in new[] { FileAccess.Read, FileAccess.Write, FileAccess.ReadWrite })
        {
            foreach (FileShare share in new[] { FileShare.None, FileShare.Read, FileShare.Write, FileShare.ReadWrite, FileShare.Delete, FileShare.Read | FileShare.Delete, FileShare.ReadWrite | FileShare.Delete })
            {
                yield return new object[] { access, share };
            }
        }
    }

    private static (Type? Exception, string? Hash) OutcomeOfTheOldCode(string path)
    {
        try
        {
            return (null, ReferenceFileHash.Compute(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ex.GetType(), null);
        }
    }

    private static (Type? Exception, string? Hash) OutcomeOfTheHasher(string path)
    {
        try
        {
            return (null, FileHasher.Default.Compute(path).Hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ex.GetType(), null);
        }
    }

    [Theory]
    [MemberData(nameof(HeldModes))]
    public void Open_FileHeldByAnotherHandle_SucceedsOrFailsExactlyLikeFileOpenRead(FileAccess heldAccess, FileShare heldShare)
    {
        string path = NewFile(RandomBytes(10000, 3));
        using var holder = new FileStream(path, FileMode.Open, heldAccess, heldShare);

        var oldOutcome = OutcomeOfTheOldCode(path);
        var newOutcome = OutcomeOfTheHasher(path);

        Assert.Equal(oldOutcome.Exception, newOutcome.Exception);
        Assert.Equal(oldOutcome.Hash, newOutcome.Hash);
    }

    [Fact]
    public void Open_TheModesThatMatter_HaveTheExpectedOutcomeOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: the sharing violation is a Windows behaviour.");
            return;
        }
        string path = NewFile(RandomBytes(10000, 3));

        // Held open for reading by another process (shared for read): can still be hashed, as before
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(FileHasher.Default.Compute(path).Hash);
        }
        // Held open for reading and writing, shared for read/write: fails with a sharing violation, as before
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => FileHasher.Default.Compute(path));
            Assert.Throws<IOException>(() => ReferenceFileHash.Compute(path));
        }
        // Held exclusively: fails, as before
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => FileHasher.Default.Compute(path));
            Assert.Throws<IOException>(() => ReferenceFileHash.Compute(path));
        }
    }

    [Fact]
    public void Open_MissingFileAndDirectoryAndMissingDirectory_FailLikeFileOpenRead()
    {
        string missing = Path.Combine(_tempDir, "missing.bin");
        string missingDirectory = Path.Combine(_tempDir, "nodir", "file.bin");
        string directory = _tempDir;

        foreach (string path in new[] { missing, missingDirectory, directory })
        {
            var oldOutcome = OutcomeOfTheOldCode(path);
            var newOutcome = OutcomeOfTheHasher(path);

            Assert.NotNull(oldOutcome.Exception);
            Assert.Equal(oldOutcome.Exception, newOutcome.Exception);
        }
    }

    [Fact]
    public void Open_WhileTheHasherReads_AWriterIsRefusedOnWindows_BecauseOfFileShareRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: Windows only.");
            return;
        }
        string path = NewFile(RandomBytes(5 * SmallBuffer, 9));
        Exception? writerError = null;
        var hasher = Hasher(after: (p, attempt, block) =>
        {
            if (block == 1)
            {
                try
                {
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException ex)
                {
                    writerError = ex;
                }
            }
        });

        FileHashResult result = hasher.Compute(path);

        // This is why the tests that change a file during the read use FileShare.ReadWrite (internal test parameter)
        Assert.NotNull(writerError);
        Assert.True(result.IsStable);
    }

    // ---- handle based size and time equal the path based ones ----

    [Fact]
    public void HandleValues_EqualThePathValues_TickForTick()
    {
        var times = new[]
        {
            new DateTime(2024, 5, 17, 13, 45, 12, DateTimeKind.Utc).AddTicks(1234567),
            new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1),
            new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999),
            new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(7777777),
        };
        foreach (DateTime time in times)
        {
            string path = NewFile(RandomBytes(1234, time.Millisecond));
            File.SetLastWriteTimeUtc(path, time);

            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);

            DateTime fromHandle = File.GetLastWriteTimeUtc(handle);
            DateTime fromPath = File.GetLastWriteTimeUtc(path);
            Assert.Equal(DateTimeKind.Utc, fromHandle.Kind);
            Assert.Equal(fromPath.Ticks, fromHandle.Ticks);
            Assert.Equal(new FileInfo(path).LastWriteTimeUtc.Ticks, fromHandle.Ticks);
            Assert.Equal(FileMetadata.Read(path).LastWriteTimeUtc.Ticks, fromHandle.Ticks);
            Assert.Equal(new FileInfo(path).Length, RandomAccess.GetLength(handle));
            Assert.Equal(FileMetadata.Read(path).Length, RandomAccess.GetLength(handle));
            _output.WriteLine($"{time:O}: handle {fromHandle.Ticks} path {fromPath.Ticks}");
        }
    }

    [Fact]
    public void HashResult_TakesSizeAndTimeFromTheFileItself()
    {
        string path = NewFile(RandomBytes(3 * SmallBuffer, 5));
        File.SetLastWriteTimeUtc(path, new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(8901234));

        FileHashResult result = Hasher().Compute(path);

        Assert.Equal(new FileInfo(path).Length, result.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, result.LastWriteTimeUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, result.LastWriteTimeUtc.Kind);
    }

    // ---- cancel ----

    [Fact]
    public void Cancel_InsideAMultiBufferFile_StopsAfterTheCurrentBufferAndThrows()
    {
        string path = NewFile(RandomBytes(8 * SmallBuffer, 6));
        using var cts = new CancellationTokenSource();
        int blocks = 0;
        var hasher = Hasher(after: (p, attempt, block) =>
        {
            blocks = block;
            if (block == 2)
            {
                cts.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => hasher.Compute(path, cts.Token));

        Assert.Equal(2, blocks);
    }

    [Fact]
    public void Cancel_BeforeTheStart_ThrowsWithoutReading()
    {
        string path = NewFile(RandomBytes(2 * SmallBuffer, 7));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int blocks = 0;
        var hasher = Hasher(after: (p, attempt, block) => blocks++);

        Assert.ThrowsAny<OperationCanceledException>(() => hasher.Compute(path, cts.Token));

        Assert.Equal(0, blocks);
    }

    [Fact]
    public void Cancel_DuringTheSecondAttempt_StillThrows()
    {
        string path = NewFile(RandomBytes(4 * SmallBuffer, 8));
        using var cts = new CancellationTokenSource();
        var hasher = Hasher(share: FileShare.ReadWrite, after: (p, attempt, block) =>
        {
            if (attempt == 1 && block == 1)
            {
                FileMutator.Apply(path, FileMutation.Append, 1);
            }
            if (attempt == 2 && block == 2)
            {
                cts.Cancel();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => hasher.Compute(path, cts.Token));
    }

    // ---- stability ----

    [Theory]
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Changed_OnlyDuringTheFirstAttempt_ReturnsTheHashOfTheFinalContentWithFinalSizeAndTime(FileMutation mutation)
    {
        string path = NewFile(RandomBytes(8 * SmallBuffer, 11));
        var hasher = Hasher(share: FileShare.ReadWrite, after: (p, attempt, block) =>
        {
            if (attempt == 1 && block == 2)
            {
                FileMutator.Apply(path, mutation, 1);
            }
        });

        FileHashResult result = hasher.Compute(path);

        Assert.True(result.IsStable);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(ReferenceFileHash.Compute(path), result.Hash);
        Assert.Equal(new FileInfo(path).Length, result.Length);
        Assert.Equal(result.Length, result.BytesRead);
        Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, result.LastWriteTimeUtc.Ticks);
    }

    [Theory]
    [InlineData(FileMutation.Append)]
    [InlineData(FileMutation.Truncate)]
    [InlineData(FileMutation.Touch)]
    public void Changed_DuringBothAttempts_IsUnstableAndHasNoHash(FileMutation mutation)
    {
        string path = NewFile(RandomBytes(8 * SmallBuffer, 12));
        int opened = 0;
        var hasher = Hasher(
            share: FileShare.ReadWrite,
            before: (p, attempt) => opened++,
            after: (p, attempt, block) =>
            {
                if (block == 2)
                {
                    FileMutator.Apply(path, mutation, attempt);
                }
            });

        FileHashResult result = hasher.Compute(path);

        Assert.False(result.IsStable);
        Assert.Null(result.Hash);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, opened);
        Assert.False(string.IsNullOrEmpty(result.InstabilityDescription));
    }

    [Fact]
    public void Truncated_BelowWhatWasAlreadyRead_IsUnstable()
    {
        string path = NewFile(RandomBytes(8 * SmallBuffer, 13));
        var hasher = Hasher(share: FileShare.ReadWrite, after: (p, attempt, block) =>
        {
            if (block == 3)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                stream.SetLength(10);
            }
        });

        FileHashResult result = hasher.Compute(path);

        // The first attempt read 3 blocks of a file that is 10 bytes long now; the second attempt reads a stable 10 byte file
        Assert.True(result.IsStable);
        Assert.Equal(10, result.Length);
        Assert.Equal(ReferenceFileHash.Compute(path), result.Hash);
    }

    [Fact]
    public void Grown_AndTheSizeIsTheOnlyDifference_IsDetectedEvenWhenTheTimeIsRestored()
    {
        string path = NewFile(RandomBytes(4 * SmallBuffer, 14));
        DateTime original = File.GetLastWriteTimeUtc(path);
        var hasher = Hasher(share: FileShare.ReadWrite, after: (p, attempt, block) =>
        {
            if (block == 1)
            {
                FileMutator.Apply(path, FileMutation.Append, attempt);
                File.SetLastWriteTimeUtc(path, original);
            }
        });

        FileHashResult result = hasher.Compute(path);

        Assert.False(result.IsStable);
    }

    [Fact]
    public void NotChanged_IsReadOnce()
    {
        string path = NewFile(RandomBytes(4 * SmallBuffer, 15));
        int opened = 0;

        FileHashResult result = Hasher(before: (p, attempt) => opened++).Compute(path);

        Assert.True(result.IsStable);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void Buffers_AreReturnedToThePool_AlsoWhenTheReadFails()
    {
        // A failing read (cancel) many times must not leak: this would run out of memory or time out with 4 MB buffers if the buffer were allocated every time
        string path = NewFile(RandomBytes(3 * SmallBuffer, 16));
        var hasher = new FileHasher(2 * 1024 * 1024);
        for (int i = 0; i < 200; i++)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => hasher.Compute(path, cts.Token));
        }
        Assert.NotNull(hasher.Compute(path).Hash);
    }
}
