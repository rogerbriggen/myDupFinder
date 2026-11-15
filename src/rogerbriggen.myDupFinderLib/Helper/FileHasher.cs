// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The outcome of <see cref="FileHasher.Compute"/>.
/// </summary>
/// <param name="Hash">The SHA-512 of the file content as 128 upper-case hex characters.</param>
/// <param name="BytesRead">Number of bytes that were read and hashed.</param>
internal readonly record struct FileHashResult(string Hash, long BytesRead);

/// <summary>
/// The one place that computes the SHA-512 of a file for scan, refresh and check.
/// <list type="bullet">
/// <item>The file is opened once, with <see cref="FileOptions.SequentialScan"/> and without the buffering of <see cref="FileStream"/>, read in blocks of
/// <see cref="DefaultBufferSize"/> bytes into a pooled buffer until a read returns 0 and fed to <see cref="IncrementalHash"/>. The token is checked before every block, so a cancel
/// does not have to wait for the end of a big file (<see cref="OperationCanceledException"/>).</item>
/// <item>The text is exactly what <c>BitConverter.ToString(SHA512.ComputeHash(stream)).Replace("-", "")</c> produced before: 128 upper-case hex characters.</item>
/// <item>Access and sharing are those of <see cref="File.OpenRead(string)"/> (<see cref="FileAccess.Read"/>, <see cref="FileShare.Read"/>), so a file that could be
/// hashed before can still be hashed and a file that could not be opened (for example held open for writing by another process) fails the same way, with the same
/// exceptions. Consequence on Windows: while this class reads a file nobody else can open it for writing.</item>
/// </list>
/// </summary>
internal sealed class FileHasher
{
    /// <summary>
    /// Size of the read blocks. Measured on an SSD with a warm cache (median of 5 runs, 1 GB file): 64 KB 557 MB/s, 256 KB 568, 1 MB 570, 4 MB 576
    /// (the old expression: 430), so from 256 KB on the differences are below 1.5 % and 64 KB is about 2 % slower; 20000 small files: no difference
    /// between the sizes. 1 MB is the tie-break: the largest size that the shared
    /// array pool still serves, and fewer requests on media where each request costs a round trip (hard disk, network share), which were NOT measured.
    /// See "Step 6" in docs/speed-up-plan.md and docs/benchmark.md. Not a job setting on purpose.
    /// </summary>
    internal const int DefaultBufferSize = 1024 * 1024;

    private const int SharedPoolMaxBufferSize = 1024 * 1024;

    private readonly int _bufferSize;
    private readonly ArrayPool<byte> _pool;

    /// <summary>The hasher with the default buffer size that the runners use.</summary>
    public static FileHasher Default { get; } = new FileHasher();

    /// <param name="bufferSize">Size of the read blocks. Only tests and the benchmark pass a value, the default is the measured <see cref="DefaultBufferSize"/>.</param>
    internal FileHasher(int bufferSize = DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        _bufferSize = bufferSize;
        _pool = bufferSize <= SharedPoolMaxBufferSize ? ArrayPool<byte>.Shared : ArrayPool<byte>.Create(bufferSize, 4);
    }

    /// <summary>Only for tests: called with the path before the file is opened. Counts the opens.</summary>
    internal Action<string>? BeforeOpen { get; init; }

    /// <summary>Only for tests: called with the path and the number of the block (1, 2, ...) after a block was read and hashed. A test can cancel a token here.</summary>
    internal Action<string, int>? AfterBufferRead { get; init; }

    /// <summary>
    /// Hashes the file.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing is returned for the file.</exception>
    /// <exception cref="IOException">The file could not be opened or read (the same exceptions as <see cref="File.OpenRead(string)"/> and reading a stream).</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public FileHashResult Compute(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        BeforeOpen?.Invoke(path);

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);

        byte[] buffer = _pool.Rent(_bufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            long bytesRead = 0;
            int block = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, _bufferSize), bytesRead);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                bytesRead += read;
                block++;
                AfterBufferRead?.Invoke(path, block);
            }

            return new FileHashResult(Convert.ToHexString(hash.GetHashAndReset()), bytesRead);
        }
        finally
        {
            _pool.Return(buffer);
        }
    }
}
