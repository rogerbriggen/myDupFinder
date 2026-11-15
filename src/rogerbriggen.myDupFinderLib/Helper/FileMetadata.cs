// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Reads the metadata of a file from the file itself. Scan, refresh and check decide with these values by default, because the
/// directory entry can be stale (NTFS hard links: a change through another link may not update this link's entry, also after writing has finished).
/// </summary>
internal static class FileMetadata
{
    /// <summary>
    /// Size, creation time and last write time of the file, read with ONE attribute query: <see cref="FileInfo"/> loads its attribute data
    /// once when the first property is read (<c>FileSystemInfo.EnsureDataInitialized</c>, the same <c>FillAttributeInfo</c> that
    /// <see cref="File.GetCreationTimeUtc(string)"/> and <see cref="File.GetLastWriteTimeUtc(string)"/> use) and serves the other properties from that data.
    /// The values are equal to those of the three single calls the code used before step 4 (<c>new FileInfo(p).Length</c>,
    /// <c>File.GetCreationTimeUtc</c>, <c>File.GetLastWriteTimeUtc</c>), the timestamps are UTC.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist (the length is read first, so a missing file always throws).</exception>
    /// <exception cref="IOException">The metadata could not be read.</exception>
    public static FileEntryInfo Read(string path)
    {
        var info = new FileInfo(path);
        long length = info.Length;
        return new FileEntryInfo(path, length, info.CreationTimeUtc, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// The metadata to decide with: the value from the directory listing when <paramref name="trustDirectoryMetadata"/> is set (no call), otherwise
    /// the values of the file itself (one call). Call it when the file is processed, not ahead.
    /// </summary>
    public static FileEntryInfo Resolve(FileEntryInfo listed, bool trustDirectoryMetadata) =>
        trustDirectoryMetadata ? listed : Read(listed.FullPath);
}
