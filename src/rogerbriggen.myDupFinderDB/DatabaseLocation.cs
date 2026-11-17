// Roger Briggen license this file to you under the MIT license.
//
using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RogerBriggen.MyDupFinderDB;

internal static class DatabaseLocation
{
    /// <summary>
    /// Resolves the database file to a full path (against the current directory, a bare file name like <c>scan.db</c> is a file
    /// in the current directory) and creates its directory if there is one to create. Returns the full path.
    /// <para>
    /// The caller must open the database with the returned full path and not with the original one: then the directory that is
    /// created here and the file that SQLite opens are the same location, even if the current directory changes in between
    /// (SQLite resolves a relative path itself, against the current directory at the time it opens the file).
    /// </para>
    /// </summary>
    public static string PrepareDatabaseFile(string databaseFile)
    {
        string fullPath = Path.GetFullPath(databaseFile);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        return fullPath;
    }

    /// <summary>
    /// Whether the database file lies on a network location, where SQLite's WAL mode must not be used (https://www.sqlite.org/wal.html: all processes must be on the same host,
    /// the shared memory file does not work over a network file system). True for a UNC path (<c>\\server\share\...</c>, also <c>\\?\UNC\...</c>) and for a path on a drive whose
    /// <see cref="DriveInfo.DriveType"/> is <see cref="DriveType.Network"/> (a mapped network drive).
    /// <para>
    /// Not detected: a <c>subst</c> drive (Windows reports the type of the substituted location only as far as the drive letter says, in practice Fixed), a local directory that is a
    /// junction or symbolic link to a share, a share that is mounted as a directory below a local drive, network file systems that the platform reports as a fixed drive (for
    /// example some FUSE or virtualization file systems), and a path whose drive cannot be queried (then false). A wrong answer is not harmful to the data: the journal mode of an existing database is
    /// never changed because of it, it only decides whether a new database starts in DELETE mode and whether a Warning is logged.
    /// </para>
    /// </summary>
    public static bool IsNetworkLocation(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                // Extended-length or device path of a local drive: look at the path behind the prefix
                return IsNetworkLocation(fullPath.Substring(4));
            }
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string? root = Path.GetPathRoot(fullPath);
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }

            // Other platforms: the mount with the longest matching mount point
            DriveInfo? best = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string mountPoint = drive.RootDirectory.FullName;
                if (IsAtOrBelow(fullPath, mountPoint) && (best is null || mountPoint.Length > best.RootDirectory.FullName.Length))
                {
                    best = drive;
                }
            }
            return best?.DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the path is the mount point itself or lies below it. A plain prefix test is not enough: <c>/mnt/data/tmp2/scan.db</c> starts with the mount point
    /// <c>/mnt/data/tmp</c> but is not below it. The root <c>/</c> (a mount point that ends with the separator) contains every path.
    /// </summary>
    internal static bool IsAtOrBelow(string fullPath, string mountPoint, char separator = '/')
    {
        if (string.IsNullOrEmpty(mountPoint) || !fullPath.StartsWith(mountPoint, StringComparison.Ordinal))
        {
            return false;
        }
        return fullPath.Length == mountPoint.Length || mountPoint[^1] == separator || fullPath[mountPoint.Length] == separator;
    }

    /// <summary>
    /// Makes sure a database that does not exist yet is created in rollback-journal mode (<c>DELETE</c>) and not in WAL mode, by creating the (empty) file first.
    /// EF Core switches a database to WAL only when it creates the file itself; a file that exists, even with zero bytes, is left in the mode it has (proven by
    /// <c>DatabaseStateProbeTest</c>). Switching a WAL database back to DELETE afterwards does not work reliably (it fails with "database is locked" while another connection,
    /// also an idle pooled one, is open). No row and no table is written by this.
    /// </summary>
    public static void CreateEmptyRollbackJournalFile(string fullPath)
    {
        // No pooling: the connection must really be closed again
        var connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        string? mode = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not create the database file {fullPath} in rollback-journal mode, SQLite reports journal_mode={mode}.");
        }
    }
}
