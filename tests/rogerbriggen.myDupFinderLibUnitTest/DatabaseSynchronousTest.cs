// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// What the job setting DatabaseSynchronous does to the connections, and the rules for the journal mode and the location of the database file (step 8).
/// The pragma is read on the connection that EF Core uses for the writes, not on a fresh connection: <c>PRAGMA synchronous</c> belongs to the connection.
/// </summary>
public class DatabaseSynchronousTest : IDisposable
{
    private const string BasePath = "/base/";
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly ListLoggerProvider _log = new ListLoggerProvider();
    private readonly ILoggerFactory _loggerFactory;

    public DatabaseSynchronousTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DatabaseSynchronousTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(_log));
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static ScanItemDto CreateItem(string name)
    {
        var date = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = BasePath + name,
            PathBase = BasePath,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = 100,
            FileSha512Hash = "HASH" + name,
            FileCreationUTC = date,
            FileLastModificationUTC = date,
            FirstScanDateUTC = date,
            LastScanDateUTC = date,
            LastSha512ScanDateUTC = date,
        };
    }

    private ScanJobDBInserts CreateDb(string? file = null, DatabaseSynchronous synchronous = DatabaseSynchronous.Full, Func<string, bool>? isNetwork = null, int batchSize = 1)
    {
        var db = new ScanJobDBInserts(_loggerFactory.CreateLogger<ScanJobDBInserts>(), batchSize, isNetworkLocation: isNetwork ?? (_ => false));
        db.SetupDB(file ?? _dbPath, synchronous);
        return db;
    }

    /// <summary>Reads a pragma on a connection that EF Core opens (so the connection settings were applied to it), and closes it again.</summary>
    private static string Pragma(ScanJobDBInserts db, string pragma)
    {
        var database = db.ContextForTests!.Database;
        database.OpenConnection();
        try
        {
            return PragmaOnOpenConnection(db, pragma);
        }
        finally
        {
            database.CloseConnection();
        }
    }

    private static string PragmaOnOpenConnection(ScanJobDBInserts db, string pragma)
    {
        using var command = db.ContextForTests!.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA " + pragma + ";";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string RawPragma(string file, string pragma)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + pragma + ";";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private int CommittedItemCount()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanItems!.Count();
    }

    private IReadOnlyList<LogEntry> Entries(LogLevel level) => _log.Entries.Where(e => e.Level == level).ToList();

    // ---- synchronous -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Default_IsFull_OnTheConnectionThatFlushes()
    {
        using var db = CreateDb();
        var database = db.ContextForTests!.Database;

        // Hold one EF connection open: the flush transaction runs on exactly this connection
        database.OpenConnection();
        try
        {
            db.Enqueue(CreateItem("a"));
            Assert.Equal(1, db.TotalSuccessCount);
            Assert.Equal("2", PragmaOnOpenConnection(db, "synchronous"));
        }
        finally
        {
            database.CloseConnection();
        }

        Assert.Equal(DatabaseSynchronous.Full, db.EffectiveSynchronous);
        Assert.Equal(2, db.GetDatabaseDiagnostics().Synchronous);
        Assert.Empty(Entries(LogLevel.Warning));
    }

    [Fact]
    public void Normal_IsInEffect_OnTheConnectionThatFlushes_ForAWalDatabase()
    {
        using var db = CreateDb(synchronous: DatabaseSynchronous.Normal);
        var database = db.ContextForTests!.Database;

        database.OpenConnection();
        try
        {
            db.Enqueue(CreateItem("a"));
            Assert.Equal(1, db.TotalSuccessCount);
            Assert.Equal("1", PragmaOnOpenConnection(db, "synchronous"));
            Assert.Equal("wal", PragmaOnOpenConnection(db, "journal_mode"));
        }
        finally
        {
            database.CloseConnection();
        }

        Assert.Equal(DatabaseSynchronous.Normal, db.EffectiveSynchronous);
        var diagnostics = db.GetDatabaseDiagnostics();
        Assert.Equal("NORMAL", diagnostics.SynchronousName);
        Assert.Equal("wal", diagnostics.JournalMode);
        Assert.Empty(Entries(LogLevel.Warning));
        Assert.Equal(1, CommittedItemCount());
    }

    [Fact]
    public void Normal_IsIgnoredWithAWarning_ForARollbackJournalDatabase_AndTheModeStays()
    {
        DatabaseLocation.CreateEmptyRollbackJournalFile(_dbPath);

        using var db = CreateDb(synchronous: DatabaseSynchronous.Normal);
        db.Enqueue(CreateItem("a"));

        Assert.Equal("2", Pragma(db, "synchronous"));
        Assert.Equal(DatabaseSynchronous.Full, db.EffectiveSynchronous);
        Assert.Equal("delete", Pragma(db, "journal_mode"));
        var warning = Assert.Single(Entries(LogLevel.Warning));
        Assert.Contains("DatabaseSynchronous=Normal was ignored", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not in WAL mode", warning.Message, StringComparison.Ordinal);
        Assert.Equal(2, db.GetDatabaseDiagnostics().Synchronous);
        Assert.Equal(1, CommittedItemCount());
    }

    [Fact]
    public void Normal_IsIgnoredWithAWarning_ForAWalDatabaseOnANetworkLocation()
    {
        // An existing WAL database on a network location: its mode is not changed, and Normal is not added to the risk
        using (var create = CreateDb()) { }
        SqliteConnection.ClearAllPools();

        using var db = CreateDb(synchronous: DatabaseSynchronous.Normal, isNetwork: _ => true);

        Assert.Equal("2", Pragma(db, "synchronous"));
        Assert.Equal(DatabaseSynchronous.Full, db.EffectiveSynchronous);
        Assert.Contains(Entries(LogLevel.Warning), e => e.Message.Contains("DatabaseSynchronous=Normal was ignored", StringComparison.Ordinal) && e.Message.Contains("network", StringComparison.Ordinal));
    }

    [Fact]
    public void Value_SurvivesEfClosingAndReopeningTheConnectionBetweenCommands_ForSeveralFlushes()
    {
        foreach (var requested in new[] { DatabaseSynchronous.Normal, DatabaseSynchronous.Full })
        {
            string file = Path.Combine(_tempDir, requested + ".db");
            using var db = CreateDb(file, requested);
            string expected = requested == DatabaseSynchronous.Normal ? "1" : "2";
            for (int i = 0; i < 6; i++)
            {
                // Every flush opens and closes its own connection (nothing is held open); queries in between do the same
                db.Enqueue(CreateItem(requested + i.ToString(CultureInfo.InvariantCulture)));
                Assert.Equal(i + 1, db.TotalSuccessCount);
                Assert.Equal(expected, Pragma(db, "synchronous"));
                _ = db.IsEmptyScanItemTable();
                Assert.Equal(expected, Pragma(db, "synchronous"));
            }
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Value_IsInEffect_InsideTheApplyCheckBatchTransaction()
    {
        using var db = CreateDb(synchronous: DatabaseSynchronous.Normal);
        db.BeginBatch();
        try
        {
            var context = db.ContextForTests!;
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "PRAGMA synchronous;";
            Assert.Equal("1", Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        finally
        {
            db.RollbackBatch();
        }
    }

    [Fact]
    public void PooledConnectionOfAnEarlierNormalUser_DoesNotLeakIntoAFullJob()
    {
        // Pooled native connections keep their pragma: a connection that was set to NORMAL and returned to the pool is handed to the next user of the same connection string
        using (var first = CreateDb(synchronous: DatabaseSynchronous.Normal))
        {
            first.Enqueue(CreateItem("a"));
            Assert.Equal("1", Pragma(first, "synchronous"));
        }

        using var second = CreateDb(synchronous: DatabaseSynchronous.Full);
        second.Enqueue(CreateItem("b"));

        Assert.Equal("2", Pragma(second, "synchronous"));
        Assert.Equal(2, second.GetDatabaseDiagnostics().Synchronous);
    }

    [Fact]
    public void ApplyCheckStyleUse_DefaultsToFull()
    {
        // applyCheck calls SetupDB(file) without a setting
        using var db = new ScanJobDBInserts(null);
        db.SetupDB(_dbPath);

        Assert.Equal("2", Pragma(db, "synchronous"));
    }

    [Fact]
    public void DatabaseStateLine_Diagnostics_ShowTheEffectiveValue()
    {
        using var db = CreateDb(synchronous: DatabaseSynchronous.Normal);

        Assert.Contains("synchronous=1 (NORMAL)", db.GetDatabaseDiagnostics().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Runners_LogTheEffectiveValue_InTheDatabaseStateLine()
    {
        string tree = Path.Combine(_tempDir, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "content");
        string reports = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(reports);

        var scanJob = new MyDupFinderScanJobDTO
        {
            JobName = "Scan",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = tree,
            DatabaseFile = _dbPath,
            ReportPath = reports,
            DatabaseSynchronous = DatabaseSynchronous.Normal,
        };
        MyDupFinderScanJobDTO.FixDto(scanJob);
        ScanTestRunner.Run(scanJob, _log);
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("Database state:", StringComparison.Ordinal) && e.Message.Contains("synchronous=1 (NORMAL)", StringComparison.Ordinal));
        _log.Clear();

        var refreshJob = new MyDupFinderRefreshJobDTO
        {
            JobName = "Refresh",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = scanJob.BasePath,
            DatabaseFile = _dbPath,
            ReportPath = reports,
            DatabaseSynchronous = DatabaseSynchronous.Normal,
        };
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(_log));
        using var provider = services.BuildServiceProvider();
        using (var refresh = new RefreshRunner(refreshJob, provider.GetService<ILogger<RefreshRunner>>(), provider))
        {
            refresh.Start(CancellationToken.None);
        }
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("Database state:", StringComparison.Ordinal) && e.Message.Contains("synchronous=1 (NORMAL)", StringComparison.Ordinal));
        _log.Clear();

        var checkJob = new MyDupFinderCheckJobDTO { ScanJobDTO = scanJob, SkipHashCheck = true };
        using (var check = new CheckRunner(checkJob, provider.GetService<ILogger<CheckRunner>>(), provider))
        {
            check.Start(CancellationToken.None);
        }
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("Database state:", StringComparison.Ordinal) && e.Message.Contains("synchronous=1 (NORMAL)", StringComparison.Ordinal));
    }

    // ---- cache_size and temp_store (step 8, measured) ----------------------------------------------------------------------------------------------

    [Fact]
    public void CacheSize_IsBigOnlyForTheReadOnlyFindDupsJob_AndTheWritingJobsKeepTheSqliteDefault()
    {
        using (var writer = CreateDb())
        {
            Assert.Equal("-2000", Pragma(writer, "cache_size"));
            Assert.Equal("0", Pragma(writer, "temp_store"));
        }
        SqliteConnection.ClearAllPools();

        using var reader = new DubFinderDB(null);
        reader.SetupDB(_dbPath);
        var database = reader.ContextForTests!.Database;
        database.OpenConnection();
        try
        {
            using var command = database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA cache_size;";
            Assert.Equal("-65536", Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture));
            command.CommandText = "PRAGMA temp_store;";
            Assert.Equal("0", Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        finally
        {
            database.CloseConnection();
        }
    }

    // ---- journal mode and location ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NewDatabase_OnALocalDisk_IsCreatedInWalMode_WithoutWarning()
    {
        using (var db = CreateDb())
        {
            Assert.Equal("wal", Pragma(db, "journal_mode"));
        }

        Assert.Empty(Entries(LogLevel.Warning));
    }

    [Fact]
    public void NewDatabase_OnANetworkLocation_EndsUpInDeleteMode_BeforeAnyRowIsWritten()
    {
        var seen = new List<string>();
        using (var db = CreateDb(isNetwork: path =>
        {
            seen.Add(path);
            return true;
        }))
        {
            // Before any row: the mode of the file is already DELETE
            Assert.Equal(0, CommittedItemCount());
            Assert.Equal("delete", RawPragma(_dbPath, "journal_mode"));

            for (int i = 0; i < 5; i++)
            {
                db.Enqueue(CreateItem("n" + i.ToString(CultureInfo.InvariantCulture)));
            }
            Assert.Equal("delete", Pragma(db, "journal_mode"));
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(Path.GetFullPath(_dbPath), Assert.Single(seen));
        Assert.Equal("delete", RawPragma(_dbPath, "journal_mode"));
        Assert.Equal(5, CommittedItemCount());
        Assert.False(File.Exists(_dbPath + "-wal"));
        Assert.False(File.Exists(_dbPath + "-shm"));
        Assert.Empty(Entries(LogLevel.Warning));
        Assert.Contains(Entries(LogLevel.Information), e => e.Message.Contains("network location", StringComparison.Ordinal) && e.Message.Contains("rollback journal", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingWalDatabase_OnANetworkLocation_IsNotConverted_ButWarned()
    {
        using (var create = CreateDb())
        {
            create.Enqueue(CreateItem("a"));
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal("wal", RawPragma(_dbPath, "journal_mode"));

        using (var db = CreateDb(isNetwork: _ => true))
        {
            db.Enqueue(CreateItem("b"));
            Assert.Equal("wal", Pragma(db, "journal_mode"));
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal("wal", RawPragma(_dbPath, "journal_mode"));
        Assert.Equal(2, CommittedItemCount());
        var warning = Assert.Single(Entries(LogLevel.Warning));
        Assert.Contains("WAL", warning.Message, StringComparison.Ordinal);
        Assert.Contains("network", warning.Message, StringComparison.Ordinal);
        Assert.Contains("PRAGMA journal_mode=DELETE;", warning.Message, StringComparison.Ordinal);
        Assert.Contains(_dbPath, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingRollbackJournalDatabase_OnALocalDisk_IsLeftAsItIs_WithAnInformation()
    {
        DatabaseLocation.CreateEmptyRollbackJournalFile(_dbPath);
        using (var first = CreateDb())
        {
            first.Enqueue(CreateItem("a"));
        }
        SqliteConnection.ClearAllPools();
        _log.Clear();

        using (var db = CreateDb())
        {
            db.Enqueue(CreateItem("b"));
            Assert.Equal("delete", Pragma(db, "journal_mode"));
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal("delete", RawPragma(_dbPath, "journal_mode"));
        Assert.Equal(2, CommittedItemCount());
        Assert.Empty(Entries(LogLevel.Warning));
        Assert.Contains(Entries(LogLevel.Information), e => e.Message.Contains("not in WAL mode", StringComparison.Ordinal) && e.Message.Contains("DatabaseSynchronous=Normal has no effect", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, "wal")]
    [InlineData(false, true, "wal")]
    [InlineData(true, false, "delete")]
    [InlineData(true, true, "delete")]
    public void OpeningAnExistingDatabase_NeverChangesItsJournalMode(bool rollbackJournal, bool network, string expectedMode)
    {
        if (rollbackJournal)
        {
            DatabaseLocation.CreateEmptyRollbackJournalFile(_dbPath);
        }
        using (var create = CreateDb())
        {
            create.Enqueue(CreateItem("a"));
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(expectedMode, RawPragma(_dbPath, "journal_mode"));

        foreach (var synchronous in new[] { DatabaseSynchronous.Full, DatabaseSynchronous.Normal })
        {
            using (var db = CreateDb(synchronous: synchronous, isNetwork: _ => network))
            {
                Assert.Equal(expectedMode, Pragma(db, "journal_mode"));
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(expectedMode, RawPragma(_dbPath, "journal_mode"));
        }
    }

    // ---- detection ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\server\share\scan.db", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData("//server/share/scan.db", true)]
    [InlineData(@"\\?\UNC\server\share\scan.db", true)]
    [InlineData(@"\\?\C:\temp\scan.db", false)]
    [InlineData("", false)]
    public void IsNetworkLocation_DetectsUncPaths(string path, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, DatabaseLocation.IsNetworkLocation(path));
    }

    [Fact]
    public void IsNetworkLocation_IsFalseForTheTempDirectory()
    {
        Assert.False(DatabaseLocation.IsNetworkLocation(Path.GetFullPath(_dbPath)));
    }

    [Theory]
    [InlineData("/mnt/data/scan.db", "/mnt/data", true)]
    [InlineData("/mnt/data", "/mnt/data", true)]
    [InlineData("/mnt/data/tmp/scan.db", "/mnt/data/tmp", true)]
    // A prefix of the name only: not below the mount point
    [InlineData("/mnt/data/tmp2/scan.db", "/mnt/data/tmp", false)]
    [InlineData("/mnt/database/scan.db", "/mnt/data", false)]
    [InlineData("/home/scan.db", "/", true)]
    [InlineData("/mnt/data/scan.db", "/mnt/data/", true)]
    [InlineData("/other/scan.db", "/mnt/data", false)]
    [InlineData("/mnt/data/scan.db", "", false)]
    public void IsAtOrBelow_MatchesOnlyAtDirectoryBoundaries(string path, string mountPoint, bool expected)
    {
        Assert.Equal(expected, DatabaseLocation.IsAtOrBelow(path, mountPoint));
    }
}
