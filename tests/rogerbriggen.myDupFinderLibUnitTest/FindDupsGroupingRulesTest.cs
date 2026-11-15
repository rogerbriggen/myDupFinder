// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Pins the grouping and pairing rules of the find-dups report with small hand-made databases, including all kinds of ties
/// (see the documentation of FindDupsInSameDB and FindDupsInDifferentDBs and the README), and checks that the report does not depend
/// on the order in which the rows were inserted. The expected lines are the lines of the CSV report, in file order.
/// </summary>
public class FindDupsGroupingRulesTest : IDisposable
{
    private const MyDupFinderFindDupsJobDTO.EFindDupsMode OnlyDups = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindOnlyDups;
    private const MyDupFinderFindDupsJobDTO.EFindDupsMode WholeLot = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot;

    private readonly string _tempDir;
    private int _runs;

    public FindDupsGroupingRulesTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FindDupsGroupingRulesTest_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A row. The number is the primary key (so the order of the keys is known) and also the file size, so a row can be recognised in the report.</summary>
    private static ScanItemDto R(string pathBase, string relPath, string hash, int number)
    {
        var item = FindDupsTestData.Row(pathBase, relPath, hash, number, "rule-" + number);
        item.Id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
        return item;
    }

    private string NewDb(IEnumerable<ScanItemDto> rows)
    {
        string file = Path.Combine(_tempDir, "db" + (++_runs) + ".db");
        // plain SQL in the given order, so the physical order of the rows (rowid) is the order of the list
        FindDupsTestData.WriteDatabaseInOrder(file, rows);
        SqliteConnection.ClearAllPools();
        return file;
    }

    private byte[] RunReportBytes(string baseDb, string? secondDb, MyDupFinderFindDupsJobDTO.EFindDupsMode mode)
    {
        string reportDir = Path.Combine(_tempDir, "report" + (++_runs));
        Directory.CreateDirectory(reportDir);
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "rules",
            DatabaseFileBase = baseDb,
            DatabaseFile = secondDb ?? string.Empty,
            ReportPath = reportDir + Path.DirectorySeparatorChar,
            FindDupsMode = mode,
        };
        if (secondDb is null)
        {
            using var runner = new FindDupsInSameDB(dto, null, new NullServiceProvider());
            runner.Start(CancellationToken.None);
        }
        else
        {
            using var runner = new FindDupsInDifferentDBs(dto, null, new NullServiceProvider());
            runner.Start(CancellationToken.None);
        }
        SqliteConnection.ClearAllPools();
        return File.ReadAllBytes(Path.Combine(reportDir, "rules dupReport.csv"));
    }

    /// <summary>Runs the job and returns the lines of the report without the header.</summary>
    private List<string> RunReport(IEnumerable<ScanItemDto> baseRows, IEnumerable<ScanItemDto>? secondRows, MyDupFinderFindDupsJobDTO.EFindDupsMode mode)
    {
        string baseDb = NewDb(baseRows);
        string? secondDb = secondRows is null ? null : NewDb(secondRows);
        var lines = System.Text.Encoding.UTF8.GetString(RunReportBytes(baseDb, secondDb, mode)).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(DupReportWriter.HeaderLine, lines[0]);
        return lines.Skip(1).ToList();
    }

    /// <summary>Asserts the report for the rows in the given order and for the rows in the reverse order of insertion (same keys).</summary>
    private void AssertReport(string[] expected, List<ScanItemDto> baseRows, List<ScanItemDto>? secondRows, MyDupFinderFindDupsJobDTO.EFindDupsMode mode)
    {
        Assert.Equal(expected, RunReport(baseRows, secondRows, mode));
        Assert.Equal(expected, RunReport(Enumerable.Reverse(baseRows).ToList(), secondRows is null ? null : Enumerable.Reverse(secondRows).ToList(), mode));
    }

    // ---------- same database ----------

    private static List<ScanItemDto> SameDbRows() => new List<ScanItemDto>
    {
        R("/r", "z.txt", "H2", 1), R("/r", "a.txt", "H2", 2), R("/r", "m.txt", "H2", 3),
        R("/r", "q.txt", "H1", 4), R("/r", "p.txt", "H1", 5),
        R("/r", "u.txt", "H3", 6),
        R("/r", "e2.txt", string.Empty, 7), R("/r", "e1.txt", string.Empty, 8),
        R("/r", "v.txt", "H15", 9),
    };

    [Fact]
    public void SameDb_OnlyDups_OneGroupIdPerHash_InHashOrder_RowsWithoutHashAreDuplicatesOfEachOther()
    {
        // The rows come in the order hash, path, key: '' (e1, e2), H1 (p, q), H15 (v: single, not reported), H2 (a, m, z), H3 (u: single, not reported).
        // The first row of a hash starts its group: '' = 1, H1 = 2, H2 = 3.
        AssertReport(
            new[]
            {
                "\"/r/e1.txt\";8;;Duplicate;Base;1",
                "\"/r/e2.txt\";7;;Duplicate;Base;1",
                "\"/r/p.txt\";5;H1;Duplicate;Base;2",
                "\"/r/q.txt\";4;H1;Duplicate;Base;2",
                "\"/r/a.txt\";2;H2;Duplicate;Base;3",
                "\"/r/m.txt\";3;H2;Duplicate;Base;3",
                "\"/r/z.txt\";1;H2;Duplicate;Base;3",
            },
            SameDbRows(), null, OnlyDups);
    }

    [Fact]
    public void SameDb_WholeLot_OneGroupIdPerHash_UniqueFilesGetTheirGroupIdInHashOrderToo()
    {
        // Same order of the rows. Every distinct hash takes the next GroupId at its first row, whether it is a duplicate or a unique:
        // '' = 1, H1 = 2, H15 (unique v) = 3, H2 = 4, H3 (unique u) = 5. The report is sorted by category first (Duplicate before Unique).
        AssertReport(
            new[]
            {
                "\"/r/e1.txt\";8;;Duplicate;Base;1",
                "\"/r/e2.txt\";7;;Duplicate;Base;1",
                "\"/r/p.txt\";5;H1;Duplicate;Base;2",
                "\"/r/q.txt\";4;H1;Duplicate;Base;2",
                "\"/r/a.txt\";2;H2;Duplicate;Base;4",
                "\"/r/m.txt\";3;H2;Duplicate;Base;4",
                "\"/r/z.txt\";1;H2;Duplicate;Base;4",
                "\"/r/v.txt\";9;H15;Unique;Base;3",
                "\"/r/u.txt\";6;H3;Unique;Base;5",
            },
            SameDbRows(), null, WholeLot);
    }

    [Fact]
    public void SameDb_SameHashAndSamePath_AreTwoRows_InTheOrderOfTheirKeys()
    {
        var rows = new List<ScanItemDto> { R("/r", "x.txt", "H1", 12), R("/r", "x.txt", "H1", 11), R("/r", "x.txt", "H1", 13) };

        AssertReport(
            new[]
            {
                "\"/r/x.txt\";11;H1;Duplicate;Base;1",
                "\"/r/x.txt\";12;H1;Duplicate;Base;1",
                "\"/r/x.txt\";13;H1;Duplicate;Base;1",
            },
            rows, null, OnlyDups);
    }

    // ---------- two databases, only dups ----------

    [Fact]
    public void DifferentDbs_OnlyDups_ReportsTheBaseRowsWhoseHashIsInTheSecondDatabase_OneGroupIdPerHash()
    {
        var baseRows = new List<ScanItemDto>
        {
            R("/b", "a2", "H_A", 2), R("/b", "a1", "H_A", 1), R("/b", "b", "H_B", 3), R("/b", "c", "H_C", 4), R("/b", "e", string.Empty, 5),
        };
        var secondRows = new List<ScanItemDto>
        {
            R("/s", "z", "H_Z", 10), R("/s", "c", "H_C", 11), R("/s", "a", "H_A", 12), R("/s", "a-copy", "H_A", 13), R("/s", "e", string.Empty, 14),
        };

        // Base rows in the order hash, path, key: H_A (a1, a2), H_C (c). The GroupIds are given out in that order: H_A = 1, H_C = 2.
        // Only the base rows are reported; the number of copies in the second database does not matter, and an empty hash never matches.
        AssertReport(
            new[]
            {
                "\"/b/a1\";1;H_A;Duplicate;Base;1",
                "\"/b/a2\";2;H_A;Duplicate;Base;1",
                "\"/b/c\";4;H_C;Duplicate;Base;2",
            },
            baseRows, secondRows, OnlyDups);
    }

    // ---------- two databases, whole lot ----------

    [Fact]
    public void DifferentDbs_WholeLot_OfSeveralBaseRowsWithTheSameRelativePath_TheFirstInHashPathKeyOrderClaimsTheSecondRow()
    {
        // Two base rows with the relative path x.txt (two scanned base paths). The first in the order hash, path, key takes the pair:
        // that is the one with the smaller hash (/b2, H1), not the one with the smaller path (/b1, H2).
        var baseRows = new List<ScanItemDto> { R("/b1", "x.txt", "H2", 1), R("/b2", "x.txt", "H1", 2) };
        var secondRows = new List<ScanItemDto> { R("/s", "x.txt", "H1", 3) };

        // The other base row is a leftover: no hash partner in the second database, so it is Missing (GroupId 2).
        AssertReport(
            new[]
            {
                "\"/b2/x.txt\";2;H1;Duplicate;Base;1",
                "\"/s/x.txt\";3;H1;Duplicate;Second;1",
                "\"/b1/x.txt\";1;H2;Missing;Base;2",
            },
            baseRows, secondRows, WholeLot);
    }

    [Fact]
    public void DifferentDbs_WholeLot_OfSeveralSecondRowsWithTheSameRelativePath_TheLastInHashPathKeyOrderIsTheCandidate()
    {
        // Two second rows with the relative path x.txt: the dictionary by relative path keeps the last one in the order hash, path, key
        // (the larger hash, /s2, H2), so the base row is paired with it and the pair is Changed. The other second row is left over (New).
        var baseRows = new List<ScanItemDto> { R("/b", "x.txt", "H1", 1) };
        var secondRows = new List<ScanItemDto> { R("/s1", "x.txt", "H1", 2), R("/s2", "x.txt", "H2", 3) };

        AssertReport(
            new[]
            {
                "\"/s1/x.txt\";2;H1;New;Second;2",
                "\"/b/x.txt\";1;H1;Changed;Base;1",
                "\"/s2/x.txt\";3;H2;Changed;Second;1",
            },
            baseRows, secondRows, WholeLot);
    }

    [Fact]
    public void DifferentDbs_WholeLot_RowsWithTheSameHashAndTheSamePath_TheKeyDecides()
    {
        // Base: two rows x.txt/H1, keys 4 and 5, so key 4 comes first and claims the pair. Second: two rows x.txt/H1, keys 6 and 7,
        // the last one (7) is the candidate. The rest (base 5, second 6) are left over and have the same hash: Moved.
        var baseRows = new List<ScanItemDto> { R("/b", "x.txt", "H1", 5), R("/b", "x.txt", "H1", 4) };
        var secondRows = new List<ScanItemDto> { R("/s", "x.txt", "H1", 7), R("/s", "x.txt", "H1", 6) };

        AssertReport(
            new[]
            {
                "\"/b/x.txt\";4;H1;Duplicate;Base;1",
                "\"/s/x.txt\";7;H1;Duplicate;Second;1",
                "\"/b/x.txt\";5;H1;Moved;Base;2",
                "\"/s/x.txt\";6;H1;Moved;Second;2",
            },
            baseRows, secondRows, WholeLot);
    }

    [Fact]
    public void DifferentDbs_WholeLot_TheSameHashOnSeveralPathsOnBothSides_IsOneMovedGroup()
    {
        var baseRows = new List<ScanItemDto> { R("/b", "a.txt", "H1", 1), R("/b", "sub/b.txt", "H1", 2), R("/b", "c.txt", "H1", 3) };
        var secondRows = new List<ScanItemDto> { R("/s", "x/a1.txt", "H1", 4), R("/s", "y.txt", "H1", 5) };

        // No relative path in common: all five rows share one GroupId, ordered by source, then path
        AssertReport(
            new[]
            {
                "\"/b/a.txt\";1;H1;Moved;Base;1",
                "\"/b/c.txt\";3;H1;Moved;Base;1",
                "\"/b/sub/b.txt\";2;H1;Moved;Base;1",
                "\"/s/x/a1.txt\";4;H1;Moved;Second;1",
                "\"/s/y.txt\";5;H1;Moved;Second;1",
            },
            baseRows, secondRows, WholeLot);
    }

    [Fact]
    public void DifferentDbs_WholeLot_GroupIdsAreGivenOutInTheOrderOfThePhases()
    {
        // Base rows in the order hash, path, key: miss (H1), mv (H2), chg (H3), same (H5).
        // Phase 1 (by relative path, in base order): chg = Changed with GroupId 1, same = Duplicate with GroupId 2.
        // Phase 2 (by hash, the leftovers, in base order): mv = Moved with GroupId 3.
        // Phase 3: base leftovers without partner: miss = Missing, GroupId 4. Phase 4: second leftovers: new = New, GroupId 5.
        var baseRows = new List<ScanItemDto>
        {
            R("/b", "same.txt", "H5", 1), R("/b", "chg.txt", "H3", 2), R("/b", "mv.txt", "H2", 3), R("/b", "miss.txt", "H1", 4),
        };
        var secondRows = new List<ScanItemDto>
        {
            R("/s", "same.txt", "H5", 5), R("/s", "chg.txt", "H4", 6), R("/s", "other/mv.txt", "H2", 7), R("/s", "new.txt", "H9", 8),
        };

        AssertReport(
            new[]
            {
                "\"/b/same.txt\";1;H5;Duplicate;Base;2",
                "\"/s/same.txt\";5;H5;Duplicate;Second;2",
                "\"/b/mv.txt\";3;H2;Moved;Base;3",
                "\"/s/other/mv.txt\";7;H2;Moved;Second;3",
                "\"/b/miss.txt\";4;H1;Missing;Base;4",
                "\"/s/new.txt\";8;H9;New;Second;5",
                "\"/b/chg.txt\";2;H3;Changed;Base;1",
                "\"/s/chg.txt\";6;H4;Changed;Second;1",
            },
            baseRows, secondRows, WholeLot);
    }

    [Fact]
    public void DifferentDbs_WholeLot_RelativePathsAreComparedIgnoringCase()
    {
        // Base: A.txt (H1) and a.txt (H2) have the same relative path for the comparison. The second row s/a.TXT is claimed by the first
        // base row in the order hash, path, key (A.txt, H1), the other one is Missing.
        var baseRows = new List<ScanItemDto> { R("/b", "a.txt", "H2", 2), R("/b", "A.txt", "H1", 1), R("/b", "Dir/X.TXT", "H3", 3) };
        var secondRows = new List<ScanItemDto> { R("/s", "a.TXT", "H1", 4), R("/s", "dir/x.txt", "H3", 5) };

        AssertReport(
            new[]
            {
                "\"/b/A.txt\";1;H1;Duplicate;Base;1",
                "\"/s/a.TXT\";4;H1;Duplicate;Second;1",
                "\"/b/Dir/X.TXT\";3;H3;Duplicate;Base;2",
                "\"/s/dir/x.txt\";5;H3;Duplicate;Second;2",
                "\"/b/a.txt\";2;H2;Missing;Base;3",
            },
            baseRows, secondRows, WholeLot);
    }

    [Fact]
    public void DifferentDbs_WholeLot_RowsWithoutHash_PairByPathAndAreDuplicates_ButNeverMoved()
    {
        var baseRows = new List<ScanItemDto> { R("/b", "e.txt", string.Empty, 1), R("/b", "e1.txt", string.Empty, 2) };
        var secondRows = new List<ScanItemDto> { R("/s", "e.txt", string.Empty, 3), R("/s", "f1.txt", string.Empty, 4) };

        // e.txt exists on both sides with the same (empty) hash: Duplicate. The other two have no partner, because rows without a hash are not paired by hash.
        AssertReport(
            new[]
            {
                "\"/b/e.txt\";1;;Duplicate;Base;1",
                "\"/s/e.txt\";3;;Duplicate;Second;1",
                "\"/b/e1.txt\";2;;Missing;Base;2",
                "\"/s/f1.txt\";4;;New;Second;3",
            },
            baseRows, secondRows, WholeLot);
    }

    // ---------- the test data helper ----------

    [Fact]
    public void WriteDatabaseInOrder_KeepsThePhysicalOrderOfTheRows_AndTheValuesAsEfCoreWritesThem()
    {
        var rows = new List<ScanItemDto> { R("/r", "c.txt", "H1", 3), R("/r", "a.txt", "H2", 1), R("/r", "b.txt", "H3", 2) };
        string raw = NewDb(rows);
        string ef = Path.Combine(_tempDir, "ef.db");
        FindDupsTestData.WriteDatabase(ef, rows);
        SqliteConnection.ClearAllPools();

        using (var connection = new SqliteConnection($"Data Source={raw};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT FileSize FROM ScanItems ORDER BY rowid";
            var sizes = new List<long>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sizes.Add(reader.GetInt64(0));
            }
            Assert.Equal(new long[] { 3, 1, 2 }, sizes);
        }

        // read through EF Core, the rows are the same as the ones EF Core wrote
        using var rawContext = DubFinderContextFactory.CreateDubFinderContext(raw);
        using var efContext = DubFinderContextFactory.CreateDubFinderContext(ef);
        static string Describe(ScanItemDto i) => string.Join("|", i.Id, i.FilenameAndPath, i.Filename, i.PathBase, i.FileSize, i.FileCreationUTC.Ticks, i.FileLastModificationUTC.Ticks, i.FileSha512Hash, i.FirstScanDateUTC.Ticks, i.LastScanDateUTC.Ticks, i.LastSha512ScanDateUTC.Ticks);
        Assert.Equal(DubFinderDB.GetAllScanItems(efContext).Select(Describe), DubFinderDB.GetAllScanItems(rawContext).Select(Describe));
    }

    // ---------- the order of the queries ----------

    [Fact]
    public void Queries_ReturnTiesInTheOrderOfThePrimaryKey_WhateverTheInsertionOrder()
    {
        var rows = new List<ScanItemDto>
        {
            R("/r", "p.txt", "H1", 3), R("/r", "p.txt", "H1", 1), R("/r", "o.txt", "H1", 9), R("/r", "p.txt", "H1", 2), R("/r", "p.txt", "H0", 7),
        };
        string[] expected = { "7", "9", "1", "2", "3" };

        foreach (var insertion in new[] { rows, Enumerable.Reverse(rows).ToList() })
        {
            string db = NewDb(insertion);
            using var finder = new DubFinderDB(null);
            finder.SetupDB(db);
            Assert.Equal(expected, finder.GetAllScanItems()!.Select(i => i.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Equal(expected, finder.GetAllScanItemsFromOtherDB(db)!.Select(i => i.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            // only the rows of the hash with more than one row
            Assert.Equal(expected.Skip(1), finder.FindDupsInSameDB()!.Select(i => i.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            // the database compared with itself: every row with a hash
            Assert.Equal(expected, finder.FindDupsInDifferentDBs(db)!.Select(i => i.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    // ---------- the report is deterministic ----------

    /// <summary>A generated pair with ties: rows with the same hash and the same path (different keys) on both sides.</summary>
    private static (List<ScanItemDto> BaseRows, List<ScanItemDto> SecondRows) PairWithTies()
    {
        var (baseRows, secondRows) = FindDupsTestData.Generate(600, 99);
        AddTies(baseRows, "tie-base-");
        AddTies(secondRows, "tie-second-");
        return (baseRows, secondRows);

        static void AddTies(List<ScanItemDto> rows, string keyPrefix)
        {
            var clones = new List<ScanItemDto>();
            for (int i = 0; i < rows.Count; i += 20)
            {
                var original = rows[i];
                var clone = FindDupsTestData.Row(original.PathBase, original.FilenameAndPath.Substring(original.PathBase.Length + 1), original.FileSha512Hash, original.FileSize + 1, keyPrefix + i);
                clones.Add(clone);
            }
            rows.AddRange(clones);
        }
    }

    private static List<T> Shuffled<T>(IEnumerable<T> items, int seed)
    {
        var list = items.ToList();
        var random = new Random(seed);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    [Fact]
    public void Report_OfAllFourModes_IsByteIdenticalBetweenTwoRuns_AndWhenTheRowsWereInsertedInAnotherOrder()
    {
        var (baseRows, secondRows) = PairWithTies();
        string baseA = NewDb(baseRows);
        string secondA = NewDb(secondRows);
        string baseB = NewDb(Shuffled(baseRows, 1));
        string secondB = NewDb(Shuffled(secondRows, 2));
        string baseC = NewDb(Enumerable.Reverse(baseRows));
        string secondC = NewDb(Enumerable.Reverse(secondRows));

        var cases = new (string Name, bool Cross, MyDupFinderFindDupsJobDTO.EFindDupsMode Mode)[]
        {
            ("same database, only dups", false, OnlyDups),
            ("same database, whole lot", false, WholeLot),
            ("different databases, only dups", true, OnlyDups),
            ("different databases, whole lot", true, WholeLot),
        };
        foreach (var (name, cross, mode) in cases)
        {
            byte[] first = RunReportBytes(baseA, cross ? secondA : null, mode);
            Assert.True(first.Length > 1000, name + ": the report should not be trivial");
            Assert.True(first.SequenceEqual(RunReportBytes(baseA, cross ? secondA : null, mode)), name + ": second run on the same databases");
            Assert.True(first.SequenceEqual(RunReportBytes(baseB, cross ? secondB : null, mode)), name + ": rows inserted in a shuffled order");
            Assert.True(first.SequenceEqual(RunReportBytes(baseC, cross ? secondC : null, mode)), name + ": rows inserted in reverse order");
        }
    }

    [Fact]
    public void Report_WholeLotAcrossDatabases_ContainsEveryCategory_SoTheDeterminismTestMeansSomething()
    {
        var (baseRows, secondRows) = PairWithTies();
        string text = System.Text.Encoding.UTF8.GetString(RunReportBytes(NewDb(baseRows), NewDb(secondRows), WholeLot));

        foreach (var category in new[] { "Duplicate", "Moved", "Missing", "New", "Changed" })
        {
            Assert.Contains(";" + category + ";", text, StringComparison.Ordinal);
        }
    }
}
