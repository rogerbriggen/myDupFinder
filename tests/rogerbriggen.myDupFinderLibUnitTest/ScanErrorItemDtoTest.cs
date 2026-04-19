// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class ScanErrorItemDtoTest
{
    [Fact]
    public void Constructor_ShouldCopyPropertiesFromScanItemDto()
    {
        var scanItem = new ScanItemDto
        {
            FilenameAndPath = Path.Combine("some", "path", "testfile.txt"),
            PathBase = Path.Combine("some", "path"),
            ScanExecutionComputer = "ScanPC",
            OriginComputer = "OriginPC",
            ScanName = "TestScan",
            FileSize = 1024,
            FileCreationUTC = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FileLastModificationUTC = new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var exception = new InvalidOperationException("Test exception");
        var runStart = new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var errorItem = new ScanErrorItemDto(scanItem, exception, runStart);

        Assert.Equal(scanItem.FilenameAndPath, errorItem.FilenameAndPath);
        Assert.Equal(scanItem.PathBase, errorItem.PathBase);
        Assert.Equal(scanItem.ScanExecutionComputer, errorItem.ScanExecutionComputer);
        Assert.Equal(scanItem.OriginComputer, errorItem.OriginComputer);
        Assert.Equal(scanItem.ScanName, errorItem.ScanName);
        Assert.Equal(scanItem.FileSize, errorItem.FileSize);
        Assert.Equal(scanItem.FileCreationUTC, errorItem.FileCreationUTC);
        Assert.Equal(scanItem.FileLastModificationUTC, errorItem.FileLastModificationUTC);
        Assert.Equal(runStart, errorItem.DateRunStartedUTC);
    }

    [Fact]
    public void Constructor_ShouldExtractFilename_FromFilenameAndPath()
    {
        var scanItem = new ScanItemDto
        {
            FilenameAndPath = Path.Combine("some", "path", "testfile.txt")
        };
        var exception = new Exception("Test");
        var errorItem = new ScanErrorItemDto(scanItem, exception, DateTime.UtcNow);

        Assert.Equal("testfile.txt", errorItem.Filename);
    }

    [Fact]
    public void Constructor_ShouldStoreExceptionText()
    {
        var scanItem = new ScanItemDto { FilenameAndPath = "file.txt" };
        var exception = new InvalidOperationException("Something went wrong");
        var errorItem = new ScanErrorItemDto(scanItem, exception, DateTime.UtcNow);

        Assert.Contains("Something went wrong", errorItem.MyException);
    }

    [Fact]
    public void FilenameAndPath_ShouldUpdateFilename_WhenChanged()
    {
        var errorItem = new ScanErrorItemDto();
        errorItem.FilenameAndPath = Path.Combine("some", "path", "file.txt");
        Assert.Equal("file.txt", errorItem.Filename);

        errorItem.FilenameAndPath = Path.Combine("other", "path", "other.txt");
        Assert.Equal("other.txt", errorItem.Filename);
    }

    [Fact]
    public void DefaultConstructor_ShouldInitializeMyExceptionToEmptyString()
    {
        var errorItem = new ScanErrorItemDto();
        Assert.Equal(string.Empty, errorItem.MyException);
    }
}
