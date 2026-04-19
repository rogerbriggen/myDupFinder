// Roger Briggen license this file to you under the MIT license.
//

using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class FileHelperTest
{
    [Fact]
    public void EndsWithDirectoryDelimiter_DirectorySeparatorChar_ReturnsTrue()
    {
        string path = "somePath" + Path.DirectorySeparatorChar;
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(path));
    }

    [Fact]
    public void EndsWithDirectoryDelimiter_AltDirectorySeparatorChar_ReturnsTrue()
    {
        string path = "somePath" + Path.AltDirectorySeparatorChar;
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(path));
    }

    [Fact]
    public void EndsWithDirectoryDelimiter_NoDelimiter_ReturnsFalse()
    {
        string path = "somePath";
        Assert.False(FileHelper.EndsWithDirectoryDelimiter(path));
    }

    [Fact]
    public void EndsWithDirectoryDelimiter_EmptyString_ReturnsFalse()
    {
        Assert.False(FileHelper.EndsWithDirectoryDelimiter(string.Empty));
    }

    [Fact]
    public void AddDirectoryDelimiter_PathWithoutDelimiter_AddsDelimiter()
    {
        string path = "somePath";
        string result = FileHelper.AddDirectoryDelimiter(path);
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(result));
        Assert.StartsWith(path, result);
    }

    [Fact]
    public void AddDirectoryDelimiter_PathAlreadyHasDirectorySeparator_NoChangeNeeded()
    {
        string path = "somePath" + Path.DirectorySeparatorChar;
        string result = FileHelper.AddDirectoryDelimiter(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void AddDirectoryDelimiter_PathAlreadyHasAltDirectorySeparator_NoChangeNeeded()
    {
        string path = "somePath" + Path.AltDirectorySeparatorChar;
        string result = FileHelper.AddDirectoryDelimiter(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void AddDirectoryDelimiter_TempPath_EndsWithDelimiter()
    {
        // Path.GetTempPath() already ends with a separator on all platforms
        string tempPath = Path.GetTempPath();
        string result = FileHelper.AddDirectoryDelimiter(tempPath);
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(result));
    }
}
