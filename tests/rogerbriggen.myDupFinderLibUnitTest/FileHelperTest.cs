// Roger Briggen license this file to you under the MIT license.
//

using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class FileHelperTest
{
    [Fact]
    public void EndsWithDirectoryDelimiter_ShouldReturnTrue_WhenPathEndsWithDirectorySeparatorChar()
    {
        string path = "somepath" + Path.DirectorySeparatorChar;
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(path));
    }

    [Fact]
    public void EndsWithDirectoryDelimiter_ShouldReturnTrue_WhenPathEndsWithAltDirectorySeparatorChar()
    {
        string path = "somepath" + Path.AltDirectorySeparatorChar;
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(path));
    }

    [Fact]
    public void EndsWithDirectoryDelimiter_ShouldReturnFalse_WhenPathHasNoTrailingDelimiter()
    {
        string path = "somepath";
        Assert.False(FileHelper.EndsWithDirectoryDelimiter(path));
    }

    [Fact]
    public void AddDirectoryDelimiter_ShouldAddDelimiter_WhenNotPresent()
    {
        string path = "somepath";
        string result = FileHelper.AddDirectoryDelimiter(path);
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(result));
        Assert.StartsWith(path, result);
    }

    [Fact]
    public void AddDirectoryDelimiter_ShouldNotAddDelimiter_WhenAlreadyEndsWithDirectorySeparatorChar()
    {
        string path = "somepath" + Path.DirectorySeparatorChar;
        string result = FileHelper.AddDirectoryDelimiter(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void AddDirectoryDelimiter_ShouldNotAddDelimiter_WhenAlreadyEndsWithAltDirectorySeparatorChar()
    {
        string path = "somepath" + Path.AltDirectorySeparatorChar;
        string result = FileHelper.AddDirectoryDelimiter(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void AddDirectoryDelimiter_ShouldPreservePathContent()
    {
        string basePath = Path.Combine("some", "nested", "path");
        string result = FileHelper.AddDirectoryDelimiter(basePath);
        Assert.StartsWith(basePath, result);
        Assert.True(result.Length == basePath.Length + 1);
    }
}
