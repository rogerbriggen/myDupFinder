// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The dictionary based lookup of check (IgnoreBasePath) must pick exactly the row that the old walk over all rows picked, for every input.
/// </summary>
public class RelativePathIndexTest
{
    private const string CurrentBase = @"C:\new\base";

    /// <summary>
    /// The implementation of CheckRunner.FindByRelativePath before step 4, unchanged, as the reference.
    /// </summary>
    private static ScanItemDto? ReferenceFindByRelativePath(string currentFile, string basePath, IEnumerable<ScanItemDto> candidates, HashSet<Guid> seenIds)
    {
        var relative = ReferenceGetRelativePathIfUnderBase(currentFile, basePath);
        if (relative is null)
        {
            return null;
        }
        foreach (var candidate in candidates)
        {
            // Skip rows already claimed by an exact match in this run.
            if (seenIds.Contains(candidate.Id))
            {
                continue;
            }
            // Skip rows that already live under our current BasePath - those would have been caught by the exact-path branch.
            if (string.Equals(candidate.PathBase, basePath, StringComparison.Ordinal))
            {
                continue;
            }
            var candidateRelative = ReferenceGetRelativePathIfUnderBase(candidate.FilenameAndPath, candidate.PathBase);
            if (!string.Equals(candidateRelative, relative, StringComparison.Ordinal))
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    private static string? ReferenceGetRelativePathIfUnderBase(string path, string basePath)
    {
        if (!path.StartsWith(basePath, StringComparison.Ordinal))
        {
            return null;
        }
        return path.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static ScanItemDto Row(Random random, string pathBase, string path) => new ScanItemDto
    {
        Id = RandomGuid(random),
        PathBase = pathBase,
        FilenameAndPath = path,
    };

    private static Guid RandomGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    [Fact]
    public void Find_PicksTheSameRowAsTheOldWalk_ForThousandsOfRandomCases()
    {
        string[] oldBases = { @"C:\old\a", @"C:\old\ab", @"D:\x", @"C:\new\base", @"C:\new\base2", @"C:\new\ba" };
        string[] relatives =
        {
            "f1.txt", "f2.txt", @"sub\f1.txt", @"sub\f2.txt", @"sub\deep\f1.txt", @"other\f1.txt", "f1.TXT", @"sub/f1.txt", @"x\y\z\w.bin", "dup.bin",
        };
        int matchesFound = 0;
        int nullsReturned = 0;
        int claimedSkips = 0;

        for (int seed = 0; seed < 4000; seed++)
        {
            var random = new Random(seed);

            // Rows: duplicate relative paths under several base paths, rows under the current base path, rows that are not below their own PathBase
            var rows = new List<ScanItemDto>();
            int rowCount = random.Next(0, 40);
            for (int i = 0; i < rowCount; i++)
            {
                string pathBase = oldBases[random.Next(oldBases.Length)];
                string relative = relatives[random.Next(relatives.Length)];
                int kind = random.Next(10);
                string path = kind switch
                {
                    0 => @"E:\not\under\its\base\" + relative,
                    1 => pathBase + relative,
                    2 => pathBase + @"\\" + relative,
                    _ => pathBase + @"\" + relative,
                };
                rows.Add(Row(random, pathBase, path));
            }
            // The same collection and order that the runner passes: the values of the dictionary of path to row, so unique paths
            var existingByPath = new Dictionary<string, ScanItemDto>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                existingByPath[row.FilenameAndPath] = row;
            }
            var candidates = existingByPath.Values.ToList();
            var index = new RelativePathIndex(candidates, CurrentBase);

            // Files of the current base path, processed in order, with claims that grow
            var seenOld = new HashSet<Guid>();
            var seenNew = new HashSet<Guid>();
            int fileCount = random.Next(1, 40);
            for (int f = 0; f < fileCount; f++)
            {
                string relative = relatives[random.Next(relatives.Length)];
                string file = random.Next(12) switch
                {
                    0 => @"F:\outside\the\base\" + relative,
                    1 => CurrentBase + @"\\" + relative,
                    2 => CurrentBase + "/" + relative,
                    _ => CurrentBase + @"\" + relative,
                };
                // Exact matches elsewhere in the run claim rows too
                if (candidates.Count > 0 && random.Next(4) == 0)
                {
                    Guid claimed = candidates[random.Next(candidates.Count)].Id;
                    seenOld.Add(claimed);
                    seenNew.Add(claimed);
                }

                var expected = ReferenceFindByRelativePath(file, CurrentBase, candidates, seenOld);
                var actual = index.Find(file, seenNew);

                Assert.Same(expected, actual);
                if (expected is null)
                {
                    nullsReturned++;
                }
                else
                {
                    matchesFound++;
                    // Production: the matched row is claimed
                    seenOld.Add(expected.Id);
                    seenNew.Add(actual!.Id);
                    if (candidates.Count(c => ReferenceGetRelativePathIfUnderBase(c.FilenameAndPath, c.PathBase) == ReferenceGetRelativePathIfUnderBase(file, CurrentBase)
                        && c.PathBase != CurrentBase) > 1)
                    {
                        claimedSkips++;
                    }
                }
            }
        }

        // The random cases really exercise matches, misses and duplicate candidates
        Assert.True(matchesFound > 5000, $"matches: {matchesFound}");
        Assert.True(nullsReturned > 5000, $"misses: {nullsReturned}");
        Assert.True(claimedSkips > 500, $"matches with several candidates: {claimedSkips}");
    }

    [Fact]
    public void Find_TieBreak_FirstUnclaimedRowInTheOrderOfTheCandidates()
    {
        var random = new Random(1);
        var first = Row(random, @"C:\old\a", @"C:\old\a\f.txt");
        var second = Row(random, @"D:\other", @"D:\other\f.txt");
        var third = Row(random, @"C:\old\a", @"C:\old\a\\f.txt");
        var index = new RelativePathIndex(new[] { first, second, third }, CurrentBase);
        var seen = new HashSet<Guid>();

        Assert.Same(first, index.Find(CurrentBase + @"\f.txt", seen));
        seen.Add(first.Id);
        Assert.Same(second, index.Find(CurrentBase + @"\f.txt", seen));
        seen.Add(second.Id);
        Assert.Same(third, index.Find(CurrentBase + @"\f.txt", seen));
        seen.Add(third.Id);
        Assert.Null(index.Find(CurrentBase + @"\f.txt", seen));
    }

    [Fact]
    public void Find_IgnoresRowsUnderTheCurrentBasePath_AndRowsNotUnderTheirOwnBase()
    {
        var random = new Random(2);
        var underCurrent = Row(random, CurrentBase, CurrentBase + @"\f.txt");
        var notUnderOwnBase = Row(random, @"C:\old", @"X:\somewhere\f.txt");
        var index = new RelativePathIndex(new[] { underCurrent, notUnderOwnBase }, CurrentBase);

        Assert.Null(index.Find(CurrentBase + @"\f.txt", new HashSet<Guid>()));
        Assert.Null(index.Find(@"X:\somewhere\f.txt", new HashSet<Guid>()));
    }
}
