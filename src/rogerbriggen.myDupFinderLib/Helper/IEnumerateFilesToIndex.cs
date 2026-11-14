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

    public abstract void EnumerateFiles(int maxFiles = 100);
}
