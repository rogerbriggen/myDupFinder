// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

internal sealed class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

public class FindDupsWholeLotTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _baseDbPath;
    private readonly string _secondDbPath;

    public FindDupsWholeLotTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FindDupsWholeLotTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _baseDbPath = Path.Combine(_tempDir, "base.db");
        _secondDbPath = Path.Combine(_tempDir, "second.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static void PopulateDatabase(string dbPath, params ScanItemDto[] items)
    {
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(dbPath);
        foreach (var item in items)
        {
            dbInserts.Enqueue(item);
        }
    }

    private static ScanItemDto Item(string fullPath, string hash, long size)
    {
        return new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = fullPath,
            PathBase = Path.GetDirectoryName(fullPath) ?? string.Empty,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = size,
            FileSha512Hash = hash,
            FileCreationUTC = DateTime.UtcNow,
            FileLastModificationUTC = DateTime.UtcNow,
            FirstScanDateUTC = DateTime.UtcNow,
            LastScanDateUTC = DateTime.UtcNow,
            LastSha512ScanDateUTC = DateTime.UtcNow,
        };
    }

    private static ScanItemDto ItemAtRoot(string basePath, string relPath, string hash, long size)
    {
        var item = Item(basePath + "/" + relPath, hash, size);
        item.PathBase = basePath;
        return item;
    }

    /// <summary>
    /// Parses one CSV data line into (path, size, hash, category, source, groupId).
    /// The path is quoted; everything after it splits cleanly on ';'.
    /// </summary>
    private sealed record ParsedRow(string Path, long Size, string Hash, string Category, string Source, int GroupId);

    private static ParsedRow Parse(string line)
    {
        var endQuote = line.IndexOf("\";", StringComparison.Ordinal);
        var path = line.Substring(1, endQuote - 1).Replace("\"\"", "\"");
        var rest = line.Substring(endQuote + 2).Split(';');
        return new ParsedRow(path, long.Parse(rest[0]), rest[1], rest[2], rest[3], int.Parse(rest[4]));
    }

    // ----- DB helpers -----

    [Fact]
    public void GetAllScanItems_ReturnsEveryRow_OrderedByHash()
    {
        PopulateDatabase(_baseDbPath,
            Item("/r/c.txt", "HASH_C", 30),
            Item("/r/a.txt", "HASH_A", 10),
            Item("/r/b.txt", "HASH_B", 20));

        using var db = new DubFinderDB(null);
        db.SetupDB(_baseDbPath);
        var result = db.GetAllScanItems();

        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal("HASH_A", result[0].FileSha512Hash);
        Assert.Equal("HASH_B", result[1].FileSha512Hash);
        Assert.Equal("HASH_C", result[2].FileSha512Hash);
    }

    [Fact]
    public void GetAllScanItemsFromOtherDB_ReturnsEveryRow()
    {
        PopulateDatabase(_secondDbPath,
            Item("/r/a.txt", "HASH_A", 10),
            Item("/r/b.txt", "HASH_B", 20));

        using var db = new DubFinderDB(null);
        db.SetupDB(_baseDbPath);
        var result = db.GetAllScanItemsFromOtherDB(_secondDbPath);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public void GetAllScanItemsFromOtherDB_Throws_OnEmptyPath()
    {
        using var db = new DubFinderDB(null);
        Assert.Throws<ArgumentException>(() => db.GetAllScanItemsFromOtherDB(string.Empty));
    }

    // ----- DupReportWriter -----

    [Fact]
    public void DupReportWriter_WritesHeader_AndOneRowPerItem_NoBlankSeparators()
    {
        var rows = new List<DupReportRow>
        {
            new DupReportRow(Item("/r/a.txt", "HASH_A", 10), DupReportCategory.Duplicate, DupReportSource.Base, 1),
            new DupReportRow(Item("/r/b.txt", "HASH_A", 10), DupReportCategory.Duplicate, DupReportSource.Base, 1),
            new DupReportRow(Item("/r/c.txt", "HASH_B", 20), DupReportCategory.Unique, DupReportSource.Base, 2),
        };
        var reportPath = Path.Combine(_tempDir, "test.csv");

        DupReportWriter.Write(reportPath, rows);

        var lines = File.ReadAllLines(reportPath);
        Assert.Equal(4, lines.Length);
        Assert.Equal(DupReportWriter.HeaderLine, lines[0]);
        Assert.DoesNotContain(lines.Skip(1), l => string.IsNullOrEmpty(l));
        Assert.Equal("\"/r/a.txt\";10;HASH_A;Duplicate;Base;1", lines[1]);
        Assert.Equal("\"/r/b.txt\";10;HASH_A;Duplicate;Base;1", lines[2]);
        Assert.Equal("\"/r/c.txt\";20;HASH_B;Unique;Base;2", lines[3]);
    }

    [Fact]
    public void DupReportWriter_EscapesEmbeddedQuoteInPath()
    {
        var rows = new List<DupReportRow>
        {
            new DupReportRow(Item("/r/a\"b.txt", "H", 1), DupReportCategory.Duplicate, DupReportSource.Second, 5),
        };
        var reportPath = Path.Combine(_tempDir, "test.csv");

        DupReportWriter.Write(reportPath, rows);

        var lines = File.ReadAllLines(reportPath);
        Assert.Equal("\"/r/a\"\"b.txt\";1;H;Duplicate;Second;5", lines[1]);
    }

    [Fact]
    public void GetRelativePath_StripsPathBase()
    {
        var item = Item("C:\\backup\\sub\\file.txt", "H", 1);
        item.PathBase = "C:\\backup";
        Assert.Equal("sub\\file.txt", DupReportWriter.GetRelativePath(item));
    }

    [Fact]
    public void GetRelativePath_ReturnsFull_WhenPathBaseEmpty()
    {
        var item = Item("/abs/file.txt", "H", 1);
        item.PathBase = string.Empty;
        Assert.Equal("/abs/file.txt", DupReportWriter.GetRelativePath(item));
    }

    [Fact]
    public void GetRelativePath_DoesNotMatchSiblingPrefix()
    {
        // PathBase "C:\data" must NOT match a file under sibling "C:\database\...".
        var item = Item("C:\\database\\file.txt", "H", 1);
        item.PathBase = "C:\\data";
        Assert.Equal("C:\\database\\file.txt", DupReportWriter.GetRelativePath(item));
    }

    [Fact]
    public void GetRelativePath_DoesNotMatchSiblingPrefix_ForwardSlash()
    {
        var item = Item("/data2/file.txt", "H", 1);
        item.PathBase = "/data";
        Assert.Equal("/data2/file.txt", DupReportWriter.GetRelativePath(item));
    }

    [Fact]
    public void GetRelativePath_AcceptsBoundaryWhenBaseEndsWithSeparator()
    {
        var item = Item("C:\\data\\file.txt", "H", 1);
        item.PathBase = "C:\\data\\";
        Assert.Equal("file.txt", DupReportWriter.GetRelativePath(item));
    }

    [Fact]
    public void GetRelativePath_ReturnsEmpty_WhenFullPathEqualsBase()
    {
        var item = Item("/data", "H", 1);
        item.PathBase = "/data";
        Assert.Equal(string.Empty, DupReportWriter.GetRelativePath(item));
    }

    // ----- Cross-DB FindDupsTheWholeLot -----

    [Fact]
    public void FindDupsTheWholeLot_CrossDB_EmitsBothSidesAndShareGroupId()
    {
        // Base DB rooted at /base, second DB rooted at /second.
        PopulateDatabase(_baseDbPath,
            ItemAtRoot("/base", "same.txt", "HASH_SAME", 100),       // -> Duplicate pair
            ItemAtRoot("/base", "changed.txt", "HASH_OLD", 200),     // -> Changed pair
            ItemAtRoot("/base", "missing.txt", "HASH_M", 300),       // -> Missing singleton
            ItemAtRoot("/base", "moved.txt", "HASH_MOVED", 400));    // -> Moved pair

        PopulateDatabase(_secondDbPath,
            ItemAtRoot("/second", "same.txt", "HASH_SAME", 100),
            ItemAtRoot("/second", "changed.txt", "HASH_NEW", 200),
            ItemAtRoot("/second", "new.txt", "HASH_N", 500),                  // -> New singleton
            ItemAtRoot("/second", "elsewhere/moved.txt", "HASH_MOVED", 400)); // hash-paired with base moved.txt

        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "wholelot",
            DatabaseFileBase = _baseDbPath,
            DatabaseFile = _secondDbPath,
            ReportPath = _tempDir + Path.DirectorySeparatorChar,
            FindDupsMode = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot,
        };

        var runner = new FindDupsInDifferentDBs(dto, null, new NullServiceProvider());
        runner.Start(System.Threading.CancellationToken.None);
        runner.Dispose();

        var reportFile = Path.Combine(_tempDir, "wholelot dupReport.csv");
        Assert.True(File.Exists(reportFile));
        var lines = File.ReadAllLines(reportFile);
        Assert.Equal(DupReportWriter.HeaderLine, lines[0]);

        var rows = lines.Skip(1).Select(Parse).ToList();

        // Expected rows:
        // 2x Duplicate (same.txt base + second)
        // 2x Changed (changed.txt base + second)
        // 2x Moved (base /moved.txt + second /elsewhere/moved.txt)
        // 1x Missing (/base/missing.txt)
        // 1x New (/second/new.txt)
        Assert.Equal(8, rows.Count);

        var byPath = rows.ToDictionary(r => r.Path);

        Assert.Equal("Duplicate", byPath["/base/same.txt"].Category);
        Assert.Equal("Base", byPath["/base/same.txt"].Source);
        Assert.Equal("Duplicate", byPath["/second/same.txt"].Category);
        Assert.Equal("Second", byPath["/second/same.txt"].Source);
        Assert.Equal(byPath["/base/same.txt"].GroupId, byPath["/second/same.txt"].GroupId);

        Assert.Equal("Changed", byPath["/base/changed.txt"].Category);
        Assert.Equal("Changed", byPath["/second/changed.txt"].Category);
        Assert.Equal(byPath["/base/changed.txt"].GroupId, byPath["/second/changed.txt"].GroupId);

        Assert.Equal("Moved", byPath["/base/moved.txt"].Category);
        Assert.Equal("Moved", byPath["/second/elsewhere/moved.txt"].Category);
        Assert.Equal(byPath["/base/moved.txt"].GroupId, byPath["/second/elsewhere/moved.txt"].GroupId);

        Assert.Equal("Missing", byPath["/base/missing.txt"].Category);
        Assert.Equal("Base", byPath["/base/missing.txt"].Source);

        Assert.Equal("New", byPath["/second/new.txt"].Category);
        Assert.Equal("Second", byPath["/second/new.txt"].Source);

        // Every GroupId should either be shared by exactly 2 rows (a pair) or alone (a singleton).
        var groupSizes = rows.GroupBy(r => r.GroupId).Select(g => g.Count()).ToList();
        Assert.Contains(groupSizes, c => c == 2);
        Assert.Contains(groupSizes, c => c == 1);
        Assert.All(groupSizes, c => Assert.InRange(c, 1, 2));
    }

    // ----- Same-DB FindDupsTheWholeLot -----

    [Fact]
    public void FindDupsTheWholeLot_SameDB_DuplicatesShareGroupId_UniqueGetsOwn()
    {
        PopulateDatabase(_baseDbPath,
            Item("/r/a.txt", "HASH_DUP", 100),
            Item("/r/a_copy.txt", "HASH_DUP", 100),
            Item("/r/unique.txt", "HASH_U", 50));

        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "wholelot",
            DatabaseFileBase = _baseDbPath,
            DatabaseFile = string.Empty,
            ReportPath = _tempDir + Path.DirectorySeparatorChar,
            FindDupsMode = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot,
        };

        var runner = new FindDupsInSameDB(dto, null, new NullServiceProvider());
        runner.Start(System.Threading.CancellationToken.None);
        runner.Dispose();

        var lines = File.ReadAllLines(Path.Combine(_tempDir, "wholelot dupReport.csv"));
        Assert.Equal(DupReportWriter.HeaderLine, lines[0]);
        var rows = lines.Skip(1).Select(Parse).ToList();
        Assert.Equal(3, rows.Count);

        var dupRows = rows.Where(r => r.Category == "Duplicate").ToList();
        var uniqueRows = rows.Where(r => r.Category == "Unique").ToList();
        Assert.Equal(2, dupRows.Count);
        Assert.Single(uniqueRows);

        // Both duplicates share one GroupId; the unique gets its own.
        Assert.Equal(dupRows[0].GroupId, dupRows[1].GroupId);
        Assert.NotEqual(dupRows[0].GroupId, uniqueRows[0].GroupId);

        // Same-DB context only has one DB -> every row is sourced from Base.
        Assert.All(rows, r => Assert.Equal("Base", r.Source));
    }

    // ----- Same-DB FindOnlyDups (regression for new columns) -----

    [Fact]
    public void FindOnlyDups_SameDB_DuplicatesShareGroupId()
    {
        PopulateDatabase(_baseDbPath,
            Item("/r/a.txt", "HASH_DUP", 100),
            Item("/r/a_copy.txt", "HASH_DUP", 100),
            Item("/r/unique.txt", "HASH_U", 50));

        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "only",
            DatabaseFileBase = _baseDbPath,
            DatabaseFile = string.Empty,
            ReportPath = _tempDir + Path.DirectorySeparatorChar,
            FindDupsMode = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindOnlyDups,
        };

        var runner = new FindDupsInSameDB(dto, null, new NullServiceProvider());
        runner.Start(System.Threading.CancellationToken.None);
        runner.Dispose();

        var lines = File.ReadAllLines(Path.Combine(_tempDir, "only dupReport.csv"));
        Assert.Equal(DupReportWriter.HeaderLine, lines[0]);
        var rows = lines.Skip(1).Select(Parse).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Duplicate", r.Category));
        Assert.All(rows, r => Assert.Equal("Base", r.Source));
        Assert.All(rows, r => Assert.Equal("HASH_DUP", r.Hash));
        Assert.Equal(rows[0].GroupId, rows[1].GroupId);
    }

    // ----- Cross-DB FindOnlyDups (regression for new columns) -----

    [Fact]
    public void FindDupsTheWholeLot_SameDB_AppliesCanonicalSort()
    {
        // Mix Duplicate and Unique items in a known scrambled hash/path order;
        // the writer should reorder by Category enum, then GroupId, then path.
        PopulateDatabase(_baseDbPath,
            Item("/r/z_unique.txt", "HASH_U", 50),
            Item("/r/b_dup.txt", "HASH_DUP", 100),
            Item("/r/a_dup.txt", "HASH_DUP", 100));

        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "sorted",
            DatabaseFileBase = _baseDbPath,
            DatabaseFile = string.Empty,
            ReportPath = _tempDir + Path.DirectorySeparatorChar,
            FindDupsMode = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot,
        };

        var runner = new FindDupsInSameDB(dto, null, new NullServiceProvider());
        runner.Start(System.Threading.CancellationToken.None);
        runner.Dispose();

        var lines = File.ReadAllLines(Path.Combine(_tempDir, "sorted dupReport.csv"));
        var rows = lines.Skip(1).Select(Parse).ToList();
        Assert.Equal(3, rows.Count);

        // Duplicate (enum value 0) comes before Unique (enum value 2).
        Assert.Equal("Duplicate", rows[0].Category);
        Assert.Equal("Duplicate", rows[1].Category);
        Assert.Equal("Unique", rows[2].Category);

        // Within the same group, paths must be ordered alphabetically (case-insensitive).
        Assert.Equal("/r/a_dup.txt", rows[0].Path);
        Assert.Equal("/r/b_dup.txt", rows[1].Path);
    }

    [Fact]
    public void FindOnlyDups_CrossDB_DuplicatesShareGroupIdPerHash()
    {
        PopulateDatabase(_baseDbPath,
            ItemAtRoot("/base", "a.txt", "HASH_A", 10),
            ItemAtRoot("/base", "b.txt", "HASH_A", 10),   // same hash as a.txt
            ItemAtRoot("/base", "c.txt", "HASH_C", 30));

        PopulateDatabase(_secondDbPath,
            ItemAtRoot("/second", "x.txt", "HASH_A", 10),
            ItemAtRoot("/second", "y.txt", "HASH_C", 30));

        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "xonly",
            DatabaseFileBase = _baseDbPath,
            DatabaseFile = _secondDbPath,
            ReportPath = _tempDir + Path.DirectorySeparatorChar,
            FindDupsMode = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindOnlyDups,
        };

        var runner = new FindDupsInDifferentDBs(dto, null, new NullServiceProvider());
        runner.Start(System.Threading.CancellationToken.None);
        runner.Dispose();

        var lines = File.ReadAllLines(Path.Combine(_tempDir, "xonly dupReport.csv"));
        var rows = lines.Skip(1).Select(Parse).ToList();

        // FindOnlyDups returns only base-side rows whose hash also exists in second.
        // All three base files qualify (HASH_A x2 + HASH_C x1).
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("Base", r.Source));
        Assert.All(rows, r => Assert.Equal("Duplicate", r.Category));

        // Rows with the same hash share GroupId; different hashes get different GroupIds.
        var hashAGroup = rows.Where(r => r.Hash == "HASH_A").Select(r => r.GroupId).Distinct().ToList();
        var hashCGroup = rows.Where(r => r.Hash == "HASH_C").Select(r => r.GroupId).Distinct().ToList();
        Assert.Single(hashAGroup);
        Assert.Single(hashCGroup);
        Assert.NotEqual(hashAGroup[0], hashCGroup[0]);
    }
}
