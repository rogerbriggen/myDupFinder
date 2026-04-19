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
    public void Constructor_FromScanItem_CopiesProperties()
    {
        var scanItem = new ScanItemDto
        {
            FilenameAndPath = Path.Combine("some", "path", "file.txt"),
            PathBase = Path.Combine("some", "path"),
            ScanExecutionComputer = "ScanPC",
            OriginComputer = "OriginPC",
            ScanName = "MyScan",
            FileSize = 9876,
            FileCreationUTC = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FileLastModificationUTC = new DateTime(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        var exception = new InvalidOperationException("Test error");
        var runStarted = new DateTime(2023, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var errorItem = new ScanErrorItemDto(scanItem, exception, runStarted);

        Assert.Equal(scanItem.FilenameAndPath, errorItem.FilenameAndPath);
        Assert.Equal(scanItem.PathBase, errorItem.PathBase);
        Assert.Equal(scanItem.ScanExecutionComputer, errorItem.ScanExecutionComputer);
        Assert.Equal(scanItem.OriginComputer, errorItem.OriginComputer);
        Assert.Equal(scanItem.ScanName, errorItem.ScanName);
        Assert.Equal(scanItem.FileSize, errorItem.FileSize);
        Assert.Equal(scanItem.FileCreationUTC, errorItem.FileCreationUTC);
        Assert.Equal(scanItem.FileLastModificationUTC, errorItem.FileLastModificationUTC);
        Assert.Equal(runStarted, errorItem.DateRunStartedUTC);
        Assert.Contains("Test error", errorItem.MyException);
    }

    [Fact]
    public void Constructor_FromScanItem_SetsFilenameCorrectly()
    {
        string filename = "myfile.dat";
        var scanItem = new ScanItemDto
        {
            FilenameAndPath = Path.Combine("some", "path", filename)
        };

        var errorItem = new ScanErrorItemDto(scanItem, new Exception("err"), DateTime.UtcNow);

        Assert.Equal(filename, errorItem.Filename);
    }

    [Fact]
    public void DefaultConstructor_InitializesMyException()
    {
        var errorItem = new ScanErrorItemDto();
        Assert.Equal(string.Empty, errorItem.MyException);
    }

    [Fact]
    public void FilenameAndPath_WhenSet_UpdatesFilename()
    {
        var errorItem = new ScanErrorItemDto();
        string filename = "newfile.csv";
        errorItem.FilenameAndPath = Path.Combine("another", "path", filename);
        Assert.Equal(filename, errorItem.Filename);
    }
}
