// Roger Briggen license this file to you under the MIT license.
//
using System.IO;

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
}
