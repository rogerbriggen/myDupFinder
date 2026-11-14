// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Tests the FilenameAndPath index migration: model and snapshot in sync, the upgrade of an existing database,
/// and the query plans of the queries that use the index.
/// </summary>
public class DatabaseIndexMigrationTest : IDisposable
{
    private const string InitialMigration = "20201222171148_InitalCreate";
    private const string FilenameAndPathIndex = "IX_ScanItems_FilenameAndPath";
    private const string BasePath = "/base/";

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly string _dbPath;

    public DatabaseIndexMigrationTest(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "DatabaseIndexMigrationTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static ScanItemDto CreateItem(string name, string hash = "HASH", long size = 100)
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
            FileSize = size,
            FileSha512Hash = hash,
            FileCreationUTC = date,
            FileLastModificationUTC = date,
            FirstScanDateUTC = date,
            LastScanDateUTC = date,
            LastSha512ScanDateUTC = date,
        };
    }

    private string CreateMigratedDatabase(string name = "plan.db")
    {
        string path = Path.Combine(_tempDir, name);
        using var context = DubFinderContextFactory.CreateDubFinderContext(path);
        context.Database.Migrate();
        return path;
    }

    private static List<string> GetScanItemsIndexes(string databaseFile)
    {
        using var connection = new SqliteConnection($"Data Source={databaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'ScanItems' ORDER BY name;";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    // ---------- model and snapshot ----------

    [Fact]
    public void Model_HasNoPendingChangesComparedToTheSnapshot()
    {
        // Uses a context without the "log only" downgrade of PendingModelChangesWarning that the factory sets,
        // so a model change without a migration throws here.
        var options = new DbContextOptionsBuilder<DubFinderContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using var context = new DubFinderContext(options);

        Assert.False(context.Database.HasPendingModelChanges(), "The model differs from the model snapshot: a migration is missing.");
    }

    [Fact]
    public void Migrate_WithoutTheWarningDowngrade_Works()
    {
        // EF Core throws PendingModelChangesWarning from Migrate() if model and snapshot differ. With default options it must not.
        var options = new DbContextOptionsBuilder<DubFinderContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using (var context = new DubFinderContext(options))
        {
            context.Database.Migrate();
        }

        Assert.Contains(FilenameAndPathIndex, GetScanItemsIndexes(_dbPath));
    }

    [Fact]
    public void Migrations_ContainTheFilenameAndPathIndex()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        var migrations = context.Database.GetMigrations().ToList();

        Assert.Equal(InitialMigration, migrations[0]);
        Assert.Contains(migrations, m => m.EndsWith("_AddFilenameAndPathIndex", StringComparison.Ordinal));
    }

    [Fact]
    public void Migrate_ThenDownMigration_DropsTheIndexAndKeepsTheRows()
    {
        string path = CreateMigratedDatabase("down.db");
        var item = CreateItem("a.txt");
        using (var context = DubFinderContextFactory.CreateDubFinderContext(path))
        {
            context.Add(item);
            context.SaveChanges();
        }
        Assert.Contains(FilenameAndPathIndex, GetScanItemsIndexes(path));

        using (var context = DubFinderContextFactory.CreateDubFinderContext(path))
        {
            context.GetService<IMigrator>().Migrate(InitialMigration);
        }
        Assert.DoesNotContain(FilenameAndPathIndex, GetScanItemsIndexes(path));

        using (var context = DubFinderContextFactory.CreateDubFinderContext(path))
        {
            Assert.Equal(item.Id, context.ScanItems!.Single().Id);
        }
    }

    // ---------- upgrade of an existing database ----------

    private static List<string> MigrationMessages(ListLoggerProvider provider) =>
        provider.Entries.Where(e => e.Message.Contains("migration", StringComparison.OrdinalIgnoreCase)).Select(e => e.Message).ToList();

    private static ILogger<ScanJobDBInserts> CreateLogger(ListLoggerProvider provider) =>
        LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)).CreateLogger<ScanJobDBInserts>();

    [Fact]
    public void SetupDB_OnDatabaseFromInitialMigration_AddsIndexKeepsRowsAndLogsMigration()
    {
        // A database as the existing users have it: migrated only to the first migration, with some rows
        var items = new List<ScanItemDto> { CreateItem("a.txt", "H1", 11), CreateItem("sub/b.txt", "H2", 22), CreateItem("sub/c.txt", "H1", 11) };
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath))
        {
            context.GetService<IMigrator>().Migrate(InitialMigration);
            context.ScanItems!.AddRange(items);
            context.SaveChanges();
        }
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain(FilenameAndPathIndex, GetScanItemsIndexes(_dbPath));

        // First open: migrates
        var firstLog = new ListLoggerProvider();
        using (var dbInserts = new ScanJobDBInserts(CreateLogger(firstLog)))
        {
            dbInserts.SetupDB(_dbPath);

            Assert.Contains(FilenameAndPathIndex, dbInserts.GetDatabaseDiagnostics().ScanItemsIndexes);
        }

        var messages = MigrationMessages(firstLog);
        Assert.Equal(2, messages.Count);
        Assert.Contains("AddFilenameAndPathIndex", messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain(InitialMigration, messages[0], StringComparison.Ordinal);
        Assert.Contains("finished in", messages[1], StringComparison.Ordinal);
        Assert.All(firstLog.Entries, e => Assert.Equal(LogLevel.Information, e.Level));

        // All rows unchanged
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath))
        {
            var rows = context.ScanItems!.AsNoTracking().OrderBy(i => i.FilenameAndPath).ToList();
            Assert.Equal(items.Count, rows.Count);
            foreach (var expected in items.OrderBy(i => i.FilenameAndPath))
            {
                var actual = rows.Single(r => r.Id == expected.Id);
                Assert.Equal(expected.FilenameAndPath, actual.FilenameAndPath);
                Assert.Equal(expected.Filename, actual.Filename);
                Assert.Equal(expected.PathBase, actual.PathBase);
                Assert.Equal(expected.ScanExecutionComputer, actual.ScanExecutionComputer);
                Assert.Equal(expected.OriginComputer, actual.OriginComputer);
                Assert.Equal(expected.ScanName, actual.ScanName);
                Assert.Equal(expected.FileSize, actual.FileSize);
                Assert.Equal(expected.FileSha512Hash, actual.FileSha512Hash);
                Assert.Equal(expected.FileCreationUTC, actual.FileCreationUTC);
                Assert.Equal(expected.FileLastModificationUTC, actual.FileLastModificationUTC);
                Assert.Equal(expected.FirstScanDateUTC, actual.FirstScanDateUTC);
                Assert.Equal(expected.LastScanDateUTC, actual.LastScanDateUTC);
                Assert.Equal(expected.LastSha512ScanDateUTC, actual.LastSha512ScanDateUTC);
            }
        }
        SqliteConnection.ClearAllPools();

        // Second open: nothing to migrate, nothing logged
        var secondLog = new ListLoggerProvider();
        using (var dbInserts = new ScanJobDBInserts(CreateLogger(secondLog)))
        {
            dbInserts.SetupDB(_dbPath);
        }
        Assert.Empty(MigrationMessages(secondLog));
    }

    [Fact]
    public void SetupDB_DupFinderDB_LogsMigrationOnlyWhenPending()
    {
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath))
        {
            context.GetService<IMigrator>().Migrate(InitialMigration);
        }
        SqliteConnection.ClearAllPools();

        var log = new ListLoggerProvider();
        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log)).CreateLogger<DubFinderDB>();
        using (var db = new DubFinderDB(logger))
        {
            db.SetupDB(_dbPath);
        }
        Assert.Equal(2, MigrationMessages(log).Count);
        Assert.Contains(FilenameAndPathIndex, GetScanItemsIndexes(_dbPath));
        SqliteConnection.ClearAllPools();

        var secondLog = new ListLoggerProvider();
        var secondLogger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(secondLog)).CreateLogger<DubFinderDB>();
        using (var db = new DubFinderDB(secondLogger))
        {
            db.SetupDB(_dbPath);
        }
        Assert.Empty(MigrationMessages(secondLog));
    }

    // ---------- query plans ----------

    private static void AssertUsesFilenameAndPathIndex(string plan)
    {
        Assert.Matches($"(USING|COVERING) INDEX {FilenameAndPathIndex}", plan);
        Assert.DoesNotContain("SCAN ScanItems", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN s", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryPlan_IsAlreadyInDB_UsesFilenameAndPathIndex()
    {
        string path = CreateMigratedDatabase();
        var si = CreateItem("a.txt");

        string plan = QueryPlanHelper.Explain(path, c => ScanJobDBInserts.CountAlreadyInDB(c, si));

        _output.WriteLine(plan);
        AssertUsesFilenameAndPathIndex(plan);
    }

    [Fact]
    public void QueryPlan_GetItemByPath_UsesFilenameAndPathIndex()
    {
        string path = CreateMigratedDatabase();

        string plan = QueryPlanHelper.Explain(path, c => ScanJobDBInserts.GetItemByPath(c, BasePath + "a.txt"));

        _output.WriteLine(plan);
        AssertUsesFilenameAndPathIndex(plan);
    }

    [Fact]
    public void QueryPlan_IsAlreadyInDB_Count_ReturnsMatchesAndStillWorksWithTheIndex()
    {
        string path = CreateMigratedDatabase();
        var item = CreateItem("a.txt");
        using (var context = DubFinderContextFactory.CreateDubFinderContext(path))
        {
            context.Add(item);
            context.SaveChanges();
        }
        var sameFile = CreateItem("a.txt");
        var otherSize = CreateItem("a.txt", size: 999);

        using var context2 = DubFinderContextFactory.CreateDubFinderContext(path);
        Assert.Equal(1, ScanJobDBInserts.CountAlreadyInDB(context2, sameFile));
        Assert.Equal(0, ScanJobDBInserts.CountAlreadyInDB(context2, otherSize));
    }

    /// <summary>
    /// Not an assertion test: writes the SQL and plan of every query to the test output, so they can be looked at
    /// (dotnet test --logger "console;verbosity=detailed"). Only checks that every plan could be produced.
    /// </summary>
    [Fact]
    public void QueryPlan_AllQueries_AreWrittenToTheOutput()
    {
        string path = CreateMigratedDatabase();
        string second = CreateMigratedDatabase("second.db");
        using (var context = DubFinderContextFactory.CreateDubFinderContext(second))
        {
            context.Add(CreateItem("x.txt", "HASH"));
            context.SaveChanges();
        }
        var si = CreateItem("a.txt");

        var plans = new List<(string Name, string Plan)>
        {
            ("IsEmptyScanItemTable", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.IsEmptyScanItemTable(c))),
            ("IsAlreadyInDB", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.CountAlreadyInDB(c, si))),
            ("IsBasePathAlreadyInDB", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.IsBasePathAlreadyInDB(c, BasePath))),
            ("GetAllItemsByBasePath", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.GetAllItemsByBasePath(c, BasePath))),
            ("GetAllItemsByScanIdentity", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.GetAllItemsByScanIdentity(c, "TestPC", "TestScan"))),
            ("GetItemByPath", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.GetItemByPath(c, BasePath + "a.txt"))),
            ("GetItemsByRelativeSuffix", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.GetItemsByRelativeSuffix(c, "sub/a.txt"))),
            ("GetItemById", QueryPlanHelper.Describe(path, c => ScanJobDBInserts.GetItemById(c, Guid.NewGuid()))),
            ("FindDupsInSameDB", QueryPlanHelper.Describe(path, c => DubFinderDB.FindDupsInSameDB(c))),
            ("GetAllScanItems", QueryPlanHelper.Describe(path, c => DubFinderDB.GetAllScanItems(c))),
        };
        var capture = new SqlCaptureInterceptor();
        using (var context = QueryPlanHelper.CreateCapturingContext(path, capture))
        using (var secondContext = QueryPlanHelper.CreateCapturingContext(second, capture))
        {
            DubFinderDB.FindDupsInDifferentDBs(context, secondContext);
        }
        Assert.Equal(2, capture.Commands.Count);
        plans.Add(("FindDupsInDifferentDBs (1, second db: distinct hashes)", capture.Commands[0].Sql.Trim() + Environment.NewLine + QueryPlanHelper.ExplainSql(second, capture.Commands[0])));
        plans.Add(("FindDupsInDifferentDBs (2, base db: Contains)", capture.Commands[1].Sql.Trim() + Environment.NewLine + QueryPlanHelper.ExplainSql(path, capture.Commands[1])));

        foreach (var (name, plan) in plans)
        {
            _output.WriteLine("=== " + name);
            _output.WriteLine(plan);
        }
        Assert.All(plans, p => Assert.False(string.IsNullOrWhiteSpace(p.Plan)));
    }
}
