// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections;
using System.Collections.Generic;
using RogerBriggen.MyDupFinderLib;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// A file list that is not read from disk, for tests of unreadable directories and odd file lists.
/// </summary>
internal sealed class FakeFileEnumerationSource : IFileEnumerationSource
{
    /// <summary>The files that the enumeration returns, in this order.</summary>
    public List<FileEntryInfo> Files { get; } = new List<FileEntryInfo>();

    /// <summary>Directories that the enumeration reports as not readable.</summary>
    public List<string> UnreadableDirectories { get; } = new List<string>();

    /// <summary>The unreadable directories are found when the file with this index is about to be returned (default: only at the very end).</summary>
    public int UnreadableFoundAtFileIndex { get; set; } = int.MaxValue;

    /// <summary>Called with the index of the file that is about to be returned.</summary>
    public Action<int>? BeforeFile { get; set; }

    /// <summary>When set, the first MoveNext throws this exception (the base path cannot be listed).</summary>
    public Exception? BasePathError { get; set; }

    /// <summary>The base path that was passed to Open.</summary>
    public string? OpenedBasePath { get; private set; }

    public IFileEnumeration Open(string basePath)
    {
        OpenedBasePath = basePath;
        return new Enumeration(this);
    }

    public static FileEntryInfo Entry(string fullPath, long length, DateTime lastWriteUtc) =>
        new FileEntryInfo(fullPath, length, DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc), DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc));

    private sealed class Enumeration : IFileEnumeration
    {
        private readonly FakeFileEnumerationSource _source;
        private readonly List<UnreadableDirectory> _unreadable = new List<UnreadableDirectory>();
        private int _index = -1;
        private bool _unreadableAdded;

        public Enumeration(FakeFileEnumerationSource source) => _source = source;

        public FileEntryInfo Current { get; private set; }

        object IEnumerator.Current => Current;

        public IReadOnlyList<UnreadableDirectory> UnreadableDirectories => _unreadable;

        public bool MoveNext()
        {
            if (_source.BasePathError is not null)
            {
                throw _source.BasePathError;
            }
            _index++;
            if (_index >= _source.UnreadableFoundAtFileIndex || _index >= _source.Files.Count)
            {
                AddUnreadable();
            }
            if (_index >= _source.Files.Count)
            {
                return false;
            }
            _source.BeforeFile?.Invoke(_index);
            Current = _source.Files[_index];
            return true;
        }

        private void AddUnreadable()
        {
            if (_unreadableAdded)
            {
                return;
            }
            _unreadableAdded = true;
            foreach (string directory in _source.UnreadableDirectories)
            {
                _unreadable.Add(new UnreadableDirectory(directory, new UnauthorizedAccessException("Access to the path is denied (simulated).")));
            }
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
