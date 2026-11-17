// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderDB;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// A collection that runs alone: other tests call <c>SqliteConnection.ClearAllPools()</c>, which would close the pooled connections that the probes of the connection pool look at.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NotParallelCollection
{
    public const string Name = "NotParallel";
}

/// <summary>
/// Facts about SQLite and EF Core that the database settings (step 8) rely on, each proven with a small probe on a real file.
/// These tests use plain Microsoft.Data.Sqlite connections and the EF context without any of the settings code, so they describe the
/// platform, not this code base's own choices.
/// </summary>
[Collection(NotParallelCollection.Name)]
public class DatabaseStateProbeTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public DatabaseStateProbeTest(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "DatabaseStateProbeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ConnectionString(string file, bool pooling = true) => new SqliteConnectionStringBuilder { DataSource = file, Pooling = pooling }.ToString();

    private static void MigrateWithEf(string file)
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(file);
        context.Database.Migrate();
    }

    private string ModeOf(string file)
    {
        using var connection = new SqliteConnection(ConnectionString(file, pooling: false));
        connection.Open();
        return Scalar(connection, "PRAGMA journal_mode;");
    }

    [Fact]
    public void Probe_DatabaseCreatedByEfMigrate_IsInWalMode()
    {
        string file = Path.Combine(_tempDir, "ef.db");
        MigrateWithEf(file);

        string mode = ModeOf(file);
        _output.WriteLine($"New database created by EF Migrate: journal_mode={mode}");

        Assert.Equal("wal", mode);
    }

    [Fact]
    public void Probe_ExistingRollbackJournalDatabase_KeepsItsModeWhenEfOpensAndMigratesIt()
    {
        string file = Path.Combine(_tempDir, "delete.db");
        using (var connection = new SqliteConnection(ConnectionString(file, pooling: false)))
        {
            connection.Open();
            Assert.Equal("delete", Scalar(connection, "PRAGMA journal_mode;"));
            Execute(connection, "CREATE TABLE Dummy (Id INTEGER);");
        }

        MigrateWithEf(file);
        using (var context = DubFinderContextFactory.CreateDubFinderContext(file))
        {
            _ = context.ScanItems!.Count();
        }

        string mode = ModeOf(file);
        _output.WriteLine($"Existing rollback-journal database after EF Migrate and a query: journal_mode={mode}");

        Assert.Equal("delete", mode);
    }

    [Fact]
    public void Probe_EmptyPreCreatedFileInDeleteMode_StaysInDeleteModeWhenEfMigratesIt()
    {
        // A zero byte file (what a new SQLite database is before the first write) that was opened in DELETE mode
        string file = Path.Combine(_tempDir, "pre.db");
        using (var connection = new SqliteConnection(ConnectionString(file, pooling: false)))
        {
            connection.Open();
            Assert.Equal("delete", Scalar(connection, "PRAGMA journal_mode=DELETE;"));
        }
        _output.WriteLine($"Pre-created file length: {(File.Exists(file) ? new FileInfo(file).Length : -1)}");

        MigrateWithEf(file);

        string mode = ModeOf(file);
        _output.WriteLine($"Zero byte file in DELETE mode, then EF Migrate: journal_mode={mode}");
        Assert.Equal("delete", mode);
        using var context = DubFinderContextFactory.CreateDubFinderContext(file);
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    [Fact]
    public void Probe_WalToDeleteSwitch_RightAfterMigrate_IsNotReliable_OutcomeIsOnlyReported()
    {
        // Observed: with a pooled connection of the same process still open the switch failed with 'database is locked' (after the 30 s busy timeout)
        // in one run and worked in another, so the code does not rely on it: a new database on a network location is created in DELETE mode up front.
        string file = Path.Combine(_tempDir, "switch.db");
        // Migrate through a context; its connection goes back to the pool (still open natively)
        using var context = DubFinderContextFactory.CreateDubFinderContext(file);
        context.Database.Migrate();
        string shortTimeout = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 1 }.ToString();
        using var connection = new SqliteConnection(shortTimeout);
        connection.Open();
        try
        {
            _output.WriteLine("PRAGMA journal_mode=DELETE on a WAL database while a pooled connection is open returned: " + Scalar(connection, "PRAGMA journal_mode=DELETE;"));
        }
        catch (SqliteException ex)
        {
            _output.WriteLine("PRAGMA journal_mode=DELETE on a WAL database while a pooled connection is open failed: " + ex.Message);
        }
    }

    [Fact]
    public void Probe_WalAndShmFiles_ExistWhileAConnectionIsOpen_AndAreRemovedWhenTheLastOneCloses()
    {
        string file = Path.Combine(_tempDir, "files.db");
        string unpooled = ConnectionString(file, pooling: false);
        using (var connection = new SqliteConnection(unpooled))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "CREATE TABLE T (Id INTEGER);");
            Execute(connection, "INSERT INTO T VALUES (1);");
            _output.WriteLine($"Connection open: -wal exists={File.Exists(file + "-wal")} (length {new FileInfo(file + "-wal").Length}), -shm exists={File.Exists(file + "-shm")}");
            Assert.True(File.Exists(file + "-wal"));
            Assert.True(File.Exists(file + "-shm"));
            // The committed row is in the -wal file, not yet in the database file
            Assert.True(new FileInfo(file + "-wal").Length > 0);
        }

        _output.WriteLine($"Last connection closed: -wal exists={File.Exists(file + "-wal")}, -shm exists={File.Exists(file + "-shm")}");
        Assert.False(File.Exists(file + "-wal"));
        Assert.False(File.Exists(file + "-shm"));
    }

    [Fact]
    public void Probe_PooledConnection_KeepsWalAndShmFilesUntilThePoolIsCleared()
    {
        string file = Path.Combine(_tempDir, "pooled.db");
        using (var connection = new SqliteConnection(ConnectionString(file)))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "CREATE TABLE T (Id INTEGER);");
            Execute(connection, "INSERT INTO T VALUES (1);");
        }

        _output.WriteLine($"Pooled connection closed: -wal exists={File.Exists(file + "-wal")}, -shm exists={File.Exists(file + "-shm")}");
        Assert.True(File.Exists(file + "-wal"));
        SqliteConnection.ClearAllPools();
        _output.WriteLine($"After ClearAllPools: -wal exists={File.Exists(file + "-wal")}, -shm exists={File.Exists(file + "-shm")}");
        Assert.False(File.Exists(file + "-wal"));
        Assert.False(File.Exists(file + "-shm"));
    }

    [Fact]
    public void Probe_DefaultSynchronous_InWalAndRollbackMode_AndWhereItIsStored()
    {
        string deleteFile = Path.Combine(_tempDir, "d.db");
        using (var connection = new SqliteConnection(ConnectionString(deleteFile, pooling: false)))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE T (Id INTEGER);");
            string mode = Scalar(connection, "PRAGMA journal_mode;");
            string sync = Scalar(connection, "PRAGMA synchronous;");
            _output.WriteLine($"Rollback mode: journal_mode={mode}, default synchronous={sync}");
            Assert.Equal("delete", mode);
            Assert.Equal("2", sync);
        }

        string walFile = Path.Combine(_tempDir, "w.db");
        using (var connection = new SqliteConnection(ConnectionString(walFile, pooling: false)))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "CREATE TABLE T (Id INTEGER);");
            string sync = Scalar(connection, "PRAGMA synchronous;");
            _output.WriteLine($"WAL mode: default synchronous={sync}");
            Assert.Equal("2", sync);

            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Assert.Equal("1", Scalar(connection, "PRAGMA synchronous;"));
            using var second = new SqliteConnection(ConnectionString(walFile, pooling: false));
            second.Open();
            Assert.Equal("wal", Scalar(second, "PRAGMA journal_mode;"));
            string secondSync = Scalar(second, "PRAGMA synchronous;");
            _output.WriteLine($"Second connection to the same WAL file while the first has NORMAL: synchronous={secondSync}");
            Assert.Equal("2", secondSync);
        }

        using var third = new SqliteConnection(ConnectionString(walFile, pooling: false));
        third.Open();
        Assert.Equal("2", Scalar(third, "PRAGMA synchronous;"));
    }

    [Fact]
    public void Probe_PragmaSynchronous_OnPooledConnection_SurvivesCloseAndReopen_ButNotOnAnotherConnectionObject()
    {
        string file = Path.Combine(_tempDir, "pool.db");
        string connectionString = ConnectionString(file);
        using (var setup = new SqliteConnection(connectionString))
        {
            setup.Open();
            Execute(setup, "PRAGMA journal_mode=WAL;");
            Execute(setup, "CREATE TABLE T (Id INTEGER);");
        }

        using (var first = new SqliteConnection(connectionString))
        {
            first.Open();
            Execute(first, "PRAGMA synchronous=NORMAL;");
        }
        using (var second = new SqliteConnection(connectionString))
        {
            second.Open();
            string reused = Scalar(second, "PRAGMA synchronous;");
            _output.WriteLine($"Pooled: after Close and Open of a new SqliteConnection object, synchronous={reused}");

            using var concurrent = new SqliteConnection(connectionString);
            concurrent.Open();
            string other = Scalar(concurrent, "PRAGMA synchronous;");
            _output.WriteLine($"Pooled: a second connection open at the same time has synchronous={other}");
            Assert.Equal("1", reused);
            Assert.Equal("2", other);
        }

        string unpooled = ConnectionString(file, pooling: false);
        using (var first = new SqliteConnection(unpooled))
        {
            first.Open();
            Execute(first, "PRAGMA synchronous=NORMAL;");
        }
        using var again = new SqliteConnection(unpooled);
        again.Open();
        Assert.Equal("2", Scalar(again, "PRAGMA synchronous;"));
    }

    [Fact]
    public void Probe_PragmaSynchronous_CannotBeChangedInsideATransaction()
    {
        string file = Path.Combine(_tempDir, "tx.db");
        using var connection = new SqliteConnection(ConnectionString(file, pooling: false));
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE T (Id INTEGER);");

        using var transaction = connection.BeginTransaction();
        var ex = Assert.Throws<SqliteException>(() => Execute(connection, "PRAGMA synchronous=NORMAL;"));
        _output.WriteLine("PRAGMA synchronous inside a transaction: " + ex.Message);
        Assert.Contains("transaction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_DefaultCacheSizeAndTempStore()
    {
        string file = Path.Combine(_tempDir, "cache.db");
        using var connection = new SqliteConnection(ConnectionString(file, pooling: false));
        connection.Open();
        Execute(connection, "CREATE TABLE T (Id INTEGER);");
        string cacheSize = Scalar(connection, "PRAGMA cache_size;");
        string pageSize = Scalar(connection, "PRAGMA page_size;");
        string tempStore = Scalar(connection, "PRAGMA temp_store;");
        string version = Scalar(connection, "SELECT sqlite_version();");
        _output.WriteLine($"SQLite {version}: cache_size={cacheSize} (negative = KiB), page_size={pageSize}, temp_store={tempStore} (0 = file/compile default, 1 = file, 2 = memory)");

        Assert.Equal("-2000", cacheSize);
        Assert.Equal("0", tempStore);
    }
}
