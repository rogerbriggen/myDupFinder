// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderDB;

/// <summary>
/// The connection settings that are applied whenever EF Core opens a connection. The effective synchronous value can change after the context was created
/// (it is decided once the journal mode of the database is known), so the interceptor reads it from here on every open.
/// </summary>
internal sealed class DatabaseConnectionSettings
{
    /// <summary>
    /// The page cache size, in KiB, of the connections of the read-only find-dups job. Measured in step 8: 64 MiB makes the find-dups queries about 30 to 40 %
    /// faster (the default is 2 MiB, which is too small for the repeated row look-ups of an index scan over 100000 rows), and it is only a limit: SQLite fills the cache
    /// as pages are read, so a small database does not use it. The writing jobs gained nothing from it and keep the SQLite default.
    /// </summary>
    public const int ReadingCacheSizeKiB = 64 * 1024;

    private int _synchronous = (int)DatabaseSynchronous.Full;

    /// <summary>The settings of a context that only reads (find-dups): synchronous Full (never used, nothing is written) and a bigger page cache.</summary>
    public static DatabaseConnectionSettings ForReading() => new DatabaseConnectionSettings { CacheSizeKiB = ReadingCacheSizeKiB };

    /// <summary>The page cache size in KiB that is set on every connection (<c>PRAGMA cache_size</c>, per connection, not stored in the file), or 0 for SQLite's default.</summary>
    public int CacheSizeKiB { get; init; }

    /// <summary>The synchronous value that is set on every connection that is opened. Full until the journal mode of the database is known.</summary>
    public DatabaseSynchronous Synchronous
    {
        get => (DatabaseSynchronous)Volatile.Read(ref _synchronous);
        set => Volatile.Write(ref _synchronous, (int)value);
    }
}

/// <summary>
/// Hook for the benchmarks only: pragma statements that every context created by <see cref="DubFinderContextFactory"/> (with an interceptor) applies on every connection it opens, to measure
/// <c>cache_size</c> and <c>temp_store</c> (step 8). Internal (visible to the test project only), empty by default, never set by production code. A test that sets it must
/// reset it in a finally block / Dispose, and must not run in parallel with other tests that use databases (the benchmarks are opt-in and run alone).
/// </summary>
internal static class MeasurementPragmas
{
    private static IReadOnlyList<string> s_pragmas = Array.Empty<string>();

    public static IReadOnlyList<string> Current
    {
        get => Volatile.Read(ref s_pragmas);
        set => Volatile.Write(ref s_pragmas, value ?? Array.Empty<string>());
    }
}

/// <summary>
/// Sets <c>PRAGMA synchronous</c> (always explicitly, also for Full) on every connection that EF Core opens, right after it was opened and before any command or
/// transaction. Why on every open: the pragma belongs to the connection, not to the database file, and Microsoft.Data.Sqlite pools the native connections, so a pooled
/// connection keeps the value of its last user (a Normal job followed by a Full job in the same process would otherwise write with Normal). SQLite refuses to change it
/// inside a transaction, so it must be set when the connection opens, which also covers the batch flush and the applyCheck batch (EF opens the connection for
/// BeginTransaction through the same path).
/// </summary>
internal sealed class ConnectionPragmaInterceptor : DbConnectionInterceptor
{
    private readonly DatabaseConnectionSettings _settings;

    public ConnectionPragmaInterceptor(DatabaseConnectionSettings settings) => _settings = settings;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private void Apply(DbConnection connection)
    {
        Execute(connection, _settings.Synchronous == DatabaseSynchronous.Normal ? "PRAGMA synchronous=NORMAL;" : "PRAGMA synchronous=FULL;");
        if (_settings.CacheSizeKiB > 0)
        {
            Execute(connection, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PRAGMA cache_size=-{_settings.CacheSizeKiB};"));
        }
        // Only the benchmarks put anything here (for example "PRAGMA cache_size=-65536;")
        foreach (string pragma in MeasurementPragmas.Current)
        {
            Execute(connection, pragma);
        }
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
