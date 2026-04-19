// Roger Briggen license this file to you under the MIT license.
//

using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class ScanItemDtoTest
{
    [Fact]
    public void FilenameAndPath_ShouldExtractFilename_FromUnixStylePath()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = "/home/user/documents/myfile.txt";
        Assert.Equal("myfile.txt", scanItem.Filename);
    }

    [Fact]
    public void FilenameAndPath_ShouldReturnFilenameOnly_WhenNoDirectory()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = "justfilename.txt";
        Assert.Equal("justfilename.txt", scanItem.Filename);
    }

    [Fact]
    public void FilenameAndPath_ShouldExtractFilenameWithoutExtension()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = Path.Combine("some", "path", "fileWithoutExtension");
        Assert.Equal("fileWithoutExtension", scanItem.Filename);
    }

    [Fact]
    public void FilenameAndPath_ShouldUpdateFilename_WhenPathIsChanged()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = Path.Combine("some", "path", "firstFile.txt");
        Assert.Equal("firstFile.txt", scanItem.Filename);

        scanItem.FilenameAndPath = Path.Combine("some", "path", "secondFile.txt");
        Assert.Equal("secondFile.txt", scanItem.Filename);
    }

    [Fact]
    public void FilenameAndPath_ShouldHandleEmptyString()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = string.Empty;
        Assert.Equal(string.Empty, scanItem.Filename);
    }

    [Fact]
    public void FilenameAndPath_ShouldHandleDeepPath()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = Path.Combine("level1", "level2", "level3", "level4", "deep.file.txt");
        Assert.Equal("deep.file.txt", scanItem.Filename);
    }
}
