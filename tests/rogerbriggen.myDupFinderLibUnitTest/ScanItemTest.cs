// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class ScanItemTest
{
    [Fact]
    public void FilenameTest()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = Path.Combine("test", "diesIstEinTest", "meineDatei.txt");
        Assert.Equal("meineDatei.txt", scanItem.Filename);
        scanItem.FilenameAndPath = Path.Combine("test", "diesIstEinTest", "nochEineDatei.txt");
        Assert.Equal("nochEineDatei.txt", scanItem.Filename);
        scanItem.FilenameAndPath = Path.Combine("test", "diesIstEinTest", "meineDatei");
        Assert.Equal("meineDatei", scanItem.Filename);
    }

    [Fact]
    public void FilenameEmptyTest()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = string.Empty;
        Assert.Equal(string.Empty, scanItem.Filename);
    }

    [Fact]
    public void FilenameInTempPathTest()
    {
        ScanItemDto scanItem = new ScanItemDto();
        string tempPath = Path.GetTempPath();
        string expectedFilename = "testfile.dat";
        scanItem.FilenameAndPath = Path.Combine(tempPath, "subfolder", expectedFilename);
        Assert.Equal(expectedFilename, scanItem.Filename);
    }

    [Fact]
    public void DefaultValuesTest()
    {
        ScanItemDto scanItem = new ScanItemDto();
        Assert.Equal(string.Empty, scanItem.FilenameAndPath);
        Assert.Equal(string.Empty, scanItem.Filename);
        Assert.Equal(string.Empty, scanItem.PathBase);
        Assert.Equal(string.Empty, scanItem.ScanExecutionComputer);
        Assert.Equal(string.Empty, scanItem.OriginComputer);
        Assert.Equal(string.Empty, scanItem.ScanName);
        Assert.Equal(0, scanItem.FileSize);
        Assert.Equal(string.Empty, scanItem.FileSha512Hash);
    }

    [Fact]
    public void PropertyAssignmentTest()
    {
        ScanItemDto scanItem = new ScanItemDto();
        Guid id = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        scanItem.Id = id;
        scanItem.PathBase = "/some/base/path";
        scanItem.ScanExecutionComputer = "MyComputer";
        scanItem.OriginComputer = "OriginPC";
        scanItem.ScanName = "TestScan";
        scanItem.FileSize = 12345;
        scanItem.FileSha512Hash = "ABCDEF";
        scanItem.FileCreationUTC = now;
        scanItem.FileLastModificationUTC = now;
        scanItem.FirstScanDateUTC = now;
        scanItem.LastScanDateUTC = now;
        scanItem.LastSha512ScanDateUTC = now;

        Assert.Equal(id, scanItem.Id);
        Assert.Equal("/some/base/path", scanItem.PathBase);
        Assert.Equal("MyComputer", scanItem.ScanExecutionComputer);
        Assert.Equal("OriginPC", scanItem.OriginComputer);
        Assert.Equal("TestScan", scanItem.ScanName);
        Assert.Equal(12345, scanItem.FileSize);
        Assert.Equal("ABCDEF", scanItem.FileSha512Hash);
        Assert.Equal(now, scanItem.FileCreationUTC);
        Assert.Equal(now, scanItem.FileLastModificationUTC);
        Assert.Equal(now, scanItem.FirstScanDateUTC);
        Assert.Equal(now, scanItem.LastScanDateUTC);
        Assert.Equal(now, scanItem.LastSha512ScanDateUTC);
    }
}
