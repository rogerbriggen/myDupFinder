// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The directory entry can be stale (NTFS hard links). By default scan, refresh and check therefore decide with the metadata of the file itself;
/// only with TrustDirectoryMetadata they use the values of the directory listing. The stale listing is simulated with the injectable file source.
/// </summary>
public class TrustDirectoryMetadataTest : IDisposable
{
    private const int FileCount = 12;

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private readonly string _treeDir;
    private readonly string _dbPath;
    private readonly string _reportDir;

    public TrustDirectoryMetadataTest(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "TrustMetaTest_" + Guid.NewGuid().ToString("N"));
        _treeDir = Path.Combine(_tempDir, "tree");
        _dbPath = Path.Combine(_tempDir, "db", "scan.db");
        _reportDir = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(_reportDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static ServiceProvider BuildServices(ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        return services.BuildServiceProvider();
    }

    private MyDupFinderScanJobDTO ScanJob(bool trust = false) => new MyDupFinderScanJobDTO
    {
        JobName = "TrustTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _treeDir,
        DatabaseFile = _dbPath,
        ReportPath = _reportDir,
        TrustDirectoryMetadata = trust,
    };

    private RefreshRunner RunRefresh(IFileEnumerationSource? source, bool trust, ListLoggerProvider? log = null)
    {
        using var serviceProvider = BuildServices(log);
        var job = new MyDupFinderRefreshJobDTO
        {
            JobName = "TrustTest",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _treeDir,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
            TrustDirectoryMetadata = trust,
        };
        var runner = new RefreshRunner(job, serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider, null, source);
        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        return runner;
    }

    private (CheckRunner Runner, CheckReportRow[] Rows) RunCheck(IFileEnumerationSource? source, bool trust, bool skipHash)
    {
        using var serviceProvider = BuildServices();
        var job = new MyDupFinderCheckJobDTO { ScanJobDTO = ScanJob(trust), SkipHashCheck = skipHash };
        var runner = new CheckRunner(job, serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider, source);
        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        string report = Directory.GetFiles(_reportDir, "check-*.csv").Single();
        var (_, rows) = CheckReportCsv.Read(report);
        File.Delete(report);
        return (runner, rows.ToArray());
    }

    private List<ScanItemDto> ReadItems()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanItems!.AsNoTracking().ToList();
    }

    private static string Sha512Hex(string path) => Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(path)));

    private static FakeFileEnumerationSource SourceOf(IEnumerable<FileEntryInfo> files)
    {
        var source = new FakeFileEnumerationSource();
        source.Files.AddRange(files);
        return source;
    }

    private static List<FileEntryInfo> ListRealFiles(string root)
    {
        var list = new List<FileEntryInfo>();
        using var enumeration = new FileTreeEnumeration(root);
        while (enumeration.MoveNext())
        {
            list.Add(enumeration.Current);
        }
        return list;
    }

    /// <summary>Generates and scans a tree. Returns the path of the file that the tests change and the listing as it is now (before the change).</summary>
    private (string Changed, List<FileEntryInfo> StaleListing) ScanTreeAndSnapshotListing()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, new TestTreeOptions
        {
            FileCount = FileCount,
            DuplicateShare = 0,
            DirectoryCount = 3,
            SizeBuckets = new[] { new SizeBucket(1.0, 64, 500) },
        });
        Assert.Equal(IService.EServiceState.finished, ScanTestRunner.Run(ScanJob()).RunnerState);
        SqliteConnection.ClearAllPools();
        string changed = Path.Combine(_treeDir, tree.Files[3].RelativePath);
        return (changed, ListRealFiles(_treeDir));
    }

    /// <summary>Changes content, size and time of the file on disk.</summary>
    private static void ChangeOnDisk(string path)
    {
        File.WriteAllBytes(path, Enumerable.Range(0, 777).Select(i => (byte)(i * 13)).ToArray());
        File.SetLastWriteTimeUtc(path, new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(4321));
    }

    // ---- stale listing: the file on disk changed, the listing still has the old values ----

    [Fact]
    public void Refresh_Default_DetectsAChangeThatTheStaleDirectoryEntryDoesNotShow()
    {
        var (changed, stale) = ScanTreeAndSnapshotListing();
        ChangeOnDisk(changed);

        var runner = RunRefresh(SourceOf(stale), trust: false);

        Assert.Equal(1, runner.UpdatedCount);
        Assert.Equal(FileCount - 1, runner.UnchangedCount);
        Assert.Equal(0, runner.ErrorCount);
        var row = ReadItems().Single(r => r.FilenameAndPath == changed);
        Assert.Equal(Sha512Hex(changed), row.FileSha512Hash);
        Assert.Equal(777, row.FileSize);
        Assert.Equal(File.GetLastWriteTimeUtc(changed).Ticks, row.FileLastModificationUTC.Ticks);
    }

    [Fact]
    public void Refresh_Default_StoresTheCreationTimeOfTheFileItselfForNewFiles()
    {
        var (_, stale) = ScanTreeAndSnapshotListing();
        string added = Path.Combine(_treeDir, "added.bin");
        File.WriteAllBytes(added, new byte[100]);
        File.SetCreationTimeUtc(added, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(777));
        // The listing says something else for the new file
        var source = SourceOf(stale);
        source.Files.Add(new FileEntryInfo(added, 5, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var runner = RunRefresh(source, trust: false);

        Assert.Equal(1, runner.AddedCount);
        var row = ReadItems().Single(r => r.FilenameAndPath == added);
        Assert.Equal(100, row.FileSize);
        Assert.Equal(new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(777).Ticks, row.FileCreationUTC.Ticks);
        Assert.Equal(File.GetLastWriteTimeUtc(added).Ticks, row.FileLastModificationUTC.Ticks);
    }

    [Fact]
    public void Refresh_TrustDirectoryMetadata_TakesTheStaleValuesAtFaceValue()
    {
        var (changed, stale) = ScanTreeAndSnapshotListing();
        string oldHash = ReadItems().Single(r => r.FilenameAndPath == changed).FileSha512Hash;
        ChangeOnDisk(changed);

        var runner = RunRefresh(SourceOf(stale), trust: true);

        // This is the documented trade-off: the change is missed and the old hash stays
        Assert.Equal(0, runner.UpdatedCount);
        Assert.Equal(FileCount, runner.UnchangedCount);
        Assert.Equal(oldHash, ReadItems().Single(r => r.FilenameAndPath == changed).FileSha512Hash);
    }

    [Fact]
    public void Check_Default_SkipHashCheck_ReportsTheChangeAsModified()
    {
        var (changed, stale) = ScanTreeAndSnapshotListing();
        ChangeOnDisk(changed);

        var (runner, rows) = RunCheck(SourceOf(stale), trust: false, skipHash: true);

        var row = Assert.Single(rows);
        Assert.Equal(CheckCategory.Modified, row.Category);
        Assert.Equal(changed, row.FilenameAndPathDisk);
        Assert.Equal(777, row.FileSizeDisk);
        Assert.Equal(File.GetLastWriteTimeUtc(changed).Ticks, row.MTimeDisk!.Value.Ticks);
        Assert.Equal(FileCount - 1, runner.OkCount);
    }

    [Fact]
    public void Check_Default_WithHashing_ReportsTheChangeAsModified_NotAsBitRot()
    {
        var (changed, stale) = ScanTreeAndSnapshotListing();
        ChangeOnDisk(changed);

        var (runner, rows) = RunCheck(SourceOf(stale), trust: false, skipHash: false);

        var row = Assert.Single(rows);
        Assert.Equal(CheckCategory.Modified, row.Category);
        Assert.Equal(0, runner.BitRotSuspectCount);
        Assert.Equal(Sha512Hex(changed), row.HashDisk);
    }

    [Fact]
    public void Check_TrustDirectoryMetadata_SkipHashCheck_MissesTheChange()
    {
        var (changed, stale) = ScanTreeAndSnapshotListing();
        ChangeOnDisk(changed);

        var (runner, rows) = RunCheck(SourceOf(stale), trust: true, skipHash: true);

        // The documented trade-off
        Assert.Empty(rows);
        Assert.Equal(FileCount, runner.OkCount);
    }

    // ---- the reverse: the listing says something else for a file that did not change ----

    [Fact]
    public void Refresh_Default_IgnoresAWrongListingForAnUnchangedFile_AndDoesNotRehash()
    {
        var (changed, listing) = ScanTreeAndSnapshotListing();
        var wrong = listing.Select(f => f.FullPath == changed
            ? new FileEntryInfo(f.FullPath, f.Length + 5, f.CreationTimeUtc, f.LastWriteTimeUtc.AddMinutes(7))
            : f).ToList();

        var runner = RunRefresh(SourceOf(wrong), trust: false);

        Assert.Equal(0, runner.UpdatedCount);
        Assert.Equal(FileCount, runner.UnchangedCount);
        Assert.Equal(0, runner.Throughput.BytesHashed);
    }

    [Fact]
    public void Check_Default_IgnoresAWrongListingForAnUnchangedFile()
    {
        var (changed, listing) = ScanTreeAndSnapshotListing();
        var wrong = listing.Select(f => f.FullPath == changed
            ? new FileEntryInfo(f.FullPath, f.Length + 5, f.CreationTimeUtc, f.LastWriteTimeUtc.AddMinutes(7))
            : f).ToList();

        var (runner, rows) = RunCheck(SourceOf(wrong), trust: false, skipHash: true);

        Assert.Empty(rows);
        Assert.Equal(FileCount, runner.OkCount);
    }

    [Fact]
    public void Scan_Default_ResumeFilterUsesTheRealSize_NotAWrongListedSize()
    {
        var (changed, listing) = ScanTreeAndSnapshotListing();
        var wrong = listing.Select(f => f.FullPath == changed ? new FileEntryInfo(f.FullPath, f.Length + 1000, f.CreationTimeUtc, f.LastWriteTimeUtc) : f).ToList();
        using var serviceProvider = BuildServices();
        var runner = new ScanRunner(ScanJob(), serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, SourceOf(wrong));

        runner.Start(CancellationToken.None);

        // Everything is already in the database with its real size: all files are skipped, nothing is hashed or added
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(0, runner.Throughput.BytesHashed);
        Assert.Equal(FileCount, runner.Throughput.Files);
        Assert.Equal(FileCount, ReadItems().Count);
    }

    [Fact]
    public void Scan_Default_StoresTheValuesOfTheFileItself_AlsoWhenTheListingIsStale()
    {
        TestTreeGenerator.Generate(_treeDir, new TestTreeOptions { FileCount = 5, DirectoryCount = 1, SizeBuckets = new[] { new SizeBucket(1.0, 64, 200) } });
        var listing = ListRealFiles(_treeDir);
        string first = listing[0].FullPath;
        var stale = listing.Select(f => f.FullPath == first ? new FileEntryInfo(f.FullPath, 1, f.CreationTimeUtc, new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)) : f).ToList();
        using var serviceProvider = BuildServices();
        var runner = new ScanRunner(ScanJob(), serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, SourceOf(stale));

        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var row = ReadItems().Single(r => r.FilenameAndPath == first);
        Assert.Equal(new FileInfo(first).Length, row.FileSize);
        Assert.Equal(File.GetLastWriteTimeUtc(first).Ticks, row.FileLastModificationUTC.Ticks);
        Assert.Equal(File.GetCreationTimeUtc(first).Ticks, row.FileCreationUTC.Ticks);
    }

    [Fact]
    public void Scan_TrustDirectoryMetadata_StoresTheListedValues()
    {
        TestTreeGenerator.Generate(_treeDir, new TestTreeOptions { FileCount = 5, DirectoryCount = 1, SizeBuckets = new[] { new SizeBucket(1.0, 64, 200) } });
        var listing = ListRealFiles(_treeDir);
        string first = listing[0].FullPath;
        var listedTime = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var stale = listing.Select(f => f.FullPath == first ? new FileEntryInfo(f.FullPath, f.Length, f.CreationTimeUtc, listedTime) : f).ToList();
        using var serviceProvider = BuildServices();
        var runner = new ScanRunner(ScanJob(trust: true), serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, SourceOf(stale));

        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();

        Assert.Equal(listedTime, ReadItems().Single(r => r.FilenameAndPath == first).FileLastModificationUTC);
    }

    // ---- a file that is listed but whose metadata cannot be read ----

    [Fact]
    public void Refresh_Default_MetadataFailure_CountsAnError_KeepsTheRow_AndContinues()
    {
        var (_, listing) = ScanTreeAndSnapshotListing();
        string vanished = listing[5].FullPath;
        File.Delete(vanished);
        var log = new ListLoggerProvider();

        var runner = RunRefresh(SourceOf(listing), trust: false, log);

        // Listed but not readable: an error, no removal (it was enumerated), the other files are processed
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(1, runner.ErrorCount);
        Assert.Equal(0, runner.RemovedCount);
        Assert.Equal(FileCount - 1, runner.UnchangedCount);
        Assert.Contains(vanished, ReadItems().Select(r => r.FilenameAndPath));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(vanished, StringComparison.Ordinal));
    }

    [Fact]
    public void Check_Default_MetadataFailure_CountsAnError_DoesNotReportTheRowAsMissing_AndContinues()
    {
        var (_, listing) = ScanTreeAndSnapshotListing();
        string vanished = listing[5].FullPath;
        File.Delete(vanished);

        var (runner, rows) = RunCheck(SourceOf(listing), trust: false, skipHash: true);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(1, runner.ErrorCount);
        Assert.Equal(0, runner.MissingOnDiskCount);
        Assert.Empty(rows);
        Assert.Equal(FileCount - 1, runner.OkCount);
    }

    [Fact]
    public void Scan_Default_MetadataFailure_CountsAnError_SkipsTheFile_AndContinues()
    {
        TestTreeGenerator.Generate(_treeDir, new TestTreeOptions { FileCount = 6, DirectoryCount = 1, SizeBuckets = new[] { new SizeBucket(1.0, 64, 200) } });
        var listing = ListRealFiles(_treeDir);
        string vanished = listing[2].FullPath;
        File.Delete(vanished);
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        var runner = new ScanRunner(ScanJob(), serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, SourceOf(listing));

        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        var rows = ReadItems();
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(rows, r => r.FilenameAndPath == vanished);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("file attribute reading", StringComparison.Ordinal) && e.Message.Contains(vanished, StringComparison.Ordinal));
    }

    // ---- a real NTFS hard link ----

    [Fact]
    public void Refresh_Default_DetectsAChangeMadeThroughAnotherHardLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: the hard link test is Windows only.");
            return;
        }
        string linkA = Path.Combine(_treeDir, "sub", "linkA.bin");
        string linkB = Path.Combine(_tempDir, "outside", "linkB.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(linkA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(linkB)!);
        File.WriteAllBytes(linkA, Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(Path.Combine(_treeDir, "other.bin"), new byte[50]);
        if (!TryCreateHardLink(linkB, linkA))
        {
            _output.WriteLine("Skipped: a hard link could not be created here (not NTFS or not allowed).");
            return;
        }
        Assert.Equal(IService.EServiceState.finished, ScanTestRunner.Run(ScanJob()).RunnerState);
        SqliteConnection.ClearAllPools();
        string oldHash = ReadItems().Single(r => r.FilenameAndPath == linkA).FileSha512Hash;

        // Change content and size through link B, after a pause so that the time differs
        Thread.Sleep(50);
        File.WriteAllBytes(linkB, Enumerable.Range(0, 900).Select(i => (byte)(i * 3)).ToArray());
        File.SetLastWriteTimeUtc(linkB, new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        // (a) Record whether the directory listing of link A is stale on this machine (printed, not asserted: the OS may update it)
        var listed = ListRealFiles(_treeDir).Single(f => f.FullPath == linkA);
        var real = new FileInfo(linkA);
        _output.WriteLine($"Link A, listing: length={listed.Length} lastWrite={listed.LastWriteTimeUtc:O}");
        _output.WriteLine($"Link A, file itself: length={real.Length} lastWrite={real.LastWriteTimeUtc:O}");
        _output.WriteLine(listed.Length != real.Length || listed.LastWriteTimeUtc != real.LastWriteTimeUtc
            ? "The directory listing of link A IS stale on this machine."
            : "The directory listing of link A is NOT stale on this machine (the OS updated it).");

        // (b) A default refresh stores the correct new values
        var runner = RunRefresh(null, trust: false);

        Assert.Equal(1, runner.UpdatedCount);
        Assert.Equal(0, runner.ErrorCount);
        var row = ReadItems().Single(r => r.FilenameAndPath == linkA);
        Assert.NotEqual(oldHash, row.FileSha512Hash);
        Assert.Equal(Sha512Hex(linkA), row.FileSha512Hash);
        Assert.Equal(900, row.FileSize);
    }

    private bool TryCreateHardLink(string link, string existing)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{existing}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            process!.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        return File.Exists(link);
    }
}
