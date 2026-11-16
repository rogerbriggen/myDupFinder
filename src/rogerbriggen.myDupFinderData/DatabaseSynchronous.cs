// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderData;

/// <summary>
/// How hard SQLite works to get a committed batch onto the disk (<c>PRAGMA synchronous</c>), the job setting <c>DatabaseSynchronous</c>.
/// See https://www.sqlite.org/pragma.html#pragma_synchronous and https://www.sqlite.org/wal.html.
/// </summary>
public enum DatabaseSynchronous
{
    /// <summary>
    /// The default, also used when the element is missing in the project file. Every commit is synced to the disk before it counts as done:
    /// a committed batch survives a power failure and an operating system crash, in every journal mode.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Faster commits, less syncing. It is only applied when the database is in WAL mode (and not stored on a network location): there the database stays consistent
    /// after a power failure or an operating system crash, but the most recent committed batches (not only the last one) can be lost. The files of the lost batches are
    /// scanned again on the next run because of the resume logic. A crash of the application alone (without a crash of the operating system or a power failure) loses nothing.
    /// In any other situation the setting is ignored (a Warning is logged) and <see cref="Full"/> is used, because in rollback-journal mode <c>NORMAL</c> adds a small risk
    /// of a corrupt database after a power failure on some file systems.
    /// </summary>
    Normal = 1,
}
