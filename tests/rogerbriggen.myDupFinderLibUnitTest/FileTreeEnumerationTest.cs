// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The shared file enumeration must return exactly the files and exactly the values that the code before step 4 used
/// (Directory.EnumerateFiles, File.GetCreationTimeUtc, File.GetLastWriteTimeUtc, FileInfo.Length): refresh and check compare them with
/// exact equality, a difference of one tick would make every file look changed.
/// </summary>
public class FileTreeEnumerationTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly List<string> _linksToRemove = new List<string>();
    private readonly List<string> _directoriesToUnlock = new List<string>();

    public FileTreeEnumerationTest(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "FileTreeEnumTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string directory in _directoriesToUnlock)
            {
                Unlock(directory);
            }
        }
        foreach (string link in _linksToRemove)
        {
            try
            {
                // Removes the link only, not the target
                if (Directory.Exists(link))
                {
                    Directory.Delete(link, false);
                }
                else if (File.Exists(link))
                {
                    File.Delete(link);
                }
            }
            catch (IOException)
            {
                // Best effort
            }
        }
        if (Directory.Exists(_tempDir))
        {
            // Read only files can not be deleted
            foreach (string path in Directory.EnumerateFiles(_tempDir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
            }
            Directory.Delete(_tempDir, true);
        }
    }

    private static List<FileEntryInfo> Enumerate(string root)
    {
        using var enumeration = new FileTreeEnumeration(root);
        var result = new List<FileEntryInfo>();
        while (enumeration.MoveNext())
        {
            result.Add(enumeration.Current);
        }
        return result;
    }

    private static void WriteFile(string path, string content = "content", FileAttributes? attributes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (attributes.HasValue)
        {
            File.SetAttributes(path, attributes.Value);
        }
    }

    /// <summary>Hidden and system files and directories, empty directories, deep nesting and names that are not plain.</summary>
    private string CreateRichTree()
    {
        string root = Path.Combine(_tempDir, "rich");
        Directory.CreateDirectory(root);
        WriteFile(Path.Combine(root, "plain.txt"));
        WriteFile(Path.Combine(root, ".hidden"), attributes: FileAttributes.Hidden);
        WriteFile(Path.Combine(root, "hidden-and-system.dat"), attributes: FileAttributes.Hidden | FileAttributes.System);
        WriteFile(Path.Combine(root, "system-only.dat"), attributes: FileAttributes.System);
        WriteFile(Path.Combine(root, "readonly.txt"), attributes: FileAttributes.ReadOnly);
        WriteFile(Path.Combine(root, "no extension"));
        WriteFile(Path.Combine(root, "ünï cödé [1] {2} #3.txt"));
        WriteFile(Path.Combine(root, "zero.bin"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "empty1"));
        Directory.CreateDirectory(Path.Combine(root, "empty2", "inner", "innermost"));
        WriteFile(Path.Combine(root, "hiddendir", "inside-hidden-dir.txt"));
        File.SetAttributes(Path.Combine(root, "hiddendir"), FileAttributes.Hidden);
        WriteFile(Path.Combine(root, "systemdir", "inside-system-dir.txt"));
        File.SetAttributes(Path.Combine(root, "systemdir"), FileAttributes.Hidden | FileAttributes.System);
        string deep = root;
        for (int i = 0; i < 25; i++)
        {
            deep = Path.Combine(deep, $"d{i:D2}");
            if (i % 5 == 0)
            {
                WriteFile(Path.Combine(deep, $"file-on-level-{i}.txt"));
            }
        }
        WriteFile(Path.Combine(deep, "deepest.txt"));
        WriteFile(Path.Combine(root, "a", "b", "c", "abc.txt"));
        WriteFile(Path.Combine(root, "a", "ab", "sibling.txt"));
        return root;
    }

    private bool TryCreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            process!.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        if (Directory.Exists(link))
        {
            _linksToRemove.Add(link);
            return true;
        }
        return false;
    }

    private bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            _linksToRemove.Add(link);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            _linksToRemove.Add(link);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void Enumerate_ReturnsExactlyTheFilesOfDirectoryEnumerateFiles_AlsoHiddenAndSystemAndInEmptyOrDeepDirectories()
    {
        string root = CreateRichTree();

        var expected = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        var actual = Enumerate(root).Select(f => f.FullPath).ToList();

        // Hidden and system files and files in hidden directories are part of it
        Assert.Contains(Path.Combine(root, ".hidden"), actual);
        Assert.Contains(Path.Combine(root, "hidden-and-system.dat"), actual);
        Assert.Contains(Path.Combine(root, "system-only.dat"), actual);
        Assert.Contains(Path.Combine(root, "hiddendir", "inside-hidden-dir.txt"), actual);
        Assert.Contains(Path.Combine(root, "systemdir", "inside-system-dir.txt"), actual);
        Assert.Contains(actual, p => p.EndsWith("deepest.txt", StringComparison.Ordinal));
        Assert.Equal(expected.Count, actual.Count);
        // The same files, in the same order and spelling
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("trailing-backslash")]
    [InlineData("forward-slashes")]
    [InlineData("mixed")]
    public void Enumerate_ReturnsTheSamePathsAsDirectoryEnumerateFiles_ForEveryFormOfTheBasePath(string form)
    {
        string root = CreateRichTree();
        string basePath = form switch
        {
            "trailing-backslash" => root + Path.DirectorySeparatorChar,
            "forward-slashes" => root.Replace('\\', '/'),
            "mixed" => root.Replace('\\', '/') + "/a/../",
            _ => root,
        };

        var expected = Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories).ToList();
        var actual = Enumerate(basePath).Select(f => f.FullPath).ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Enumerate_TreatsJunctionsAndSymbolicLinksLikeDirectoryEnumerateFiles()
    {
        string root = Path.Combine(_tempDir, "linktree");
        string outside = Path.Combine(_tempDir, "outside");
        WriteFile(Path.Combine(root, "own.txt"));
        WriteFile(Path.Combine(outside, "o1.txt"));
        WriteFile(Path.Combine(outside, "nested", "o2.txt"));
        bool junction = TryCreateJunction(Path.Combine(root, "junction"), outside);
        bool directoryLink = TryCreateDirectorySymlink(Path.Combine(root, "dirlink"), outside);
        bool fileLink = TryCreateFileSymlink(Path.Combine(root, "filelink.txt"), Path.Combine(outside, "o1.txt"));
        _output.WriteLine($"Created: junction={junction}, directory symbolic link={directoryLink}, file symbolic link={fileLink}. Missing kinds are skipped (no privilege or not Windows).");

        var expected = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        var actual = Enumerate(root).ToList();

        Assert.Equal(expected, actual.Select(f => f.FullPath).ToList());
        if (junction)
        {
            Assert.Contains(Path.Combine(root, "junction", "nested", "o2.txt"), expected);
        }
        if (directoryLink)
        {
            Assert.Contains(Path.Combine(root, "dirlink", "nested", "o2.txt"), expected);
        }
        // Values of a file symbolic link are those of the link itself, as before
        foreach (FileEntryInfo file in actual)
        {
            AssertSameValuesAsFileApis(file);
        }
    }

    [Fact]
    public void Enumerate_ReturnsTheSameValuesAsTheFileApis_ForFilesWithAssortedTimestamps()
    {
        string root = Path.Combine(_tempDir, "times");
        Directory.CreateDirectory(root);
        // Sub second ticks, old dates, far future, around the DST changes of Europe and the USA (this is all UTC: the values must not be touched by a local time zone)
        var times = new List<DateTime>
        {
            new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567),
            new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1),
            new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(9999999),
            new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(10_000),
            new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_000_000_000),
            new DateTime(2200, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(5_000_000),
            new DateTime(2023, 1, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(7654321),
            new DateTime(2023, 7, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(7654321),
            new DateTime(2023, 3, 26, 1, 30, 0, DateTimeKind.Utc).AddTicks(500),
            new DateTime(2023, 10, 29, 0, 30, 0, DateTimeKind.Utc).AddTicks(500),
            new DateTime(2023, 3, 12, 7, 30, 0, DateTimeKind.Utc).AddTicks(500),
            new DateTime(2023, 11, 5, 6, 30, 0, DateTimeKind.Utc).AddTicks(500),
            new DateTime(2024, 2, 29, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999),
        };
        for (int i = 0; i < times.Count; i++)
        {
            string path = Path.Combine(root, i % 2 == 0 ? string.Empty : "sub", $"file{i:D2}.bin");
            WriteFile(path, new string('x', i * 37));
            // Creation and last write time are different from each other
            File.SetCreationTimeUtc(path, times[i]);
            File.SetLastWriteTimeUtc(path, times[(i + 3) % times.Count]);
        }

        var files = Enumerate(root);

        Assert.Equal(times.Count, files.Count);
        foreach (FileEntryInfo file in files)
        {
            AssertSameValuesAsFileApis(file);
        }
        // The explicit times really are in the values (the test would be worthless if both sides just returned "now")
        var byName = files.ToDictionary(f => Path.GetFileName(f.FullPath), StringComparer.Ordinal);
        Assert.Equal(times[0], byName["file00.bin"].CreationTimeUtc);
        Assert.Equal(times[3], byName["file00.bin"].LastWriteTimeUtc);
        Assert.Equal(times[3], byName["file03.bin"].CreationTimeUtc);
        Assert.Equal(times[6], byName["file03.bin"].LastWriteTimeUtc);
        Assert.Equal(times[2], byName["file13.bin"].LastWriteTimeUtc);
    }

    private static void AssertSameValuesAsFileApis(FileEntryInfo file)
    {
        string path = file.FullPath;
        DateTime creation = File.GetCreationTimeUtc(path);
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);

        Assert.Equal(new FileInfo(path).Length, file.Length);
        Assert.Equal(DateTimeKind.Utc, file.CreationTimeUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, file.LastWriteTimeUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, creation.Kind);
        Assert.Equal(DateTimeKind.Utc, lastWrite.Kind);
        Assert.True(creation.Ticks == file.CreationTimeUtc.Ticks, $"Creation time of {path}: file API {creation.Ticks}, listing {file.CreationTimeUtc.Ticks}");
        Assert.True(lastWrite.Ticks == file.LastWriteTimeUtc.Ticks, $"Last write time of {path}: file API {lastWrite.Ticks}, listing {file.LastWriteTimeUtc.Ticks}");
    }

    [Fact]
    public void Enumerate_NonExistingBasePath_ThrowsFromMoveNext()
    {
        using var enumeration = new FileTreeEnumeration(Path.Combine(_tempDir, "does-not-exist"));

        Assert.Throws<DirectoryNotFoundException>(() => enumeration.MoveNext());
        Assert.Empty(enumeration.UnreadableDirectories);
        Assert.False(enumeration.MoveNext());
    }

    [Fact]
    public void Enumerate_EmptyTree_ReturnsNothing()
    {
        string root = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));

        using var enumeration = new FileTreeEnumeration(root);

        Assert.False(enumeration.MoveNext());
        Assert.Empty(enumeration.UnreadableDirectories);
    }

    /// <summary>
    /// Real file system test: a directory whose listing is denied by an ACL. Windows only. If the environment does not enforce the denial
    /// (the directory can still be listed, for example when running with special privileges) the test says so and ends without checking anything;
    /// the injected-source tests of the runners cover the logic in that case.
    /// </summary>
    [Fact]
    public void Enumerate_DirectoryWithDeniedListing_IsReportedAndTheSiblingsAreStillEnumerated()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: the ACL test is Windows only.");
            return;
        }
        string root = Path.Combine(_tempDir, "acl");
        WriteFile(Path.Combine(root, "a", "ok1.txt"));
        WriteFile(Path.Combine(root, "locked", "secret.txt"));
        WriteFile(Path.Combine(root, "locked", "sub", "secret2.txt"));
        WriteFile(Path.Combine(root, "z", "ok2.txt"));
        WriteFile(Path.Combine(root, "z", "deeper", "ok3.txt"));
        WriteFile(Path.Combine(root, "root.txt"));
        string locked = Path.Combine(root, "locked");

        _directoriesToUnlock.Add(locked);
        Lock(locked);
        try
        {
            bool denied;
            try
            {
                _ = Directory.GetFiles(locked);
                denied = false;
            }
            catch (UnauthorizedAccessException)
            {
                denied = true;
            }
            if (!denied)
            {
                _output.WriteLine("Skipped: the ACL denial is not enforced in this environment (the directory could be listed), nothing was verified.");
                return;
            }

            using var enumeration = new FileTreeEnumeration(root);
            var files = new List<string>();
            while (enumeration.MoveNext())
            {
                files.Add(enumeration.Current.FullPath);
            }

            var unreadable = Assert.Single(enumeration.UnreadableDirectories);
            Assert.Equal(locked, unreadable.Path);
            Assert.IsType<UnauthorizedAccessException>(unreadable.Error);
            Assert.Equal(
                new[]
                {
                    Path.Combine(root, "a", "ok1.txt"),
                    Path.Combine(root, "root.txt"),
                    Path.Combine(root, "z", "deeper", "ok3.txt"),
                    Path.Combine(root, "z", "ok2.txt"),
                },
                files.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Unlock(locked);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Lock(string directory)
    {
        var info = new DirectoryInfo(directory);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void Unlock(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists)
            {
                return;
            }
            DirectorySecurity security = info.GetAccessControl();
            security.RemoveAccessRuleAll(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do here; the temp directory is then left behind
        }
    }
}
