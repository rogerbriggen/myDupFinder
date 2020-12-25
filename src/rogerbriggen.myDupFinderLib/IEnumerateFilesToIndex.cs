// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Concurrent;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib
{
    public interface IEnumerateFilesToIndex
    {
        public int CurrentCount { get; }

        public int ErrorCount { get; }

        public bool HasMore { get; }

        public ConcurrentQueue<ScanItemDto> ScanItemCollection { get; }

        public abstract void EnumerateFiles(int maxFiles = 100);
    }
}
