// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Tests the cross-database only-dups query of step 5 (second database attached to the connection of the base database):
/// same result as the old implementation, the second database is not modified, connection handling, and the query plans.
/// </summary>
public class FindDupsAttachedQueryTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly string _baseDb;
    private readonly string _secondDb;

    public FindDupsAttachedQueryTest(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "FindDupsAttachedQueryTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _baseDb = Path.Combine(_tempDir, "base.db");
        _secondDb = Path.Combine(_tempDir, "second.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static ScanItemDto B(string rel, string hash, string key) => FindDupsTestData.Row("/b", rel, hash, 10, key);

    private static ScanItemDto S(string rel, string hash, string key) => FindDupsTestData.Row("/s", rel, hash, 10, key);

    private void Write(string file, IEnumerable<ScanItemDto> rows, string? targetMigration = null)
    {
        FindDupsTestData.WriteDatabase(file, rows, targetMigration);
        SqliteConnection.ClearAllPools();
    }

    private static string Describe(ScanItemDto i) =>
        string.Join("|", i.Id, i.FilenameAndPath, i.Filename, i.PathBase, i.ScanExecutionComputer, i.OriginComputer, i.ScanName, i.FileSize,
            i.FileCreationUTC.Ticks, i.FileLastModificationUTC.Ticks, i.FileSha512Hash, i.FirstScanDateUTC.Ticks, i.LastScanDateUTC.Ticks, i.LastSha512ScanDateUTC.Ticks);

    /// <summary>The total order of the queries: hash, path, primary key (ordinal, as SQLite compares the text of the columns).</summary>
    private static List<ScanItemDto> InTotalOrder(IEnumerable<ScanItemDto> items) =>
        items.OrderBy(i => i.FileSha512Hash, StringComparer.Ordinal)
            .ThenBy(i => i.FilenameAndPath, StringComparer.Ordinal)
            .ThenBy(i => i.Id.ToString("D").ToUpperInvariant(), StringComparer.Ordinal)
            .ToList();

    private static List<ScanItemDto>? Reference(string baseDb, string secondDb)
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(baseDb);
        using var second = DubFinderContextFactory.CreateDubFinderContext(secondDb);
        return ReferenceFindDups.FindDupsInDifferentDBs(context, second);
    }

    private static List<ScanItemDto>? Attached(string baseDb, string secondDb, DubFinderDB.HashMatchForm? form = null)
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(baseDb);
        return DubFinderDB.FindDupsInDifferentDBs(context, secondDb, form);
    }

    /// <summary>The query has to give the result of the old implementation in the form chosen by the code and in each of the two forms.</summary>
    private void AssertSameAsReference(string baseDb, string secondDb)
    {
        var expected = Reference(baseDb, secondDb);
        foreach (DubFinderDB.HashMatchForm? form in new DubFinderDB.HashMatchForm?[] { null, DubFinderDB.HashMatchForm.Exists, DubFinderDB.HashMatchForm.In })
        {
            var actual = Attached(baseDb, secondDb, form);

            if (expected is null)
            {
                Assert.Null(actual);
                continue;
            }
            Assert.NotNull(actual);

            // Same rows (all columns), in the total order, which is the order by hash of the old implementation with the ties resolved
            Assert.Equal(InTotalOrder(expected).Select(Describe), actual.Select(Describe));
            Assert.True(actual.Select(a => a.FileSha512Hash).SequenceEqual(expected.Select(e => e.FileSha512Hash).OrderBy(h => h, StringComparer.Ordinal), StringComparer.Ordinal));
            Assert.DoesNotContain(actual, a => string.IsNullOrEmpty(a.FileSha512Hash));
        }
    }

    [Theory]
    [InlineData(2000, 10, 1.0)]
    [InlineData(2000, 10, 0.0)]
    [InlineData(2000, 50, 0.3)]
    [InlineData(10, 2000, 1.0)]
    [InlineData(10, 2000, 0.0)]
    [InlineData(50, 2000, 0.3)]
    [InlineData(2000, 2000, 0.5)]
    public void Attached_GivesTheResultOfTheOldImplementation_StronglyAsymmetricPairs(int baseRows, int secondRows, double share)
    {
        int common = (int)(secondRows * share);
        // the base rows also contain copies (two rows with the same hash) and some rows without hash
        Write(_baseDb, Enumerable.Range(0, baseRows).Select(i => B("f" + i, i % 97 == 0 ? string.Empty : FindDupsTestData.HashOf("c" + (i % 1500)), "b" + i)));
        Write(_secondDb, Enumerable.Range(0, secondRows).Select(j => S("f" + j, j % 89 == 0 ? string.Empty : FindDupsTestData.HashOf(j < common ? "c" + (j * 7 % 1500) : "x" + j), "s" + j)));

        AssertSameAsReference(_baseDb, _secondDb);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("in")]
    public void Attached_EveryForm_LeavesTheSecondDatabaseAndTheConnectionAsItWas(string formName)
    {
        var form = formName == "in" ? DubFinderDB.HashMatchForm.In : DubFinderDB.HashMatchForm.Exists;
        PrepareSecond("wal");
        string secondBefore = FileSha256(_secondDb);
        var filesBefore = Snapshot().Select(f => f.Name).ToList();

        using (var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb))
        {
            var first = DubFinderDB.FindDupsInDifferentDBs(context, _secondDb, form);
            var again = DubFinderDB.FindDupsInDifferentDBs(context, _secondDb, form);
            Assert.NotEmpty(first!);
            Assert.Equal(first!.Select(Describe), again!.Select(Describe));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
            Assert.Equal(new[] { "main" }, AttachedDatabases(context));
            Assert.Equal(300, DubFinderDB.GetAllScanItems(context).Count);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        using (var next = DubFinderContextFactory.CreateDubFinderContext(_baseDb))
        {
            Assert.Equal(new[] { "main" }, AttachedDatabases(next));
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(secondBefore, FileSha256(_secondDb));
        Assert.Equal(filesBefore, Snapshot().Select(f => f.Name).ToList());
    }

    [Theory]
    [InlineData(2000, 10, "in")]
    [InlineData(2000, 600, "in")]
    [InlineData(2000, 700, "exists")]
    [InlineData(2000, 2000, "exists")]
    [InlineData(10, 2000, "exists")]
    [InlineData(0, 100, "exists")]
    public void Attached_ChoosesTheFormFromTheSizesOfTheTwoTables(int baseRows, int secondRows, string expectedForm)
    {
        Write(_baseDb, Enumerable.Range(0, baseRows).Select(i => B("f" + i, FindDupsTestData.HashOf("c" + i), "b" + i)));
        Write(_secondDb, Enumerable.Range(0, secondRows).Select(j => S("f" + j, FindDupsTestData.HashOf("c" + j), "s" + j)));
        var capture = new SqlCaptureInterceptor();
        using (var context = QueryPlanHelper.CreateCapturingContext(_baseDb, capture))
        {
            DubFinderDB.FindDupsInDifferentDBs(context, _secondDb);
        }

        var match = capture.Commands.Single(c => c.Sql.Contains("dupfinder_second.ScanItems AS o WHERE", StringComparison.Ordinal) && c.Sql.Contains("ORDER BY", StringComparison.Ordinal));
        Assert.Equal(expectedForm == "in", match.Sql.Contains("IN (SELECT o.FileSha512Hash", StringComparison.Ordinal));
        Assert.Equal(expectedForm == "exists", match.Sql.Contains("EXISTS (SELECT 1", StringComparison.Ordinal));
    }

    // ---------- equivalence with the old implementation ----------

    private static (List<ScanItemDto> Base, List<ScanItemDto> Second) Scenario(string name) => name switch
    {
        "overlapping" => (
            new List<ScanItemDto> { B("a", "H1", "b1"), B("b", "H2", "b2"), B("c", "H3", "b3"), B("d", "H4", "b4") },
            new List<ScanItemDto> { S("x", "H2", "s1"), S("y", "H4", "s2"), S("z", "H9", "s3") }),
        "nothing in common" => (
            new List<ScanItemDto> { B("a", "H1", "b1"), B("b", "H2", "b2") },
            new List<ScanItemDto> { S("x", "H8", "s1"), S("y", "H9", "s2") }),
        "hashes only on one side" => (
            new List<ScanItemDto> { B("a", "H1", "b1"), B("b", "H2", "b2"), B("c", "H3", "b3") },
            new List<ScanItemDto> { S("x", "H3", "s1"), S("y", "H7", "s2"), S("z", "H8", "s3") }),
        "duplicates on both sides" => (
            new List<ScanItemDto> { B("a", "H1", "b1"), B("a2", "H1", "b2"), B("a3", "H1", "b3"), B("b", "H2", "b4"), B("c", "H2", "b5") },
            new List<ScanItemDto> { S("x", "H1", "s1"), S("x2", "H1", "s2"), S("y", "H2", "s3"), S("y2", "H2", "s4"), S("y3", "H2", "s5") }),
        "empty hash rows on both sides" => (
            new List<ScanItemDto> { B("a", string.Empty, "b1"), B("b", string.Empty, "b2"), B("c", "H1", "b3") },
            new List<ScanItemDto> { S("x", string.Empty, "s1"), S("y", "H1", "s2"), S("z", string.Empty, "s3") }),
        "empty hashes only in base" => (
            new List<ScanItemDto> { B("a", string.Empty, "b1"), B("b", "H1", "b2") },
            new List<ScanItemDto> { S("x", "H1", "s1") }),
        "second has only empty hashes" => (
            new List<ScanItemDto> { B("a", string.Empty, "b1"), B("b", "H1", "b2") },
            new List<ScanItemDto> { S("x", string.Empty, "s1"), S("y", string.Empty, "s2") }),
        "second is empty" => (
            new List<ScanItemDto> { B("a", "H1", "b1"), B("b", string.Empty, "b2") },
            new List<ScanItemDto>()),
        "base is empty" => (
            new List<ScanItemDto>(),
            new List<ScanItemDto> { S("x", "H1", "s1"), S("y", string.Empty, "s2") }),
        "both are empty" => (new List<ScanItemDto>(), new List<ScanItemDto>()),
        "same hash and same path in base" => (
            new List<ScanItemDto> { B("a", "H1", "b1"), B("a", "H1", "b2"), B("a", "H1", "b3"), B("a", "H0", "b4") },
            new List<ScanItemDto> { S("x", "H1", "s1") }),
        "hash differing in case only" => (
            new List<ScanItemDto> { B("a", "abc", "b1"), B("b", "ABC", "b2") },
            new List<ScanItemDto> { S("x", "ABC", "s1") }),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static IEnumerable<object[]> ScenarioNames() => new[]
    {
        "overlapping", "nothing in common", "hashes only on one side", "duplicates on both sides", "empty hash rows on both sides",
        "empty hashes only in base", "second has only empty hashes", "second is empty", "base is empty", "both are empty",
        "same hash and same path in base", "hash differing in case only",
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Attached_GivesTheResultOfTheOldImplementation_HandMadeScenarios(string scenario)
    {
        var (baseRows, secondRows) = Scenario(scenario);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows);

        AssertSameAsReference(_baseDb, _secondDb);
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(2, 400)]
    [InlineData(3, 400)]
    [InlineData(4, 1500)]
    [InlineData(5, 2)]
    public void Attached_GivesTheResultOfTheOldImplementation_GeneratedDatabasePairs(int seed, int rows)
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(rows, seed);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows);

        AssertSameAsReference(_baseDb, _secondDb);
        var result = Attached(_baseDb, _secondDb);
        if (rows >= 50)
        {
            Assert.NotNull(result);
            Assert.NotEmpty(result);
        }
    }

    [Fact]
    public void Attached_ReturnsNullOnlyWhenTheSecondDatabaseHasNoNonEmptyHash_AnEmptyListWhenNothingMatches()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        Write(_secondDb, new[] { S("x", "H2", "s1") });
        var noMatch = Attached(_baseDb, _secondDb);
        Assert.NotNull(noMatch);
        Assert.Empty(noMatch);

        File.Delete(_secondDb);
        Write(_secondDb, new[] { S("y", string.Empty, "s2") });
        Assert.Null(Attached(_baseDb, _secondDb));
    }

    [Fact]
    public void Attached_RowsWithoutHashInTheBaseDatabaseNeverMatch()
    {
        Write(_baseDb, new[] { B("a", string.Empty, "b1"), B("b", string.Empty, "b2"), B("c", "H1", "b3") });
        Write(_secondDb, new[] { S("x", string.Empty, "s1"), S("y", "H1", "s2") });

        var result = Attached(_baseDb, _secondDb);

        Assert.NotNull(result);
        Assert.Equal(new[] { "/b/c" }, result.Select(r => r.FilenameAndPath));
    }

    [Fact]
    public void Attached_ResultIsInTheTotalOrder_HashPathAndId()
    {
        // same hash and same path three times, only the primary key differs; inserted in the "wrong" order
        var rows = new[]
        {
            B("p", "H2", "k3"), B("p", "H1", "k2"), B("q", "H1", "k1"), B("p", "H1", "k5"), B("p", "H1", "k4"),
        };
        rows[0].Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        rows[1].Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        rows[2].Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        rows[3].Id = Guid.Parse("00000000-0000-0000-0000-00000000000F");
        rows[4].Id = Guid.Parse("00000000-0000-0000-0000-00000000000A");
        Write(_baseDb, rows);
        Write(_secondDb, new[] { S("x", "H1", "s1"), S("y", "H2", "s2") });

        var result = Attached(_baseDb, _secondDb)!;

        Assert.Equal(
            new[] { "/b/p|...02", "/b/p|...0A", "/b/p|...0F", "/b/q|...01", "/b/p|...03" },
            result.Select(r => r.FilenameAndPath + "|..." + r.Id.ToString("D").ToUpperInvariant().Substring(34)));
    }

    // ---------- the second database is not modified ----------

    private static string FileSha256(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record FileState(string Name, long Length, DateTime LastWriteUtc, string Sha256);

    private List<FileState> Snapshot() =>
        Directory.GetFiles(_tempDir).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new FileState(Path.GetFileName(f), new FileInfo(f).Length, File.GetLastWriteTimeUtc(f), FileSha256(f)))
            .ToList();

    private void PrepareSecond(string kind)
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(300, 7);
        Write(_baseDb, baseRows);
        switch (kind)
        {
            case "wal":
                Write(_secondDb, secondRows);
                Assert.Equal("wal", FindDupsTestData.SetJournalMode(_secondDb, "WAL"));
                break;
            case "delete":
                Write(_secondDb, secondRows);
                Assert.Equal("delete", FindDupsTestData.SetJournalMode(_secondDb, "DELETE"));
                break;
            case "old":
                Write(_secondDb, secondRows, FindDupsTestData.InitialMigration);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("wal")]
    [InlineData("delete")]
    [InlineData("old")]
    public void Attached_DoesNotChangeTheSecondDatabaseFile(string kind)
    {
        PrepareSecond(kind);
        string secondBefore = FileSha256(_secondDb);
        var before = Snapshot();
        _output.WriteLine("Files before: " + string.Join(", ", before.Select(f => f.Name + " " + f.Length)));

        // No other connection has the second database open: this is the case where SQLite could write a checkpoint when it closes the file.
        using (var db = new DubFinderDB(null))
        {
            db.SetupDB(_baseDb);
            var result = db.FindDupsInDifferentDBs(_secondDb);
            Assert.NotNull(result);
            Assert.NotEmpty(result);
        }
        SqliteConnection.ClearAllPools();

        var after = Snapshot();
        _output.WriteLine("Files after:  " + string.Join(", ", after.Select(f => f.Name + " " + f.Length)));
        Assert.Equal(secondBefore, FileSha256(_secondDb));
        Assert.Equal(before.Single(f => f.Name == "second.db"), after.Single(f => f.Name == "second.db"));
        // No file is left behind next to the second database (no journal, wal or shm)
        Assert.Equal(before.Select(f => f.Name), after.Select(f => f.Name));
        // and the second database still contains exactly what was written
        using var verify = DubFinderContextFactory.CreateDubFinderContext(_secondDb);
        Assert.Equal(300, verify.ScanItems!.Count());
    }

    [Fact]
    public void Attached_OnADatabaseOfAnOlderVersion_NeedsNoMigrationAndDoesNotMigrate()
    {
        PrepareSecond("old");
        string secondBefore = FileSha256(_secondDb);
        AssertIndexes(_secondDb, hasFilenameAndPathIndex: false);

        AssertSameAsReference(_baseDb, _secondDb);

        Assert.Equal(secondBefore, FileSha256(_secondDb));
        AssertIndexes(_secondDb, hasFilenameAndPathIndex: false);
        Assert.Equal(1, MigrationHistoryRows(_secondDb));
    }

    private static void AssertIndexes(string file, bool hasFilenameAndPathIndex)
    {
        using var connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='ScanItems'";
        var names = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }
        Assert.Contains("IX_ScanItems_FileSha512Hash", names);
        Assert.Equal(hasFilenameAndPathIndex, names.Contains("IX_ScanItems_FilenameAndPath"));
    }

    private static long MigrationHistoryRows(string file)
    {
        using var connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
        return (long)command.ExecuteScalar()!;
    }

    // ---------- journal modes ----------

    [Theory]
    [InlineData("WAL", "DELETE")]
    [InlineData("DELETE", "WAL")]
    [InlineData("DELETE", "DELETE")]
    [InlineData("WAL", "WAL")]
    public void Attached_WorksWhenTheTwoDatabasesHaveDifferentJournalModes(string baseMode, string secondMode)
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(300, 11);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows);
        FindDupsTestData.SetJournalMode(_baseDb, baseMode);
        FindDupsTestData.SetJournalMode(_secondDb, secondMode);
        SqliteConnection.ClearAllPools();
        Assert.Equal(baseMode.ToLowerInvariant(), FindDupsTestData.GetJournalMode(_baseDb));
        Assert.Equal(secondMode.ToLowerInvariant(), FindDupsTestData.GetJournalMode(_secondDb));
        SqliteConnection.ClearAllPools();
        string secondBefore = FileSha256(_secondDb);

        AssertSameAsReference(_baseDb, _secondDb);
        SqliteConnection.ClearAllPools();

        Assert.Equal(secondBefore, FileSha256(_secondDb));
        Assert.Equal(baseMode.ToLowerInvariant(), FindDupsTestData.GetJournalMode(_baseDb));
        Assert.Equal(secondMode.ToLowerInvariant(), FindDupsTestData.GetJournalMode(_secondDb));
    }

    // ---------- connection handling ----------

    private static List<string> AttachedDatabases(DubFinderContext context) =>
        context.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_database_list ORDER BY seq").ToList();

    [Fact]
    public void Attached_CanRunTwiceInARowAndTheContextStaysUsable()
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(200, 21);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows);
        using var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb);
        var connection = context.Database.GetDbConnection();
        Assert.Equal(ConnectionState.Closed, connection.State);

        var first = DubFinderDB.FindDupsInDifferentDBs(context, _secondDb);
        Assert.Equal(ConnectionState.Closed, connection.State);
        var second = DubFinderDB.FindDupsInDifferentDBs(context, _secondDb);
        Assert.Equal(ConnectionState.Closed, connection.State);

        Assert.NotNull(first);
        Assert.NotEmpty(first);
        Assert.Equal(first.Select(Describe), second!.Select(Describe));

        // another query on the same context, afterwards, and the second database is not attached any more
        Assert.Equal(200, DubFinderDB.GetAllScanItems(context).Count);
        Assert.Equal(new[] { "main" }, AttachedDatabases(context));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(first.Select(Describe), DubFinderDB.FindDupsInDifferentDBs(context, _secondDb)!.Select(Describe));
    }

    [Fact]
    public void Attached_LeavesAConnectionOpenThatTheCallerOpened_AndDetaches()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        Write(_secondDb, new[] { S("x", "H1", "s1") });
        using var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb);
        context.Database.OpenConnection();
        try
        {
            var result = DubFinderDB.FindDupsInDifferentDBs(context, _secondDb);

            Assert.Single(result!);
            Assert.Equal(ConnectionState.Open, context.Database.GetDbConnection().State);
            Assert.Equal(new[] { "main" }, AttachedDatabases(context));
        }
        finally
        {
            context.Database.CloseConnection();
        }
    }

    [Fact]
    public void Attached_DoesNotLeaveTheAttachmentInTheConnectionPool()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        Write(_secondDb, new[] { S("x", "H1", "s1") });
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb))
        {
            Assert.Single(DubFinderDB.FindDupsInDifferentDBs(context, _secondDb)!);
        }

        // The next context with the same connection string gets the pooled connection of the first one
        using var next = DubFinderContextFactory.CreateDubFinderContext(_baseDb);
        Assert.Equal(new[] { "main" }, AttachedDatabases(next));
    }

    [Fact]
    public void Attached_WhenTheQueryFails_DetachesAndTheContextStaysUsable()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        // a valid SQLite database without the ScanItems table
        using (var connection = new SqliteConnection($"Data Source={_secondDb};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Other (X INTEGER)";
            command.ExecuteNonQuery();
        }
        using var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb);

        Assert.Throws<SqliteException>(() => DubFinderDB.FindDupsInDifferentDBs(context, _secondDb));

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Equal(new[] { "main" }, AttachedDatabases(context));
        Assert.Single(DubFinderDB.GetAllScanItems(context));
    }

    [Fact]
    public void Attached_WhenTheSecondFileIsNotADatabase_ThrowsAndTheContextStaysUsable()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        File.WriteAllText(_secondDb, "this is not a database, and it is long enough to be looked at by sqlite" + new string('x', 200));
        string before = FileSha256(_secondDb);
        using var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb);

        Assert.Throws<SqliteException>(() => DubFinderDB.FindDupsInDifferentDBs(context, _secondDb));

        Assert.Equal(new[] { "main" }, AttachedDatabases(context));
        Assert.Single(DubFinderDB.GetAllScanItems(context));
        Assert.Equal(before, FileSha256(_secondDb));
    }

    // ---------- missing file, same file, special characters ----------

    [Fact]
    public void Attached_WhenTheSecondFileDoesNotExist_ThrowsAndDoesNotCreateIt()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        string missing = Path.Combine(_tempDir, "missing.db");
        var before = Directory.GetFiles(_tempDir).OrderBy(f => f).ToList();

        using (var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb))
        {
            var ex = Assert.Throws<FileNotFoundException>(() => DubFinderDB.FindDupsInDifferentDBs(context, missing));
            Assert.Contains("missing.db", ex.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "main" }, AttachedDatabases(context));
        }
        using (var db = new DubFinderDB(null))
        {
            db.SetupDB(_baseDb);
            Assert.Throws<FileNotFoundException>(() => db.FindDupsInDifferentDBs(missing));
            Assert.Throws<FileNotFoundException>(() => db.GetAllScanItemsFromOtherDB(missing));
        }
        SqliteConnection.ClearAllPools();

        Assert.False(File.Exists(missing));
        Assert.Equal(before, Directory.GetFiles(_tempDir).OrderBy(f => f).ToList());
    }

    [Fact]
    public void Attached_WhenTheSecondPathIsADirectory_Throws()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        using var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb);

        Assert.Throws<FileNotFoundException>(() => DubFinderDB.FindDupsInDifferentDBs(context, _tempDir));
    }

    [Fact]
    public void Attached_WhenTheSecondDatabaseIsTheBaseDatabase_GivesTheResultOfTheOldImplementation()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1"), B("b", "H1", "b2"), B("c", "H2", "b3"), B("d", string.Empty, "b4") });

        AssertSameAsReference(_baseDb, _baseDb);

        // every row with a hash is its own match
        var result = Attached(_baseDb, _baseDb)!;
        Assert.Equal(3, result.Count);
        // also with another spelling of the same path
        string other = Path.Combine(_tempDir, ".", "base.db");
        using var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb);
        Assert.Equal(3, DubFinderDB.FindDupsInDifferentDBs(context, other)!.Count);
    }

    [Fact]
    public void Attached_PathWithSpecialCharacters_IsPassedAsAParameter()
    {
        // quote, semicolon, hash sign, percent sign and non-ASCII characters in the directory name
        string special = Path.Combine(_tempDir, "it's #1; 100% caf\u00e9 \u00fcber");
        Directory.CreateDirectory(special);
        string secondDb = Path.Combine(special, "second's.db");
        Write(_baseDb, new[] { B("a", "H1", "b1"), B("b", "H2", "b2") });
        // the connection strings of the (other) contexts could not hold such a path, so the file is created elsewhere and moved
        Write(_secondDb, new[] { S("x", "H2", "s1") });
        File.Move(_secondDb, secondDb);

        var result = Attached(_baseDb, secondDb);

        Assert.Equal(new[] { "/b/b" }, result!.Select(r => r.FilenameAndPath));
    }

    [Fact]
    public void Attached_RelativePathOfTheSecondDatabase_Works()
    {
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        Write(_secondDb, new[] { S("x", "H1", "s1") });
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, _secondDb);
        if (Path.IsPathRooted(relative))
        {
            return; // another drive: no relative path exists
        }

        Assert.Single(Attached(_baseDb, relative)!);
    }

    [Fact]
    public void Attached_InstanceMethod_ValidatesArguments()
    {
        using var db = new DubFinderDB(null);
        Assert.Throws<InvalidOperationException>(() => db.FindDupsInDifferentDBs(_secondDb));
        Write(_baseDb, new[] { B("a", "H1", "b1") });
        db.SetupDB(_baseDb);
        Assert.Throws<ArgumentException>(() => db.FindDupsInDifferentDBs(" "));
    }

    [Fact]
    public void FindDupsInSameDB_WithoutSetup_NamesTheRightMethod()
    {
        using var db = new DubFinderDB(null);
        var ex = Assert.Throws<InvalidOperationException>(() => db.FindDupsInSameDB());
        Assert.Contains("FindDupsInSameDB", ex.Message, StringComparison.Ordinal);
    }

    // ---------- no change tracking ----------

    [Fact]
    public void ReadOnlyQueries_DoNotTrackTheRows()
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(200, 41);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows);
        using var context = DubFinderContextFactory.CreateDubFinderContext(_baseDb);

        Assert.NotEmpty(DubFinderDB.FindDupsInSameDB(context));
        Assert.NotEmpty(DubFinderDB.GetAllScanItems(context));
        Assert.NotEmpty(DubFinderDB.FindDupsInDifferentDBs(context, _secondDb)!);
        Assert.Empty(context.ChangeTracker.Entries());

        using var db = new DubFinderDB(null);
        db.SetupDB(_baseDb);
        Assert.Equal(200, db.GetAllScanItemsFromOtherDB(_secondDb)!.Count);
    }

    // ---------- query plans ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryPlan_AttachedExistsForm_WalksTheBaseIndexAndSearchesTheSecondIndex(bool olderSecondDatabase)
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(500, 31);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows, olderSecondDatabase ? FindDupsTestData.InitialMigration : null);

        string plan = QueryPlanHelper.ExplainWithAttached(_baseDb, _secondDb, c => DubFinderDB.FindDupsInDifferentDBs(c, _secondDb, DubFinderDB.HashMatchForm.Exists), "EXISTS (SELECT 1 FROM dupfinder_second", out var command);

        _output.WriteLine(command.Sql.Trim());
        _output.WriteLine(plan);
        // meant for a base database that is not much bigger than the second one:
        // base side walked in hash order through the hash index, second side one search in the hash index, no table access
        Assert.Matches(@"SCAN s USING (COVERING )?INDEX IX_ScanItems_FileSha512Hash", plan);
        Assert.Matches(@"SEARCH o (EXISTS )?USING COVERING INDEX IX_ScanItems_FileSha512Hash \(FileSha512Hash=\?\)", plan);
        // only the ties of the hash are sorted (path, primary key), not the whole result
        Assert.Contains("USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN o", plan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryPlan_AttachedInForm_ScansTheSecondIndexAndSeeksTheBaseIndex(bool olderSecondDatabase)
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(500, 31);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows, olderSecondDatabase ? FindDupsTestData.InitialMigration : null);

        string plan = QueryPlanHelper.ExplainWithAttached(_baseDb, _secondDb, c => DubFinderDB.FindDupsInDifferentDBs(c, _secondDb, DubFinderDB.HashMatchForm.In), "IN (SELECT o.FileSha512Hash FROM dupfinder_second", out var command);

        _output.WriteLine(command.Sql.Trim());
        _output.WriteLine(plan);
        // meant for a second database much smaller than the base one: the base index is not walked, it is searched
        // for every hash of the second database, which is read from its hash index without table access
        Assert.Matches(@"SEARCH s USING INDEX IX_ScanItems_FileSha512Hash \(FileSha512Hash=\?\)", plan);
        Assert.Matches(@"SCAN o USING COVERING INDEX IX_ScanItems_FileSha512Hash", plan);
        Assert.DoesNotContain("SCAN s", plan, StringComparison.Ordinal);
        Assert.Contains("USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryPlan_TotalOrder_DoesNotTurnTheQueriesIntoAFullSort()
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(500, 32);
        Write(_baseDb, baseRows);
        Write(_secondDb, secondRows);

        var plans = new List<(string Name, string Plan)>
        {
            ("GetAllScanItems (also GetAllScanItemsFromOtherDB)", QueryPlanHelper.Describe(_baseDb, c => DubFinderDB.GetAllScanItems(c))),
            ("FindDupsInSameDB", QueryPlanHelper.Describe(_baseDb, c => DubFinderDB.FindDupsInSameDB(c))),
        };
        foreach (var (name, plan) in plans)
        {
            _output.WriteLine("=== " + name);
            _output.WriteLine(plan);
            Assert.Contains("USING INDEX IX_ScanItems_FileSha512Hash", plan, StringComparison.Ordinal);
            Assert.Contains("USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("USE TEMP B-TREE FOR ORDER BY", plan, StringComparison.Ordinal);
        }
    }
}
