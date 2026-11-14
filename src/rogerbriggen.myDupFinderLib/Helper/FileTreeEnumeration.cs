// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// One file as it was reported by the directory listing: the full path and the metadata of the directory entry.
/// Both timestamps are UTC (<see cref="DateTimeKind.Utc"/>), with the same ticks that
/// <see cref="File.GetCreationTimeUtc(string)"/> and <see cref="File.GetLastWriteTimeUtc(string)"/> return.
/// </summary>
/// <param name="FullPath">The path in the form that <c>Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories)</c> returns.</param>
/// <param name="Length">Size in bytes, same as <c>new FileInfo(path).Length</c>.</param>
/// <param name="CreationTimeUtc">Creation time (UTC).</param>
/// <param name="LastWriteTimeUtc">Last write time (UTC).</param>
public readonly record struct FileEntryInfo(string FullPath, long Length, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);

/// <summary>
/// A directory whose content could not be listed completely, and why.
/// </summary>
/// <param name="Path">Full path of the directory, in the same form as the paths of the files.</param>
/// <param name="Error">The exception of the failed listing.</param>
public sealed record UnreadableDirectory(string Path, Exception Error);

/// <summary>
/// The result of walking one directory tree: the files, and after (or while) walking the directories that could not be read.
/// </summary>
public interface IFileEnumeration : IEnumerator<FileEntryInfo>
{
    /// <summary>
    /// Directories whose content could not be read completely. Files below them are not (or only partly) in the enumeration.
    /// This list only grows while the enumeration runs and is <b>complete only after <see cref="IEnumerator.MoveNext"/> returned
    /// false</b>. Anything that deletes or reports data because a file was not enumerated has to wait for that
    /// and must exempt everything below these directories. Not thread safe: read it from the thread that enumerates.
    /// </summary>
    IReadOnlyList<UnreadableDirectory> UnreadableDirectories { get; }
}

/// <summary>
/// Creates the file enumeration of a base path. The runners take this as a parameter so tests can simulate unreadable
/// directories and odd file lists without a real file system problem.
/// </summary>
public interface IFileEnumerationSource
{
    /// <summary>
    /// Starts an enumeration of all files below <paramref name="basePath"/>. Nothing is read before the first MoveNext.
    /// </summary>
    IFileEnumeration Open(string basePath);
}

/// <summary>
/// The real file system.
/// </summary>
public sealed class FileTreeEnumerationSource : IFileEnumerationSource
{
    /// <summary>The one instance, the source has no state.</summary>
    public static FileTreeEnumerationSource Instance { get; } = new FileTreeEnumerationSource();

    /// <inheritdoc />
    public IFileEnumeration Open(string basePath) => new FileTreeEnumeration(basePath);
}

/// <summary>
/// Enumerates all files below a base path and takes size and timestamps from the directory listing, so no further
/// file system call per file is needed (that matters on hard disks and network shares). The one enumerator of scan, refresh and check.
/// <para>
/// <b>Same files as <c>Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories)</c></b>, which uses
/// <c>EnumerationOptions.CompatibleRecursive</c>: no attribute is skipped (hidden and system files are included, the .NET default for
/// new <see cref="EnumerationOptions"/> would drop them, so <c>AttributesToSkip = 0</c> is set explicitly), "." and ".." are not returned,
/// directory entries are not files. Reparse points are not treated specially: junctions and directory symbolic links are
/// entered like normal directories (their files are returned with the path through the link), file symbolic links are returned as
/// files with the metadata of the link itself. There is no cycle protection, as before. The order is the same too: the files of a
/// directory in listing order, the sub directories after it in the order of the listing (breadth first).
/// </para>
/// <para>
/// <b>Unreadable directories:</b> every directory is listed on its own (an explicit queue of directories, one non-recursive listing each).
/// An exception while opening or reading a directory (access denied, I/O or network error, directory vanished or is a broken link, ...)
/// is recorded in <see cref="UnreadableDirectories"/> together with the path of exactly that directory and the walk continues with the next
/// one. Because every listing belongs to exactly one known directory, the failing directory is never guessed; a recursive
/// <c>FileSystemEnumerator</c> would only report an error code through its <c>ContinueOnError</c> callback without saying which
/// directory it was. Files of a directory that failed half way were already returned, but the directory counts as unreadable.
/// The exception is the base path itself: if it cannot be listed, the exception is thrown by <see cref="MoveNext"/> (as the old
/// enumeration did) because then nothing was enumerated at all.
/// </para>
/// <para>
/// <b>Known limitation:</b> the metadata is that of the directory entry. On NTFS the entry is updated lazily, so for hard linked
/// files (the entry of another link may be stale) and for files that are being written the size and the last write time can be older
/// than the real values that <see cref="FileInfo"/> reads from the file itself. The values are also older by the time the file is hashed,
/// since the enumeration runs ahead of the hashing. A caller that needs stable values has to re-check size and time after reading the
/// file (step 6 of docs/speed-up-plan.md).
/// </para>
/// Not thread safe.
/// </summary>
public sealed class FileTreeEnumeration : IFileEnumeration
{
    private static readonly EnumerationOptions s_listingOptions = new EnumerationOptions
    {
        // One directory per listing, the recursion is done here
        RecurseSubdirectories = false,
        // Hidden and system files are files like all others (the default of EnumerationOptions would skip them)
        AttributesToSkip = 0,
        // Errors must reach us, so the directory can be recorded
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32,
        MatchCasing = MatchCasing.PlatformDefault,
    };

    private readonly Queue<string> _pendingDirectories = new Queue<string>();
    private readonly List<UnreadableDirectory> _unreadable = new List<UnreadableDirectory>();
    private readonly string _basePath;
    private IEnumerator<ListedEntry>? _currentListing;
    private string _currentDirectory = string.Empty;
    private bool _currentIsBase;
    private bool _finished;

    /// <param name="basePath">The directory to walk. Nothing is read before the first <see cref="MoveNext"/>.</param>
    public FileTreeEnumeration(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        _basePath = basePath;
        _pendingDirectories.Enqueue(basePath);
    }

    /// <inheritdoc />
    public FileEntryInfo Current { get; private set; }

    object IEnumerator.Current => Current;

    /// <inheritdoc />
    public IReadOnlyList<UnreadableDirectory> UnreadableDirectories => _unreadable;

    /// <inheritdoc />
    public bool MoveNext()
    {
        if (_finished)
        {
            return false;
        }
        while (true)
        {
            try
            {
                if (_currentListing is null)
                {
                    if (!_pendingDirectories.TryDequeue(out string? directory))
                    {
                        _finished = true;
                        return false;
                    }
                    _currentDirectory = directory;
                    _currentIsBase = ReferenceEquals(directory, _basePath);
                    _currentListing = new FileSystemEnumerable<ListedEntry>(directory, TransformEntry, s_listingOptions).GetEnumerator();
                }

                if (!_currentListing.MoveNext())
                {
                    _currentListing.Dispose();
                    _currentListing = null;
                    continue;
                }

                ListedEntry entry = _currentListing.Current;
                string fullPath = Path.Join(_currentDirectory, entry.Name);
                if (entry.IsDirectory)
                {
                    _pendingDirectories.Enqueue(fullPath);
                    continue;
                }
                Current = new FileEntryInfo(fullPath, entry.Length, entry.CreationTimeUtc, entry.LastWriteTimeUtc);
                return true;
            }
#pragma warning disable CA1031 // Do not catch general exception types: every failure of a listing makes that directory unreadable
            catch (Exception ex)
#pragma warning restore CA1031
            {
                try
                {
                    _currentListing?.Dispose();
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                    // The listing is abandoned anyway
                }
                _currentListing = null;
                if (_currentIsBase)
                {
                    // Nothing could be enumerated from the base path: the caller has to see the error (a caller that would
                    // treat this as "no files" would delete or report everything).
                    _finished = true;
                    _pendingDirectories.Clear();
                    throw;
                }
                _unreadable.Add(new UnreadableDirectory(_currentDirectory, ex));
            }
        }
    }

    /// <inheritdoc />
    public void Reset() => throw new NotSupportedException();

    /// <inheritdoc />
    public void Dispose()
    {
        _currentListing?.Dispose();
        _currentListing = null;
        _pendingDirectories.Clear();
        _finished = true;
    }

    private static ListedEntry TransformEntry(ref FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            return new ListedEntry(entry.FileName.ToString(), true, 0, default, default);
        }
        return new ListedEntry(entry.FileName.ToString(), false, entry.Length, entry.CreationTimeUtc.UtcDateTime, entry.LastWriteTimeUtc.UtcDateTime);
    }

    private readonly record struct ListedEntry(string Name, bool IsDirectory, long Length, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc);
}
