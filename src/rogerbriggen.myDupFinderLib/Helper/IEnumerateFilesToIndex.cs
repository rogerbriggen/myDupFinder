// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Concurrent;
using System.Collections.Generic;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib;

public interface IEnumerateFilesToIndex
{
    public int CurrentCount { get; }

    public int ErrorCount { get; }

    public bool HasMore { get; }

    public ConcurrentQueue<ScanItemDto> ScanItemCollection { get; }

    /// <summary>
    /// Directories that could not be read completely so far (complete when <see cref="HasMore"/> is false). The files below them are not enumerated.
    /// </summary>
    public IReadOnlyList<UnreadableDirectory> UnreadableDirectories { get; }

    /// <summary>
    /// Replaces size and timestamps of an item (taken from the directory listing when the batch was made) with the values of the file itself,
    /// unless the job trusts the directory metadata. Call it when the item is processed, right before the resume filter and the hashing.
    /// A failure is counted in <see cref="ErrorCount"/> and logged, and the item is then not counted in <see cref="CurrentCount"/>: the caller must skip it (false).
    /// </summary>
    public bool TryRefreshMetadata(ScanItemDto item);

    public abstract void EnumerateFiles(int maxFiles = 100);
}
