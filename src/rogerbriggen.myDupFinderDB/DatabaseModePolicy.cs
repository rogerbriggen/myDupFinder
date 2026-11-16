// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderDB;

/// <summary>
/// Decides which <c>synchronous</c> value a run really uses, from the requested job setting, the journal mode of the database and its location, and logs what it found.
/// It never changes the journal mode of a database (a new database on a network location is created in rollback-journal mode before EF Core touches it, see
/// <see cref="DatabaseLocation.CreateEmptyRollbackJournalFile"/>).
/// </summary>
internal static class DatabaseModePolicy
{
    /// <summary>
    /// Reads the journal mode of the opened database and sets the effective synchronous value in <paramref name="settings"/> (it takes effect on the next connection that EF opens).
    /// </summary>
    /// <param name="existed">The database file existed before this run (otherwise it was created by this run).</param>
    /// <param name="network">The database file is on a network location.</param>
    public static DatabaseSynchronous Apply(
        DubFinderContext context,
        DatabaseConnectionSettings settings,
        DatabaseSynchronous requested,
        bool existed,
        bool network,
        string databaseFile,
        ILogger logger)
    {
        string journalMode = ReadJournalMode(context);
        bool wal = string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase);
        DatabaseSynchronous effective = DatabaseSynchronous.Full;

        if (wal && network && existed)
        {
            logger.LogWarning(
                "Database {DatabaseFile} is in WAL mode but lies on a network location. SQLite's WAL mode must not be used for a database file on a network file system (https://www.sqlite.org/wal.html), the database can get corrupted. The journal mode was NOT changed. To convert it to the rollback journal, run when no job uses the database: sqlite3 \"{DatabaseFileForCommand}\" \"PRAGMA journal_mode=DELETE;\"",
                databaseFile,
                databaseFile);
        }
        else if (wal && network)
        {
            logger.LogWarning("Database {DatabaseFile} was created in WAL mode on a network location, which is not supported by SQLite (https://www.sqlite.org/wal.html).", databaseFile);
        }
        else if (!wal && network && !existed)
        {
            logger.LogInformation(
                "Database {DatabaseFile} is new and lies on a network location: it uses the rollback journal (journal_mode={JournalMode}) because SQLite's WAL mode must not be used on a network file system.",
                databaseFile,
                journalMode);
        }
        else if (!wal)
        {
            logger.LogInformation(
                "Database {DatabaseFile} is not in WAL mode (journal_mode={JournalMode}); the journal mode is left as it is. DatabaseSynchronous=Normal has no effect for it, Full is used.",
                databaseFile,
                journalMode);
        }

        if (requested == DatabaseSynchronous.Normal)
        {
            if (wal && !network)
            {
                effective = DatabaseSynchronous.Normal;
                logger.LogInformation(
                    "DatabaseSynchronous=Normal is applied (database in WAL mode): the database stays consistent after a power failure or a crash of the operating system, but the most recent committed batches can be lost and are scanned again on the next run.");
            }
            else
            {
                logger.LogWarning(
                    "DatabaseSynchronous=Normal was ignored and Full is used, because {Reason}.",
                    wal ? "the database lies on a network location" : string.Create(CultureInfo.InvariantCulture, $"the database is not in WAL mode (journal_mode={journalMode}) and Normal could corrupt it after a power failure on some file systems"));
            }
        }

        settings.Synchronous = effective;
        return effective;
    }

    private static string ReadJournalMode(DubFinderContext context)
    {
        // Through EF, so the connection settings are applied like for every other command
        context.Database.OpenConnection();
        try
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        finally
        {
            context.Database.CloseConnection();
        }
    }
}
