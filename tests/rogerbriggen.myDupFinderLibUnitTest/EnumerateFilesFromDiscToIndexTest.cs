// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class EnumerateFilesFromDiscToIndexTest
{
    private string CreateTempDirWithFiles(int fileCount)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnumFilesTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        for (int i = 0; i < fileCount; i++)
        {
            File.WriteAllText(Path.Combine(tempDir, $"testfile{i}.txt"), $"Content {i}");
        }
        return tempDir;
    }

    [Fact]
    public void EnumerateFiles_WithFiles_PopulatesScanItemCollection()
    {
        string tempDir = CreateTempDirWithFiles(5);
        try
        {
            var scanJobDto = new MyDupFinderScanJobDTO
            {
                BasePath = tempDir,
                OriginComputer = "TestPC",
                ScanName = "TestScan"
            };

            var enumerator = new EnumerateFilesFromDiscToIndex(scanJobDto, null);
            enumerator.EnumerateFiles(100);

            Assert.Equal(5, enumerator.CurrentCount);
            Assert.Equal(5, enumerator.ScanItemCollection.Count);
            Assert.False(enumerator.HasMore);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnumerateFiles_MaxFilesLimitsReturnedItems()
    {
        string tempDir = CreateTempDirWithFiles(10);
        try
        {
            var scanJobDto = new MyDupFinderScanJobDTO
            {
                BasePath = tempDir,
                OriginComputer = "TestPC",
                ScanName = "TestScan"
            };

            var enumerator = new EnumerateFilesFromDiscToIndex(scanJobDto, null);
            enumerator.EnumerateFiles(3);

            Assert.Equal(3, enumerator.CurrentCount);
            Assert.Equal(3, enumerator.ScanItemCollection.Count);
            Assert.True(enumerator.HasMore);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnumerateFiles_EmptyDirectory_HasMoreFalse()
    {
        string tempDir = CreateTempDirWithFiles(0);
        try
        {
            var scanJobDto = new MyDupFinderScanJobDTO
            {
                BasePath = tempDir,
                OriginComputer = "TestPC",
                ScanName = "TestScan"
            };

            var enumerator = new EnumerateFilesFromDiscToIndex(scanJobDto, null);
            enumerator.EnumerateFiles(100);

            Assert.Equal(0, enumerator.CurrentCount);
            Assert.Empty(enumerator.ScanItemCollection);
            Assert.False(enumerator.HasMore);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnumerateFiles_ScanItemHasCorrectProperties()
    {
        string tempDir = CreateTempDirWithFiles(1);
        try
        {
            var scanJobDto = new MyDupFinderScanJobDTO
            {
                BasePath = tempDir,
                OriginComputer = "TestOriginPC",
                ScanName = "MyScan"
            };

            var enumerator = new EnumerateFilesFromDiscToIndex(scanJobDto, null);
            enumerator.EnumerateFiles(100);

            Assert.True(enumerator.ScanItemCollection.TryDequeue(out ScanItemDto? item));
            Assert.NotNull(item);
            Assert.Equal(tempDir, item!.PathBase);
            Assert.Equal("TestOriginPC", item.OriginComputer);
            Assert.Equal("MyScan", item.ScanName);
            Assert.False(string.IsNullOrEmpty(item.FilenameAndPath));
            Assert.False(string.IsNullOrEmpty(item.Filename));
            Assert.Equal(Environment.MachineName, item.ScanExecutionComputer);
            Assert.True(item.FileSize >= 0);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnumerateFiles_MultipleCallsAccumulateCount()
    {
        string tempDir = CreateTempDirWithFiles(5);
        try
        {
            var scanJobDto = new MyDupFinderScanJobDTO
            {
                BasePath = tempDir,
                OriginComputer = "TestPC",
                ScanName = "TestScan"
            };

            var enumerator = new EnumerateFilesFromDiscToIndex(scanJobDto, null);
            enumerator.EnumerateFiles(3);
            Assert.Equal(3, enumerator.CurrentCount);
            Assert.True(enumerator.HasMore);

            // Second call processes the remaining 2 files (5 - 3 = 2), so HasMore becomes false
            enumerator.EnumerateFiles(3);
            Assert.Equal(5, enumerator.CurrentCount);
            Assert.False(enumerator.HasMore);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnumerateFiles_WhenHasMoreFalse_DoesNothing()
    {
        string tempDir = CreateTempDirWithFiles(2);
        try
        {
            var scanJobDto = new MyDupFinderScanJobDTO
            {
                BasePath = tempDir,
                OriginComputer = "TestPC",
                ScanName = "TestScan"
            };

            var enumerator = new EnumerateFilesFromDiscToIndex(scanJobDto, null);
            enumerator.EnumerateFiles(100);
            Assert.False(enumerator.HasMore);
            int countAfterFirst = enumerator.CurrentCount;

            // Call again when HasMore is false - should not change anything
            enumerator.EnumerateFiles(100);
            Assert.Equal(countAfterFirst, enumerator.CurrentCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnumerateFiles_WithSubdirectories_EnumeratesAllFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "EnumFilesTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string subDir = Path.Combine(tempDir, "subdir");
        Directory.CreateDirectory(subDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "file1.txt"), "content1");
            File.WriteAllText(Path.Combine(subDir, "file2.txt"), "content2");

            var scanJobDto = new MyDupFinderScanJobDTO
            {
                BasePath = tempDir,
                OriginComputer = "TestPC",
                ScanName = "TestScan"
            };

            var enumerator = new EnumerateFilesFromDiscToIndex(scanJobDto, null);
            enumerator.EnumerateFiles(100);

            Assert.Equal(2, enumerator.CurrentCount);
            Assert.False(enumerator.HasMore);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
