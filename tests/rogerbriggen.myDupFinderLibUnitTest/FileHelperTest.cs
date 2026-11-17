// Roger Briggen license this file to you under the MIT license.
//

using System;
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

    [Theory]
    [InlineData("data", "data", true)]
    [InlineData("data/", "data", true)]
    [InlineData("data", "data/", true)]
    [InlineData("data/sub/x.db", "data", true)]
    [InlineData("data/sub/", "data/", true)]
    [InlineData("data2", "data", false)]
    [InlineData("data2/x.db", "data", false)]
    [InlineData("other/x.db", "data", false)]
    [InlineData("data/../data2/x.db", "data", false)]
    [InlineData("data/./sub/../x.db", "data", true)]
    [InlineData("other/../data/x.db", "./data", true)]
    [InlineData("data/x.db", "data/sub/..", true)]
    public void IsSameOrBelow_ResolvesRelativePathsAndStopsAtSeparators(string path, string basePath, bool expected)
    {
        // Relative to the current directory; the separators are converted so that the test runs on every platform
        Assert.Equal(expected, FileHelper.IsSameOrBelow(Sep(path), Sep(basePath)));
    }

    [Fact]
    public void IsSameOrBelow_UsesTheAltSeparator()
    {
        string root = Path.Combine(Path.GetTempPath(), "data");
        string withAlt = root.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.True(FileHelper.IsSameOrBelow(withAlt + "/x.db", root));
        Assert.True(FileHelper.IsSameOrBelow(Path.Combine(root, "x.db"), withAlt));
        Assert.False(FileHelper.IsSameOrBelow(withAlt + "2/x.db", root));
    }

    [Fact]
    public void IsSameOrBelow_ComparesCaseOnlyOnWindows()
    {
        string root = Path.Combine(Path.GetTempPath(), "data");
        string upper = Path.Combine(Path.GetTempPath(), "DATA", "x.db");
        Assert.Equal(OperatingSystem.IsWindows(), FileHelper.IsSameOrBelow(upper, root));
    }

    [Fact]
    public void IsSameOrBelow_RootAsBaseContainsEverythingOnThatRoot()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        Assert.True(FileHelper.IsSameOrBelow(Path.Combine(root, "x.db"), root));
        Assert.True(FileHelper.IsSameOrBelow(Path.GetTempPath(), root));
        Assert.True(FileHelper.IsSameOrBelow(root, root));
    }

    [Fact]
    public void IsSameOrBelow_RelativePathIsResolvedAgainstTheCurrentDirectory()
    {
        string cwd = Environment.CurrentDirectory;
        Assert.True(FileHelper.IsSameOrBelow("x.db", cwd));
        Assert.True(FileHelper.IsSameOrBelow(Path.Combine("sub", "x.db"), "." + Path.DirectorySeparatorChar));
        Assert.False(FileHelper.IsSameOrBelow(Path.Combine("..", "x.db"), cwd));
    }

    private static string Sep(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}
