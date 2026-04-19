// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class FindDupsInDifferentDBsTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _baseDbPath;
    private readonly string _secondDbPath;

    public FindDupsInDifferentDBsTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FindDupsInDifferentDBsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _baseDbPath = Path.Combine(_tempDir, "base.db");
        _secondDbPath = Path.Combine(_tempDir, "second.db");
    }

    public void Dispose()
    {
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

    private static ScanItemDto CreateScanItem(string path, string hash, long fileSize)
    {
        return new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = path,
            PathBase = Path.GetDirectoryName(path) ?? string.Empty,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = fileSize,
            FileSha512Hash = hash,
            FileCreationUTC = DateTime.UtcNow,
            FileLastModificationUTC = DateTime.UtcNow,
            FirstScanDateUTC = DateTime.UtcNow,
            LastScanDateUTC = DateTime.UtcNow,
            LastSha512ScanDateUTC = DateTime.UtcNow
        };
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldFindDuplicates_WhenMatchingHashesExist()
    {
        // Arrange: Create base DB with some items
        PopulateDatabase(_baseDbPath,
            CreateScanItem("/base/file1.txt", "HASH_A", 100),
            CreateScanItem("/base/file2.txt", "HASH_B", 200),
            CreateScanItem("/base/file3.txt", "HASH_C", 300));

        // Arrange: Create second DB with items sharing some hashes
        PopulateDatabase(_secondDbPath,
            CreateScanItem("/second/fileX.txt", "HASH_A", 100),
            CreateScanItem("/second/fileY.txt", "HASH_C", 300),
            CreateScanItem("/second/fileZ.txt", "HASH_D", 400));

        // Act
        using var dubFinderDb = new DubFinderDB(null);
        dubFinderDb.SetupDB(_baseDbPath);
        var result = dubFinderDb.FindDupsInDifferentDBs(_secondDbPath);

        // Assert: Should find file1 (HASH_A) and file3 (HASH_C) from base DB
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.FilenameAndPath == "/base/file1.txt");
        Assert.Contains(result, r => r.FilenameAndPath == "/base/file3.txt");
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldReturnNull_WhenNoMatchingHashes()
    {
        // Arrange: Create base DB with some items
        PopulateDatabase(_baseDbPath,
            CreateScanItem("/base/file1.txt", "HASH_A", 100),
            CreateScanItem("/base/file2.txt", "HASH_B", 200));

        // Arrange: Create second DB with different hashes
        PopulateDatabase(_secondDbPath,
            CreateScanItem("/second/fileX.txt", "HASH_X", 100),
            CreateScanItem("/second/fileY.txt", "HASH_Y", 200));

        // Act
        using var dubFinderDb = new DubFinderDB(null);
        dubFinderDb.SetupDB(_baseDbPath);
        var result = dubFinderDb.FindDupsInDifferentDBs(_secondDbPath);

        // Assert
        Assert.True(result is null || result.Count == 0);
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldReturnNull_WhenSecondDBIsEmpty()
    {
        // Arrange: Create base DB with items
        PopulateDatabase(_baseDbPath,
            CreateScanItem("/base/file1.txt", "HASH_A", 100));

        // Arrange: Create empty second DB
        PopulateDatabase(_secondDbPath);

        // Act
        using var dubFinderDb = new DubFinderDB(null);
        dubFinderDb.SetupDB(_baseDbPath);
        var result = dubFinderDb.FindDupsInDifferentDBs(_secondDbPath);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldThrow_WhenCalledWithoutSetupDB()
    {
        using var dubFinderDb = new DubFinderDB(null);
        Assert.Throws<InvalidOperationException>(() => dubFinderDb.FindDupsInDifferentDBs(_secondDbPath));
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldThrow_WhenSecondDatabaseFileIsNull()
    {
        PopulateDatabase(_baseDbPath);

        using var dubFinderDb = new DubFinderDB(null);
        dubFinderDb.SetupDB(_baseDbPath);
        Assert.Throws<ArgumentException>(() => dubFinderDb.FindDupsInDifferentDBs(null!));
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldThrow_WhenSecondDatabaseFileIsEmpty()
    {
        PopulateDatabase(_baseDbPath);

        using var dubFinderDb = new DubFinderDB(null);
        dubFinderDb.SetupDB(_baseDbPath);
        Assert.Throws<ArgumentException>(() => dubFinderDb.FindDupsInDifferentDBs(string.Empty));
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldReturnMultipleItems_WhenMultipleFilesShareSameHash()
    {
        // Arrange: Create base DB with multiple files sharing same hash
        PopulateDatabase(_baseDbPath,
            CreateScanItem("/base/file1.txt", "HASH_A", 100),
            CreateScanItem("/base/file1_copy.txt", "HASH_A", 100),
            CreateScanItem("/base/file2.txt", "HASH_B", 200));

        // Arrange: Create second DB with matching hash
        PopulateDatabase(_secondDbPath,
            CreateScanItem("/second/fileX.txt", "HASH_A", 100));

        // Act
        using var dubFinderDb = new DubFinderDB(null);
        dubFinderDb.SetupDB(_baseDbPath);
        var result = dubFinderDb.FindDupsInDifferentDBs(_secondDbPath);

        // Assert: Should find both files with HASH_A from base DB
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.FilenameAndPath == "/base/file1.txt");
        Assert.Contains(result, r => r.FilenameAndPath == "/base/file1_copy.txt");
    }

    [Fact]
    public void FindDupsInDifferentDBs_ShouldOrderByHash()
    {
        // Arrange: Create base DB with items
        PopulateDatabase(_baseDbPath,
            CreateScanItem("/base/file_c.txt", "HASH_C", 300),
            CreateScanItem("/base/file_a.txt", "HASH_A", 100),
            CreateScanItem("/base/file_b.txt", "HASH_B", 200));

        // Arrange: Create second DB with all matching hashes
        PopulateDatabase(_secondDbPath,
            CreateScanItem("/second/fileX.txt", "HASH_C", 300),
            CreateScanItem("/second/fileY.txt", "HASH_A", 100),
            CreateScanItem("/second/fileZ.txt", "HASH_B", 200));

        // Act
        using var dubFinderDb = new DubFinderDB(null);
        dubFinderDb.SetupDB(_baseDbPath);
        var result = dubFinderDb.FindDupsInDifferentDBs(_secondDbPath);

        // Assert: Results should be ordered by hash
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("HASH_A", result[0].FileSha512Hash);
        Assert.Equal("HASH_B", result[1].FileSha512Hash);
        Assert.Equal("HASH_C", result[2].FileSha512Hash);
    }
}
