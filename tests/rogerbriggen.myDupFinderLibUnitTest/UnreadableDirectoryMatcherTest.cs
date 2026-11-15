// Roger Briggen license this file to you under the MIT license.
//

using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class UnreadableDirectoryMatcherTest
{
    [Fact]
    public void Nothing_IsProtected_WhenNoDirectoryIsUnreadable()
    {
        var matcher = new UnreadableDirectoryMatcher(System.Array.Empty<string>(), @"C:\data");

        Assert.True(matcher.IsEmpty);
        Assert.False(matcher.IsProtected(@"C:\data\a\f.txt"));
        Assert.False(matcher.IsProtectedByAnyBasePath(@"D:\elsewhere\f.txt", @"C:\data"));
    }

    [Theory]
    [InlineData(@"C:\data\a\f.txt", true)]
    [InlineData(@"C:\data\a\deep\er\f.txt", true)]
    [InlineData(@"C:\data\a", true)]
    [InlineData(@"C:\data\ab\f.txt", false)]
    [InlineData(@"C:\data\a2", false)]
    [InlineData(@"C:\data\b\a\f.txt", false)]
    [InlineData(@"C:\data\f.txt", false)]
    [InlineData(@"C:\data\A\F.TXT", true)]
    [InlineData(@"C:/data/a/f.txt", true)]
    [InlineData(@"C:\data\\a\f.txt", true)]
    [InlineData(@"C:/data\a/f.txt", true)]
    [InlineData(@"C:/data/ab/f.txt", false)]
    public void IsProtected_DecidesAtDirectoryBoundary_IgnoringCaseAndSeparatorStyle(string path, bool expected)
    {
        var matcher = new UnreadableDirectoryMatcher(new[] { @"C:\data\a" }, @"C:\data");

        Assert.Equal(expected, matcher.IsProtected(path));
    }

    [Theory]
    [InlineData(@"C:\data\a")]
    [InlineData(@"C:\data\a\")]
    [InlineData(@"C:/data/a")]
    [InlineData(@"C:/data/a/")]
    [InlineData(@"c:\DATA\A")]
    public void TheUnreadableDirectory_CanBeGivenInAnyForm(string unreadable)
    {
        var matcher = new UnreadableDirectoryMatcher(new[] { unreadable }, @"C:\data");

        Assert.True(matcher.IsProtected(@"C:\data\a\f.txt"));
        Assert.False(matcher.IsProtected(@"C:\data\ab\f.txt"));
    }

    [Fact]
    public void NestedUnreadableDirectory_ProtectsOnlyItsOwnSubtree()
    {
        var matcher = new UnreadableDirectoryMatcher(new[] { @"C:\data\c\x" }, @"C:\data");

        Assert.True(matcher.IsProtected(@"C:\data\c\x\g.txt"));
        Assert.False(matcher.IsProtected(@"C:\data\c\y\g.txt"));
        Assert.False(matcher.IsProtected(@"C:\data\c\g.txt"));
        Assert.False(matcher.IsProtected(@"C:\data\c\xy\g.txt"));
    }

    [Fact]
    public void RootOfTheUnreadableDirectory_ProtectsEverythingBelow()
    {
        var matcher = new UnreadableDirectoryMatcher(new[] { @"C:\" }, @"C:\");

        Assert.True(matcher.IsProtected(@"C:\anything\f.txt"));
        Assert.False(matcher.IsProtected(@"D:\anything\f.txt"));
    }

    [Fact]
    public void UncPaths_AreMatched()
    {
        var matcher = new UnreadableDirectoryMatcher(new[] { @"\\server\share\a" }, @"\\server\share");

        Assert.True(matcher.IsProtected(@"\\server\share\a\f.txt"));
        Assert.True(matcher.IsProtected(@"//server/share/a/f.txt"));
        Assert.False(matcher.IsProtected(@"\\server\share\ab\f.txt"));
    }

    [Theory]
    [InlineData(@"C:\old\a\f.txt", @"C:\old", true)]
    [InlineData(@"C:\old\a\deep\f.txt", @"C:\old", true)]
    [InlineData(@"C:\old\ab\f.txt", @"C:\old", false)]
    [InlineData(@"C:\old\b\f.txt", @"C:\old", false)]
    [InlineData(@"C:\new\a\f.txt", @"C:\old", true)]
    [InlineData(@"Z:\nowhere\f.txt", @"C:\old", true)]
    public void IsProtectedByAnyBasePath_ComparesTheRelativePath_AndProtectsWhenInDoubt(string path, string pathBase, bool expected)
    {
        // Unreadable: C:\new\a. A row of the old base path C:\old that would be at a\... under the new base path is protected.
        // The row C:\new\a\f.txt with a wrong PathBase is protected by its absolute path, a path that is not under its own base path in doubt.
        var matcher = new UnreadableDirectoryMatcher(new[] { @"C:\new\a" }, @"C:\new");

        Assert.Equal(expected, matcher.IsProtectedByAnyBasePath(path, pathBase));
    }
}
