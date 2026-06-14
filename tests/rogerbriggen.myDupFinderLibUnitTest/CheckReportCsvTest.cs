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
}
