// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Finds the database row for a file that is not at the path where it was scanned, by the path relative to the base path
/// (check with IgnoreBasePath). The rows are indexed once by their relative path, so a lookup does not walk all rows.
/// <para>
/// The result is the same as walking all candidates in their order for each file: the first row (in the order of the candidates) that is not claimed
/// yet (not in the <c>seenIds</c> set), whose <c>PathBase</c> is not the current base path, and whose path relative to its own <c>PathBase</c> is equal
/// (ordinal) to the relative path of the file. A row whose path is not below its own <c>PathBase</c> never matches. The rows in one list keep
/// their order, and the claimed state is looked up at the time of the call, so the claims that happen while the files are processed are respected.
/// </para>
/// </summary>
internal sealed class RelativePathIndex
{
    private readonly string _basePath;
    private readonly Dictionary<string, List<ScanItemDto>> _byRelativePath = new Dictionary<string, List<ScanItemDto>>(StringComparer.Ordinal);

    /// <param name="candidates">All rows that may be matched, in the order that decides the tie-breaking.</param>
    /// <param name="basePath">The current base path of the run.</param>
    public RelativePathIndex(IEnumerable<ScanItemDto> candidates, string basePath)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(basePath);
        _basePath = basePath;
        foreach (var candidate in candidates)
        {
            // Rows under the current base path are found by the exact path lookup
            if (string.Equals(candidate.PathBase, basePath, StringComparison.Ordinal))
            {
                continue;
            }
            var relative = GetRelativePathIfUnderBase(candidate.FilenameAndPath, candidate.PathBase);
            if (relative is null)
            {
                continue;
            }
            if (!_byRelativePath.TryGetValue(relative, out var list))
            {
                list = new List<ScanItemDto>(1);
                _byRelativePath.Add(relative, list);
            }
            list.Add(candidate);
        }
    }

    /// <returns>The first unclaimed row with the same relative path, or null.</returns>
    public ScanItemDto? Find(string currentFile, HashSet<Guid> seenIds)
    {
        var relative = GetRelativePathIfUnderBase(currentFile, _basePath);
        if (relative is null)
        {
            return null;
        }
        if (!_byRelativePath.TryGetValue(relative, out var list))
        {
            return null;
        }
        foreach (var candidate in list)
        {
            if (seenIds.Contains(candidate.Id))
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    /// <summary>
    /// The path below the base path without leading separators, or null when the path does not start with the base path (plain ordinal prefix test, as before).
    /// </summary>
    internal static string? GetRelativePathIfUnderBase(string path, string basePath)
    {
        if (!path.StartsWith(basePath, StringComparison.Ordinal))
        {
            return null;
        }
        return path.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
