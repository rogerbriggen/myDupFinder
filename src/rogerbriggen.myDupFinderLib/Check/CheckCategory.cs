// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Classification of a single file by the check job, written as the first column of the CSV report.
/// </summary>
public enum CheckCategory
{
    /// <summary>
    /// FileSize and FileLastModificationUTC in the DB match the disk, but the hash differs.
    /// This is the headline bit-rot / silent corruption case.
    /// </summary>
    BitRotSuspect,

    /// <summary>
    /// FileSize or FileLastModificationUTC differs from the DB and the hash differs as well.
    /// A normal edit; legitimate when expected.
    /// </summary>
    Modified,

    /// <summary>
    /// FileSize or FileLastModificationUTC differs but the hash is unchanged.
    /// The file was touched (e.g. mtime rewritten) but its content is the same.
    /// </summary>
    ModifiedNoHashChange,

    /// <summary>
    /// A row in the DB has no matching file on disk.
    /// </summary>
    MissingOnDisk,

    /// <summary>
    /// A file is on disk but no row exists in the DB.
    /// </summary>
    NewOnDisk,
}
