// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Linq;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderDB;

/// <summary>
/// The rule for a path that has more than one row in the database (a state that older versions of the scan produced, see "one row per path"):
/// which row is THE row of the path (the primary row), the others are surplus and get removed.
/// <list type="number">
/// <item>A row whose size and last write time (compared as ticks, the kind of the values does not matter: values read back from SQLite have no kind) are the ones of the file now wins over rows that do not match.</item>
/// <item>Among rows of the same class (matching, or not matching) the one with the latest <see cref="ScanItemDto.LastScanDateUTC"/> wins.</item>
/// <item>If that is still a tie, the row whose <see cref="ScanItemDto.Id"/> text (<c>D</c> format, lower case) is the highest in ordinal order wins. Only to make the choice deterministic.</item>
/// </list>
/// Used by the resume filter of the scan, by refresh and by check, so all three keep the same row.
/// </summary>
public static class PrimaryRowChooser
{
    /// <summary>
    /// The identity rule: two rows describe the same file only if FilenameAndPath, OriginComputer AND ScanName are equal. A database can hold the scans of several computers or
    /// several named scans whose path strings are equal (the same drive letter on two PCs), so everything that removes or replaces rows is confined to the rows of the job's
    /// own OriginComputer and ScanName. The comparison is ordinal and exact (like the SQL equality of <c>GetAllItemsByScanIdentity</c>, see the README).
    /// </summary>
    public static bool HasIdentity(ScanItemDto row, string originComputer, string scanName)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Equals(row.OriginComputer, originComputer, StringComparison.Ordinal) && string.Equals(row.ScanName, scanName, StringComparison.Ordinal);
    }

    /// <summary>True when the row has the size and the last write time (ticks) of the file.</summary>
    public static bool Matches(ScanItemDto row, long fileSize, DateTime fileLastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.FileSize == fileSize && row.FileLastModificationUTC.Ticks == fileLastWriteTimeUtc.Ticks;
    }

    /// <summary>Chooses the primary row of the (not empty) list of rows of one path.</summary>
    public static ScanItemDto Choose(IReadOnlyList<ScanItemDto> rows, long fileSize, DateTime fileLastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("There must be at least one row.", nameof(rows));
        }
        ScanItemDto best = rows[0];
        bool bestMatches = Matches(best, fileSize, fileLastWriteTimeUtc);
        for (int i = 1; i < rows.Count; i++)
        {
            ScanItemDto candidate = rows[i];
            bool candidateMatches = Matches(candidate, fileSize, fileLastWriteTimeUtc);
            if (candidateMatches != bestMatches ? candidateMatches : IsLater(candidate, best))
            {
                best = candidate;
                bestMatches = candidateMatches;
            }
        }
        return best;
    }

    /// <summary>Groups the rows by their exact FilenameAndPath (ordinal). The groups are in the order of the first row of each path, the rows of a group keep their order.</summary>
    public static Dictionary<string, List<ScanItemDto>> GroupByPath(IEnumerable<ScanItemDto> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new Dictionary<string, List<ScanItemDto>>(StringComparer.Ordinal);
        foreach (ScanItemDto row in rows)
        {
            if (!groups.TryGetValue(row.FilenameAndPath, out List<ScanItemDto>? group))
            {
                group = new List<ScanItemDto>(1);
                groups.Add(row.FilenameAndPath, group);
            }
            group.Add(row);
        }
        return groups;
    }

    private static bool IsLater(ScanItemDto candidate, ScanItemDto current)
    {
        int byDate = candidate.LastScanDateUTC.Ticks.CompareTo(current.LastScanDateUTC.Ticks);
        if (byDate != 0)
        {
            return byDate > 0;
        }
        return string.CompareOrdinal(candidate.Id.ToString("D"), current.Id.ToString("D")) > 0;
    }
}
