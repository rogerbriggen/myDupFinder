// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Generic;
using System.Globalization;

namespace RogerBriggen.MyDupFinderDB;

/// <summary>
/// Read-only snapshot of the SQLite settings and indexes of an opened database, to know what a run really works with.
/// </summary>
/// <param name="JournalMode">Result of PRAGMA journal_mode (for example "wal" or "delete").</param>
/// <param name="Synchronous">Result of PRAGMA synchronous: 0 = OFF, 1 = NORMAL, 2 = FULL, 3 = EXTRA. This setting belongs to the connection, so it shows what this process uses.</param>
/// <param name="ScanItemsIndexes">Names of all indexes on the ScanItems table (from sqlite_master), sorted by name. Includes automatic indexes such as the primary key.</param>
public sealed record DatabaseDiagnostics(string JournalMode, long Synchronous, IReadOnlyList<string> ScanItemsIndexes)
{
    public string SynchronousName => Synchronous switch
    {
        0 => "OFF",
        1 => "NORMAL",
        2 => "FULL",
        3 => "EXTRA",
        _ => "UNKNOWN",
    };

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "journal_mode={0}, synchronous={1} ({2}), ScanItems indexes=[{3}]",
        JournalMode,
        Synchronous,
        SynchronousName,
        string.Join(", ", ScanItemsIndexes));
}
