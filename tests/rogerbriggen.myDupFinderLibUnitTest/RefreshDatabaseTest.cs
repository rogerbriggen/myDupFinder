// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class RefreshDatabaseTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public RefreshDatabaseTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefreshDatabaseTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        // Clear SQLite connection pool to release file locks before cleanup
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static ScanItemDto CreateScanItem(string path, string basePath, string hash, long fileSize, DateTime lastModificationUTC)
    {
        return new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = path,
            PathBase = basePath,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = fileSize,
            FileSha512Hash = hash,
            FileCreationUTC = DateTime.UtcNow,
            FileLastModificationUTC = lastModificationUTC,
            FirstScanDateUTC = DateTime.UtcNow,
            LastScanDateUTC = DateTime.UtcNow,
            LastSha512ScanDateUTC = DateTime.UtcNow
        };
    }

    [Fact]
    public void GetAllItemsByBasePath_ShouldReturnItemsForMatchingPath()
    {
        // Arrange
        string basePath = "/test/base/";
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);
        dbInserts.Enqueue(CreateScanItem("/test/base/file1.txt", basePath, "HASH_A", 100, DateTime.UtcNow));
        dbInserts.Enqueue(CreateScanItem("/test/base/file2.txt", basePath, "HASH_B", 200, DateTime.UtcNow));
        dbInserts.Enqueue(CreateScanItem("/other/path/file3.txt", "/other/path/", "HASH_C", 300, DateTime.UtcNow));

        // Act
        var result = dbInserts.GetAllItemsByBasePath(basePath);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal(basePath, item.PathBase));
    }

    [Fact]
    public void GetAllItemsByBasePath_ShouldReturnEmptyList_WhenNoMatches()
    {
        // Arrange
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);
        dbInserts.Enqueue(CreateScanItem("/other/file.txt", "/other/", "HASH_A", 100, DateTime.UtcNow));

        // Act
        var result = dbInserts.GetAllItemsByBasePath("/nonexistent/");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllItemsByBasePath_ShouldThrow_WhenCalledWithoutSetupDB()
    {
        using var dbInserts = new ScanJobDBInserts(null);
        Assert.Throws<InvalidOperationException>(() => dbInserts.GetAllItemsByBasePath("/test/"));
    }

    [Fact]
    public void UpdateItem_ShouldUpdateFileSizeAndHash()
    {
        // Arrange
        string basePath = "/test/base/";
        DateTime originalDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime newDate = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);
        dbInserts.Enqueue(CreateScanItem("/test/base/file1.txt", basePath, "OLD_HASH", 100, originalDate));

        // Get the item back from the DB
        var items = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Single(items);
        var existingItem = items[0];

        // Act
        DateTime scanDate = DateTime.UtcNow;
        dbInserts.UpdateItem(existingItem, 200, newDate, "NEW_HASH", scanDate);

        // Assert - re-read from DB
        var updatedItems = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Single(updatedItems);
        Assert.Equal(200, updatedItems[0].FileSize);
        Assert.Equal("NEW_HASH", updatedItems[0].FileSha512Hash);
        Assert.Equal(newDate, updatedItems[0].FileLastModificationUTC);
    }

    [Fact]
    public void UpdateItem_ShouldThrow_WhenCalledWithoutSetupDB()
    {
        using var dbInserts = new ScanJobDBInserts(null);
        var item = CreateScanItem("/test/file.txt", "/test/", "HASH", 100, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => dbInserts.UpdateItem(item, 200, DateTime.UtcNow, "NEW_HASH", DateTime.UtcNow));
    }

    [Fact]
    public void TouchItem_ShouldUpdateLastScanDate()
    {
        // Arrange
        string basePath = "/test/base/";
        DateTime originalScanDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);

        var item = CreateScanItem("/test/base/file1.txt", basePath, "HASH_A", 100, DateTime.UtcNow);
        item.LastScanDateUTC = originalScanDate;
        dbInserts.Enqueue(item);

        var items = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Single(items);
        var existingItem = items[0];

        // Act
        DateTime newScanDate = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        dbInserts.TouchItem(existingItem, newScanDate);

        // Assert
        var touchedItems = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Single(touchedItems);
        Assert.Equal(newScanDate, touchedItems[0].LastScanDateUTC);
        // Hash should remain unchanged
        Assert.Equal("HASH_A", touchedItems[0].FileSha512Hash);
    }

    [Fact]
    public void TouchItem_ShouldThrow_WhenCalledWithoutSetupDB()
    {
        using var dbInserts = new ScanJobDBInserts(null);
        var item = CreateScanItem("/test/file.txt", "/test/", "HASH", 100, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => dbInserts.TouchItem(item, DateTime.UtcNow));
    }

    [Fact]
    public void RemoveItem_ShouldRemoveItemFromDB()
    {
        // Arrange
        string basePath = "/test/base/";
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);
        dbInserts.Enqueue(CreateScanItem("/test/base/file1.txt", basePath, "HASH_A", 100, DateTime.UtcNow));
        dbInserts.Enqueue(CreateScanItem("/test/base/file2.txt", basePath, "HASH_B", 200, DateTime.UtcNow));

        var items = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Equal(2, items.Count);

        // Act
        var itemToRemove = items.First(i => i.FilenameAndPath == "/test/base/file1.txt");
        dbInserts.RemoveItem(itemToRemove);

        // Assert
        var remainingItems = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Single(remainingItems);
        Assert.Equal("/test/base/file2.txt", remainingItems[0].FilenameAndPath);
    }

    [Fact]
    public void RemoveItem_ShouldThrow_WhenCalledWithoutSetupDB()
    {
        using var dbInserts = new ScanJobDBInserts(null);
        var item = CreateScanItem("/test/file.txt", "/test/", "HASH", 100, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => dbInserts.RemoveItem(item));
    }

    [Fact]
    public void FullRefreshScenario_AddUpdateRemove()
    {
        // This test simulates a full refresh scenario:
        // 1. Initial scan puts 3 files in DB
        // 2. One file is deleted, one is modified, one is unchanged, one is new
        string basePath = "/test/base/";
        DateTime originalDate = new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);

        // Initial scan: 3 files
        dbInserts.Enqueue(CreateScanItem("/test/base/file1.txt", basePath, "HASH_1", 100, originalDate));
        dbInserts.Enqueue(CreateScanItem("/test/base/file2.txt", basePath, "HASH_2", 200, originalDate));
        dbInserts.Enqueue(CreateScanItem("/test/base/file3.txt", basePath, "HASH_3", 300, originalDate));

        // Verify initial state
        var initialItems = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Equal(3, initialItems.Count);

        // Simulate refresh:
        // file1.txt - unchanged (same size and date)
        var file1 = initialItems.First(i => i.FilenameAndPath == "/test/base/file1.txt");
        dbInserts.TouchItem(file1, DateTime.UtcNow);

        // file2.txt - modified (different size)
        var file2 = initialItems.First(i => i.FilenameAndPath == "/test/base/file2.txt");
        DateTime newDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        dbInserts.UpdateItem(file2, 250, newDate, "NEW_HASH_2", DateTime.UtcNow);

        // file3.txt - deleted from disk
        var file3 = initialItems.First(i => i.FilenameAndPath == "/test/base/file3.txt");
        dbInserts.RemoveItem(file3);

        // file4.txt - new file
        dbInserts.Enqueue(CreateScanItem("/test/base/file4.txt", basePath, "HASH_4", 400, DateTime.UtcNow));

        // Verify final state
        var finalItems = dbInserts.GetAllItemsByBasePath(basePath);
        Assert.Equal(3, finalItems.Count);

        // file1 should be unchanged
        var finalFile1 = finalItems.First(i => i.FilenameAndPath == "/test/base/file1.txt");
        Assert.Equal("HASH_1", finalFile1.FileSha512Hash);
        Assert.Equal(100, finalFile1.FileSize);

        // file2 should be updated
        var finalFile2 = finalItems.First(i => i.FilenameAndPath == "/test/base/file2.txt");
        Assert.Equal("NEW_HASH_2", finalFile2.FileSha512Hash);
        Assert.Equal(250, finalFile2.FileSize);

        // file3 should not exist
        Assert.DoesNotContain(finalItems, i => i.FilenameAndPath == "/test/base/file3.txt");

        // file4 should be present
        var finalFile4 = finalItems.First(i => i.FilenameAndPath == "/test/base/file4.txt");
        Assert.Equal("HASH_4", finalFile4.FileSha512Hash);
        Assert.Equal(400, finalFile4.FileSize);
    }
}
