// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderDB;

/// <summary>
/// Applies the pending migrations of a database. A migration can take a long time on a big database
/// (for example building an index over millions of rows), so it is logged when one is applied.
/// </summary>
internal static class DatabaseMigrator
{
    /// <summary>
    /// Runs <c>Database.Migrate()</c>. If migrations are pending, logs them before and the elapsed time after.
    /// Nothing is logged when no migration is pending.
    /// </summary>
    public static void Migrate(DubFinderContext context, ILogger logger, string databaseFile)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        string[] pending = context.Database.GetPendingMigrations().ToArray();
        if (pending.Length == 0)
        {
            // Still call Migrate: it creates the database / history table if they are missing
            context.Database.Migrate();
            return;
        }

        logger.LogInformation(
            "Database {DatabaseFile}: {Count} pending migration(s): {Migrations}. Applying them now, this can take a while for a large database.",
            databaseFile,
            pending.Length,
            string.Join(", ", pending));
        long start = Stopwatch.GetTimestamp();
        context.Database.Migrate();
        logger.LogInformation(
            "Database {DatabaseFile}: migration finished in {ElapsedSeconds:F1} s.",
            databaseFile,
            Stopwatch.GetElapsedTime(start).TotalSeconds);
    }
}
