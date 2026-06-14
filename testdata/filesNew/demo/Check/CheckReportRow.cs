// Roger Briggen license this file to you under the MIT license.
//

using System;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// One line of the check CSV. All "_DB" / "_Disk" fields are nullable so that
/// rows that only apply to one side (MissingOnDisk has no Disk side, NewOnDisk has no DB side)
/// can still round-trip cleanly through the CSV.
/// </summary>
public sealed class CheckReportRow
{
    public CheckCategory Category { get; set; }

    /// <summary>
    /// True when IgnoreBasePath matched a DB row whose recorded PathBase differs from the
    /// current job's BasePath. applyCheck rewrites PathBase / FilenameAndPath in this case.
    /// </summary>
    public bool PathMoved { get; set; }

    /// <summary>
    /// Primary key of the matched DB row. Guid.Empty for NewOnDisk (no row exists yet).
    /// </summary>
    public Guid ScanItemId { get; set; }

    public string? FilenameAndPathDB { get; set; }
    public string? FilenameAndPathDisk { get; set; }
    public string? PathBaseDB { get; set; }
    public string? PathBaseDisk { get; set; }

    public long? FileSizeDB { get; set; }
    public long? FileSizeDisk { get; set; }

    public DateTime? MTimeDB { get; set; }
    public DateTime? MTimeDisk { get; set; }

    public string? HashDB { get; set; }
    public string? HashDisk { get; set; }

    /// <summary>
    /// Creation time on disk (recorded only for NewOnDisk so applyCheck can store it accurately).
    /// </summary>
    public DateTime? CreationTimeDisk { get; set; }
}
