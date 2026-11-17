// Roger Briggen license this file to you under the MIT license.
//

using System;

namespace RogerBriggen.MyDupFinderData;

/// <summary>
/// The identity of the volume (disk, partition, mount) that a base path of a scan lay on when it was last verified. Refresh, scan and check compare it with the volume
/// the base path lies on now, so a unmounted or exchanged disk is not mistaken for a tree whose files are all gone.
/// One row per OriginComputer, ScanName and BasePath.
/// </summary>
public class ScanVolumeDto
{
    /// <summary>
    /// ID for database (Primary Key)
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The computer name, where the files are located (the job's OriginComputer).
    /// </summary>
    public string OriginComputer { get; set; } = string.Empty;

    /// <summary>
    /// The job's ScanName.
    /// </summary>
    public string ScanName { get; set; } = string.Empty;

    /// <summary>
    /// The base path (with the trailing directory delimiter), the same string the rows use as PathBase.
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// The identity string of the volume, for example win-volume:..., win-serial:..., linux-uuid:... or mount:...
    /// </summary>
    public string VolumeIdentity { get; set; } = string.Empty;

    /// <summary>
    /// When this identity was recorded for the first time (UTC).
    /// </summary>
    public DateTime FirstRecordedUTC { get; set; }

    /// <summary>
    /// When the volume of the base path was last found to have this identity (UTC).
    /// </summary>
    public DateTime LastVerifiedUTC { get; set; }
}
