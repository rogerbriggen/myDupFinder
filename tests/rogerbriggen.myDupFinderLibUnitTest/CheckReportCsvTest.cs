// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class CheckReportCsvTest : IDisposable
{
    private readonly string _tempDir;

    public CheckReportCsvTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CheckReportCsvTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static CheckReportHeader SampleHeader() => new()
    {
        DatabaseFile = @"m:\db\base.db",
        BasePath = @"m:\test\",
        JobName = "MyJob",
        OriginComputer = "PC1",
        ScanName = "ScanA",
        SkipHashCheck = false,
        IgnoreBasePath = true,
        GeneratedUTC = new DateTime(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Roundtrip_PreservesAllCategoriesAndFields()
    {
        var header = SampleHeader();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var mtimeDb = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var mtimeDisk = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var rows = new List<CheckReportRow>
        {
            new()
            {
                Category = CheckCategory.BitRotSuspect,
                ScanItemId = id1,
                FilenameAndPathDB = @"m:\test\file,with,comma.dat",
                FilenameAndPathDisk = @"m:\test\file,with,comma.dat",
                PathBaseDB = @"m:\test\",
                PathBaseDisk = @"m:\test\",
                FileSizeDB = 1024,
                FileSizeDisk = 1024,
                MTimeDB = mtimeDb,
                MTimeDisk = mtimeDb,
                HashDB = "AAA",
                HashDisk = "BBB",
            },
            new()
            {
                Category = CheckCategory.Modified,
                PathMoved = true,
                ScanItemId = id2,
                FilenameAndPathDB = @"m:\old\sub\note ""quoted"".txt",
                FilenameAndPathDisk = @"d:\new\sub\note ""quoted"".txt",
                PathBaseDB = @"m:\old\",
                PathBaseDisk = @"d:\new\",
                FileSizeDB = 10,
                FileSizeDisk = 20,
                MTimeDB = mtimeDb,
                MTimeDisk = mtimeDisk,
                HashDB = "OLD",
                HashDisk = "NEW",
            },
            new()
            {
                Category = CheckCategory.MissingOnDisk,
                ScanItemId = id3,
                FilenameAndPathDB = @"m:\test\gone.bin",
                PathBaseDB = @"m:\test\",
                FileSizeDB = 999,
                MTimeDB = mtimeDb,
                HashDB = "GONEHASH",
            },
            new()
            {
                Category = CheckCategory.NewOnDisk,
                FilenameAndPathDisk = @"m:\test\fresh.dat",
                PathBaseDisk = @"m:\test\",
                FileSizeDisk = 42,
                MTimeDisk = mtimeDisk,
                HashDisk = "FRESHHASH",
                CreationTimeDisk = mtimeDisk,
            },
        };

        var csvPath = Path.Combine(_tempDir, "roundtrip.csv");
        using (var writer = new StreamWriter(csvPath))
        {
            CheckReportCsv.WriteHeader(writer, header);
            foreach (var row in rows)
            {
                CheckReportCsv.WriteRow(writer, row);
            }
        }

        var (parsedHeader, parsedRowsEnum) = CheckReportCsv.Read(csvPath);
        var parsedRows = parsedRowsEnum.ToList();

        Assert.Equal(header.DatabaseFile, parsedHeader.DatabaseFile);
        Assert.Equal(header.BasePath, parsedHeader.BasePath);
        Assert.Equal(header.JobName, parsedHeader.JobName);
        Assert.Equal(header.OriginComputer, parsedHeader.OriginComputer);
        Assert.Equal(header.ScanName, parsedHeader.ScanName);
        Assert.Equal(header.SkipHashCheck, parsedHeader.SkipHashCheck);
        Assert.Equal(header.IgnoreBasePath, parsedHeader.IgnoreBasePath);
        Assert.Equal(header.GeneratedUTC, parsedHeader.GeneratedUTC);
        Assert.Equal(CheckReportHeader.CurrentSchemaVersion, parsedHeader.SchemaVersion);

        Assert.Equal(rows.Count, parsedRows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var a = rows[i];
            var b = parsedRows[i];
            Assert.Equal(a.Category, b.Category);
            Assert.Equal(a.PathMoved, b.PathMoved);
            Assert.Equal(a.ScanItemId, b.ScanItemId);
            Assert.Equal(a.FilenameAndPathDB, b.FilenameAndPathDB);
            Assert.Equal(a.FilenameAndPathDisk, b.FilenameAndPathDisk);
            Assert.Equal(a.PathBaseDB, b.PathBaseDB);
            Assert.Equal(a.PathBaseDisk, b.PathBaseDisk);
            Assert.Equal(a.FileSizeDB, b.FileSizeDB);
            Assert.Equal(a.FileSizeDisk, b.FileSizeDisk);
            Assert.Equal(a.MTimeDB, b.MTimeDB);
            Assert.Equal(a.MTimeDisk, b.MTimeDisk);
            Assert.Equal(a.HashDB, b.HashDB);
            Assert.Equal(a.HashDisk, b.HashDisk);
            Assert.Equal(a.CreationTimeDisk, b.CreationTimeDisk);
        }
    }

    [Fact]
    public void Read_Throws_OnFileWithoutSignature()
    {
        var path = Path.Combine(_tempDir, "no-sig.csv");
        File.WriteAllText(path, "# random comment\nCategory,Whatever\nA,B\n");
        Assert.Throws<InvalidDataException>(() => CheckReportCsv.Read(path));
    }

    // ---------- streaming reader ----------

    private string WriteReportFile(string name, IEnumerable<CheckReportRow> rows)
    {
        var path = Path.Combine(_tempDir, name);
        using var writer = new StreamWriter(path);
        CheckReportCsv.WriteHeader(writer, SampleHeader());
        foreach (var row in rows)
        {
            CheckReportCsv.WriteRow(writer, row);
        }
        return path;
    }

    private static CheckReportRow NewRow(string? path, string? hash = null) => new()
    {
        Category = CheckCategory.NewOnDisk,
        FilenameAndPathDisk = path,
        PathBaseDisk = @"m:\test\",
        FileSizeDisk = 42,
        MTimeDisk = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        HashDisk = hash,
    };

    public static TheoryData<string> TrickyPaths() => new()
    {
        "a\nb",
        "a\r\nb",
        "a\rb",
        "\r",
        "\n",
        "ends with CR\r",
        "a\n\n\nb",
        "a,b,c",
        "say \"hi\"",
        "\"",
        "\"\"",
        "\"\"\"",
        " leading space",
        "trailing space ",
        "  both  ",
        ",",
        ",\"\r\n\"",
        "quote at end\"",
        "\"quote at start",
    };

    [Theory]
    [MemberData(nameof(TrickyPaths))]
    public void Roundtrip_PreservesTheExactCharactersOfAField(string value)
    {
        var rows = new[]
        {
            NewRow("first.dat", "H1"),
            new CheckReportRow { Category = CheckCategory.Modified, ScanItemId = Guid.NewGuid(), FilenameAndPathDB = value, FilenameAndPathDisk = value, PathBaseDB = value, HashDB = value, HashDisk = null },
            NewRow("last.dat", "H3"),
        };
        var path = WriteReportFile("tricky.csv", rows);

        var read = CheckReportCsv.Read(path).Rows.ToList();

        Assert.Equal(3, read.Count);
        Assert.Equal("first.dat", read[0].FilenameAndPathDisk);
        Assert.Equal(value, read[1].FilenameAndPathDB);
        Assert.Equal(value, read[1].FilenameAndPathDisk);
        Assert.Equal(value, read[1].PathBaseDB);
        Assert.Equal(value, read[1].HashDB);
        Assert.Null(read[1].HashDisk);
        Assert.Null(read[1].PathBaseDisk);
        Assert.Equal("last.dat", read[2].FilenameAndPathDisk);
        Assert.Equal("H3", read[2].HashDisk);
    }

    [Fact]
    public void Roundtrip_ManyFieldsWithLineBreaksInOneRecord_AndEmptyOptionalFields()
    {
        var row = new CheckReportRow
        {
            Category = CheckCategory.BitRotSuspect,
            PathMoved = true,
            ScanItemId = Guid.NewGuid(),
            FilenameAndPathDB = "db\r\nname,with \"all\"\rof it\n",
            FilenameAndPathDisk = "disk\nname",
            PathBaseDB = null,
            PathBaseDisk = string.Empty,
            FileSizeDB = null,
            FileSizeDisk = 7,
            MTimeDB = null,
            MTimeDisk = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            HashDB = null,
            HashDisk = "X",
            CreationTimeDisk = null,
        };
        var path = WriteReportFile("multi.csv", new[] { NewRow("before.dat"), row, NewRow("after.dat") });

        var read = CheckReportCsv.Read(path).Rows.ToList();

        Assert.Equal(3, read.Count);
        var b = read[1];
        Assert.Equal(row.FilenameAndPathDB, b.FilenameAndPathDB);
        Assert.Equal(row.FilenameAndPathDisk, b.FilenameAndPathDisk);
        Assert.Null(b.PathBaseDB);
        Assert.Null(b.PathBaseDisk);
        Assert.Null(b.FileSizeDB);
        Assert.Equal(7, b.FileSizeDisk);
        Assert.Null(b.MTimeDB);
        Assert.Equal(row.MTimeDisk, b.MTimeDisk);
        Assert.Null(b.HashDB);
        Assert.Equal("X", b.HashDisk);
        Assert.Null(b.CreationTimeDisk);
        Assert.True(b.PathMoved);
        Assert.Equal(row.ScanItemId, b.ScanItemId);
        Assert.Equal("after.dat", read[2].FilenameAndPathDisk);
    }

    [Fact]
    public void Read_SkipsBlankLinesBetweenRecords_AndAcceptsAnyLineEnding()
    {
        var path = WriteReportFile("blank.csv", new[] { NewRow("a.dat", "A"), NewRow("b.dat", "B"), NewRow("c.dat", "C") });
        var lines = File.ReadAllLines(path);
        var header = lines.Take(lines.Length - 3).ToArray();
        var records = lines.Skip(lines.Length - 3).ToArray();
        // LF, then CRLF with blank lines in between, and no line ending after the last record
        var text = string.Join("\n", header) + "\n" + records[0] + "\n\n" + records[1] + "\r\n\r\n\r\n" + records[2];
        File.WriteAllText(path, text);

        var read = CheckReportCsv.Read(path).Rows.Select(r => r.FilenameAndPathDisk).ToList();

        Assert.Equal(new[] { "a.dat", "b.dat", "c.dat" }, read);

        // Lone CR as line ending
        File.WriteAllText(path, string.Join("\r", header) + "\r" + records[0] + "\r" + records[1] + "\r" + records[2] + "\r");
        Assert.Equal(new[] { "a.dat", "b.dat", "c.dat" }, CheckReportCsv.Read(path).Rows.Select(r => r.FilenameAndPathDisk).ToList());
    }

    [Fact]
    public void Read_UnterminatedQuotedFieldAtTheEnd_IsInvalidData()
    {
        var path = WriteReportFile("open.csv", new[] { NewRow("a.dat", "A") });
        File.AppendAllText(path, "NewOnDisk,false,,,\"never closed,\nsecond line" + Environment.NewLine);

        var (_, rows) = CheckReportCsv.Read(path);

        Assert.Throws<InvalidDataException>(() => rows.ToList());
    }

    [Fact]
    public void Read_RowWithTheWrongNumberOfFields_IsInvalidData()
    {
        var path = WriteReportFile("fields.csv", new[] { NewRow("a.dat", "A") });
        File.AppendAllText(path, "NewOnDisk,false,only,four" + Environment.NewLine);

        var (_, rows) = CheckReportCsv.Read(path);

        var ex = Assert.Throws<InvalidDataException>(() => rows.ToList());
        Assert.Contains("4 fields", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Throws_WhenTheColumnHeaderIsMissingOrWrong()
    {
        var onlyComments = Path.Combine(_tempDir, "nocols.csv");
        File.WriteAllText(onlyComments, "# myDupFinder check report v2\n# SchemaVersion=2\n");
        Assert.Throws<InvalidDataException>(() => CheckReportCsv.Read(onlyComments));

        var wrong = Path.Combine(_tempDir, "wrongcols.csv");
        File.WriteAllText(wrong, "# myDupFinder check report v2\n# SchemaVersion=2\nCategory,Other\n");
        Assert.Throws<InvalidDataException>(() => CheckReportCsv.Read(wrong));

        var empty = Path.Combine(_tempDir, "empty.csv");
        File.WriteAllText(empty, string.Empty);
        Assert.Throws<InvalidDataException>(() => CheckReportCsv.Read(empty));
    }

    [Fact]
    public void Read_ReturnsTheHeaderAtOnce_AndTheRowsAreOnlyReadWhenTheSequenceIsEnumerated()
    {
        var path = WriteReportFile("lazy.csv", new[] { NewRow("a.dat", "A"), NewRow("b.dat", "B") });
        File.AppendAllText(path, "this line is not a valid record" + Environment.NewLine);

        // No exception here: the malformed last row is not read yet
        var (header, rows) = CheckReportCsv.Read(path);
        Assert.Equal("MyJob", header.JobName);

        // The first rows can be taken without reading (or failing on) the rest of the file
        Assert.Equal("a.dat", rows.First().FilenameAndPathDisk);
        Assert.Equal(new[] { "a.dat", "b.dat" }, rows.Take(2).Select(r => r.FilenameAndPathDisk).ToArray());

        // Reading to the end reads the whole file and meets the malformed row
        Assert.Throws<InvalidDataException>(() => rows.Count());
    }

    [Fact]
    public void Read_EveryEnumerationReadsTheFileAgain_AndTheFileIsNotLockedAfterwards()
    {
        var path = WriteReportFile("again.csv", new[] { NewRow("a.dat", "A") });
        var (_, rows) = CheckReportCsv.Read(path);
        Assert.Single(rows);

        // The report changes between two enumerations: the second pass sees the new content
        File.AppendAllText(path, "NewOnDisk,false,,,b.dat,,,,1,,2026-01-02T03:04:05.0000000Z,,H,\n");
        Assert.Equal(2, rows.Count());
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Opening with FileShare.None only works when no enumeration left the file open
        }
    }

    [Fact]
    public void Read_ALargeReport_IsEnumeratedRowByRow()
    {
        const int total = 200_000;
        var path = Path.Combine(_tempDir, "large.csv");
        using (var writer = new StreamWriter(path))
        {
            CheckReportCsv.WriteHeader(writer, SampleHeader());
            for (int i = 0; i < total; i++)
            {
                // Every 1000th path has a line break inside the quoted field
                var name = i % 1000 == 0 ? $"m:\\test\\multi\r\nline{i}.dat" : $"m:\\test\\f{i}.dat";
                CheckReportCsv.WriteRow(writer, NewRow(name, "H" + i));
            }
        }

        var (_, rows) = CheckReportCsv.Read(path);

        long count = 0;
        long multiLine = 0;
        foreach (var row in rows)
        {
            count++;
            if (row.FilenameAndPathDisk!.Contains('\n', StringComparison.Ordinal))
            {
                multiLine++;
            }
            Assert.Equal(CheckCategory.NewOnDisk, row.Category);
        }
        Assert.Equal(total, count);
        Assert.Equal(total / 1000, multiLine);

        // Laziness: a garbage line at the end of the file does not matter for the first row, only for a pass to the end
        File.AppendAllText(path, "garbage" + Environment.NewLine);
        Assert.Equal("m:\\test\\multi\r\nline0.dat", rows.First().FilenameAndPathDisk);
        Assert.Throws<InvalidDataException>(() => rows.Count());
    }
}
