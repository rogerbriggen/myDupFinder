// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The outcome of <see cref="FileHasher.Compute"/>.
/// </summary>
/// <param name="Hash">The SHA-512 of the file content as 128 upper-case hex characters, or null when the file was not stable (see <see cref="IsStable"/>).</param>
/// <param name="BytesRead">Number of bytes that were read and hashed in the last attempt.</param>
/// <param name="Length">Size of the file as the open file handle reported it before the last attempt was read. When the result is stable this is the size that the hash belongs to.</param>
/// <param name="LastWriteTimeUtc">Last write time (UTC) as the open file handle reported it before the last attempt was read. When the result is stable this is the time that the hash belongs to.</param>
/// <param name="Attempts">1 or 2: how many times the file was read from the start.</param>
/// <param name="InstabilityDescription">null when stable, otherwise what differed in the last attempt (for the log).</param>
internal readonly record struct FileHashResult(string? Hash, long BytesRead, long Length, DateTime LastWriteTimeUtc, int Attempts, string? InstabilityDescription)
{
    /// <summary>
    /// True when size and last write time of the open handle were the same before and after reading and the number of bytes read equals that size,
    /// so <see cref="Hash"/>, <see cref="Length"/> and <see cref="LastWriteTimeUtc"/> describe the same state of the file.
    /// </summary>
    public bool IsStable => Hash is not null;
}

/// <summary>
/// The file changed while it was being read, twice in a row. Used as the exception of the error row that the scan stores for such a file.
/// </summary>
internal sealed class FileChangedWhileReadException : IOException
{
    public FileChangedWhileReadException(string message) : base(message)
    {
    }
}

/// <summary>
/// The one place that computes the SHA-512 of a file for scan, refresh and check.
/// <list type="bullet">
/// <item>The file is opened once, with <see cref="FileOptions.SequentialScan"/> and without the buffering of <see cref="FileStream"/>, read in blocks of
/// <see cref="DefaultBufferSize"/> bytes into a pooled buffer and fed to <see cref="IncrementalHash"/>. The token is checked before every block, so a cancel
/// does not have to wait for the end of a big file (<see cref="OperationCanceledException"/>).</item>
/// <item>The text is exactly what <c>BitConverter.ToString(SHA512.ComputeHash(stream)).Replace("-", "")</c> produced before: 128 upper-case hex characters.</item>
/// <item>Access and sharing are those of <see cref="File.OpenRead(string)"/> (<see cref="FileAccess.Read"/>, <see cref="FileShare.Read"/>), so a file that could be
/// hashed before can still be hashed and a file that could not be opened (for example held open for writing by another process) fails the same way, with the same
/// exceptions. Consequence on Windows: while this class reads a file nobody else can open it for writing.</item>
/// <item>Change detection: size and last write time are taken from the open handle right after opening and again after the last read. The result is stable only if
/// both are unchanged and the number of bytes read equals the size. Otherwise the file is read once more from the start (new open); if that attempt is not stable
/// either, the result is not stable and has no hash. Nothing that is not stable may be stored as a hash. Limits: a change that keeps size and last write time
/// (a file system that does not update the time, an in-place rewrite of equal length before the writer closes the file) is not detected, and a change between
/// the metadata read of the runner and the open is not covered by this check.</item>
/// </list>
/// </summary>
internal sealed class FileHasher
{
    /// <summary>
    /// Size of the read blocks. Measured on an SSD with a warm cache (median of 5 runs, 1 GB file): 64 KB 557 MB/s, 256 KB 568, 1 MB 570, 4 MB 576
    /// (the old expression: 430), so from 256 KB on the differences are below 1.5 % and 64 KB is about 2 % slower; 20000 small files: no difference
    /// between the sizes (a request is never larger than the rest of the file plus one byte). 1 MB is the tie-break: the largest size that the shared
    /// array pool still serves, and fewer requests on media where each request costs a round trip (hard disk, network share), which were NOT measured.
    /// See "Step 6" in docs/speed-up-plan.md and docs/benchmark.md. Not a job setting on purpose.
    /// </summary>
    internal const int DefaultBufferSize = 1024 * 1024;

    /// <summary>A file is read at most this many times from the start (the first read plus one retry).</summary>
    internal const int MaxAttempts = 2;

    private const int SharedPoolMaxBufferSize = 1024 * 1024;

    /// <summary>Size of a read at or beyond the end that the handle reported.</summary>
    private const int EndProbeSize = 4096;

    private readonly int _bufferSize;
    private readonly FileShare _share;
    private readonly ArrayPool<byte> _pool;

    /// <summary>The hasher with the default buffer size that the runners use.</summary>
    public static FileHasher Default { get; } = new FileHasher();

    /// <param name="bufferSize">Size of the read blocks. Only tests and the benchmark pass a value, the default is the measured <see cref="DefaultBufferSize"/>.</param>
    /// <param name="share">Only for tests: with <see cref="FileShare.Read"/> (the default, same as <see cref="File.OpenRead(string)"/>) Windows refuses every other write access
    /// to the file while it is read, so a test that wants to change the file during the read passes <see cref="FileShare.ReadWrite"/>. The runners never do.</param>
    internal FileHasher(int bufferSize = DefaultBufferSize, FileShare share = FileShare.Read)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _bufferSize = bufferSize;
        _share = share;
        _pool = bufferSize <= SharedPoolMaxBufferSize ? ArrayPool<byte>.Shared : ArrayPool<byte>.Create(bufferSize, 4);
    }

    /// <summary>Only for tests: called with the path and the attempt number (1 or 2) before the file is opened. Counts the opens.</summary>
    internal Action<string, int>? BeforeAttempt { get; init; }

    /// <summary>Only for tests: called with the path, the attempt number (1 or 2) and the number of the block (1, 2, ...) after a block was read and hashed.
    /// A test can change the file here (see the <c>share</c> parameter of the constructor) or cancel a token.</summary>
    internal Action<string, int, int>? AfterBufferRead { get; init; }

    /// <summary>
    /// Hashes the file. Reads it a second time when the first read was not stable.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing is returned for the file.</exception>
    /// <exception cref="IOException">The file could not be opened or read (the same exceptions as <see cref="File.OpenRead(string)"/> and reading a stream).</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public FileHashResult Compute(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileHashResult result = default;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            result = ComputeOnce(path, attempt, cancellationToken);
            if (result.IsStable)
            {
                break;
            }
        }
        return result;
    }

    private FileHashResult ComputeOnce(string path, int attempt, CancellationToken cancellationToken)
    {
        BeforeAttempt?.Invoke(path, attempt);

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, _share, FileOptions.SequentialScan);
        long lengthBefore = RandomAccess.GetLength(handle);
        DateTime lastWriteBefore = File.GetLastWriteTimeUtc(handle);

        byte[] buffer = _pool.Rent(_bufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            long bytesRead = 0;
            int block = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Ask for no more than the rest of the file according to the size from the handle, plus one byte so that a file that grew is noticed
                // (a read request into a big buffer costs time even when the file is small, measured). At the expected end, one more small read
                // finds the real end. The result does not depend on this: the loop always reads until a read returns 0.
                int request = bytesRead < lengthBefore ? (int)Math.Min(_bufferSize, lengthBefore - bytesRead + 1) : Math.Min(_bufferSize, EndProbeSize);
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, request), bytesRead);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                bytesRead += read;
                block++;
                AfterBufferRead?.Invoke(path, attempt, block);
            }

            long lengthAfter = RandomAccess.GetLength(handle);
            DateTime lastWriteAfter = File.GetLastWriteTimeUtc(handle);
            if (lengthBefore == lengthAfter && lastWriteBefore == lastWriteAfter && bytesRead == lengthBefore)
            {
                return new FileHashResult(Convert.ToHexString(hash.GetHashAndReset()), bytesRead, lengthBefore, lastWriteBefore, attempt, null);
            }

            string description = string.Format(
                CultureInfo.InvariantCulture,
                "size before {0} after {1}, bytes read {2}, last write time before {3:O} after {4:O}",
                lengthBefore,
                lengthAfter,
                bytesRead,
                lastWriteBefore,
                lastWriteAfter);
            return new FileHashResult(null, bytesRead, lengthBefore, lastWriteBefore, attempt, description);
        }
        finally
        {
            _pool.Return(buffer);
        }
    }
}
