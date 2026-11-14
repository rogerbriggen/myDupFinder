// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Scan, refresh and check all take size and timestamps from the directory listing. A tree that is scanned and then refreshed and checked
/// without touching the files must show no change: if the values differed by one tick, a refresh would hash everything again.
/// </summary>
public class EnumerationEndToEndTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _treeDir;
    private readonly string _dbPath;
    private readonly string _reportDir;

    public EnumerationEndToEndTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EnumEndToEnd_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ScanThenRefreshThenCheck_WithoutTouchingTheFiles_ShowsNoChangeAtAll()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, new TestTreeOptions
        {
            FileCount = 60,
            DuplicateShare = 0.2,
            DirectoryCount = 8,
            SizeBuckets = new[] { new SizeBucket(1.0, 64, 3000) },
        });
        // Odd timestamps (sub second ticks, old, a different DST period) and a hidden and a system file
        string[] files = tree.Files.Select(f => Path.Combine(_treeDir, f.RelativePath)).ToArray();
        File.SetLastWriteTimeUtc(files[0], new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567));
        File.SetLastWriteTimeUtc(files[1], new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(3));
        File.SetLastWriteTimeUtc(files[2], new DateTime(2023, 7, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(7654321));
        File.SetLastWriteTimeUtc(files[3], new DateTime(2023, 1, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(9999999));
        File.SetAttributes(files[4], FileAttributes.Hidden);
        File.SetAttributes(files[5], FileAttributes.Hidden | FileAttributes.System);
        string hiddenFile = Path.Combine(_treeDir, "hidden-in-hidden-dir", "h.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(hiddenFile)!);
        File.WriteAllText(hiddenFile, "hidden content");
        File.SetAttributes(Path.GetDirectoryName(hiddenFile)!, FileAttributes.Hidden);
        int fileCount = tree.Files.Count + 1;

        var scanJob = new MyDupFinderScanJobDTO
        {
            JobName = "EndToEnd",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _treeDir,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
        };
        var scan = ScanTestRunner.Run(scanJob);
        Assert.Equal(IService.EServiceState.finished, scan.RunnerState);
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath))
        {
            var rows = context.ScanItems!.AsNoTracking().ToList();
            Assert.Equal(fileCount, rows.Count);
            // The stored values are those of the file APIs
            foreach (var row in rows)
            {
                Assert.Equal(new FileInfo(row.FilenameAndPath).Length, row.FileSize);
                Assert.Equal(File.GetLastWriteTimeUtc(row.FilenameAndPath).Ticks, row.FileLastModificationUTC.Ticks);
                Assert.Equal(File.GetCreationTimeUtc(row.FilenameAndPath).Ticks, row.FileCreationUTC.Ticks);
            }
        }
        SqliteConnection.ClearAllPools();

        var services = new ServiceCollection();
        services.AddLogging();
        using var serviceProvider = services.BuildServiceProvider();

        // Refresh: nothing updated, nothing added or removed, no file hashed again
        var refresh = new RefreshRunner(new MyDupFinderRefreshJobDTO
        {
            JobName = "EndToEnd",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _treeDir,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
        }, serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider);
        refresh.Start(CancellationToken.None);
        Assert.Equal(IService.EServiceState.finished, refresh.RunnerState);
        Assert.Equal(0, refresh.UpdatedCount);
        Assert.Equal(0, refresh.AddedCount);
        Assert.Equal(0, refresh.RemovedCount);
        Assert.Equal(0, refresh.ErrorCount);
        Assert.Equal(0, refresh.ProtectedCount);
        Assert.Equal(fileCount, refresh.UnchangedCount);
        Assert.Equal(0, refresh.Throughput.BytesHashed);
        SqliteConnection.ClearAllPools();

        // Check (with hashing): everything is Ok
        var check = new CheckRunner(new MyDupFinderCheckJobDTO
        {
            ScanJobDTO = new MyDupFinderScanJobDTO
            {
                JobName = "EndToEnd",
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = _treeDir,
                DatabaseFile = _dbPath,
                ReportPath = _reportDir,
            },
        }, serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider);
        check.Start(CancellationToken.None);
        Assert.Equal(IService.EServiceState.finished, check.RunnerState);
        Assert.Equal(fileCount, check.OkCount);
        Assert.Equal(0, check.ModifiedCount);
        Assert.Equal(0, check.ModifiedNoHashChangeCount);
        Assert.Equal(0, check.BitRotSuspectCount);
        Assert.Equal(0, check.NewOnDiskCount);
        Assert.Equal(0, check.MissingOnDiskCount);
        Assert.Equal(0, check.ErrorCount);
        Assert.Equal(0, check.UnreadableProtectedCount);
        var (_, reportRows) = CheckReportCsv.Read(check.ReportFilePath!);
        Assert.Empty(reportRows);
    }

    [Fact]
    public void Refresh_ChangedAndDeletedFiles_StillDetected_WithTheValuesFromTheListing()
    {
        var tree = TestTreeGenerator.Generate(_treeDir, new TestTreeOptions
        {
            FileCount = 20,
            DirectoryCount = 4,
            SizeBuckets = new[] { new SizeBucket(1.0, 64, 500) },
        });
        string[] files = tree.Files.Select(f => Path.Combine(_treeDir, f.RelativePath)).ToArray();
        var scanJob = new MyDupFinderScanJobDTO { JobName = "EndToEnd", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _treeDir, DatabaseFile = _dbPath, ReportPath = _reportDir };
        Assert.Equal(IService.EServiceState.finished, ScanTestRunner.Run(scanJob).RunnerState);
        SqliteConnection.ClearAllPools();
        // Same size, only the time changes (one tick) / content and size change / deleted
        File.SetLastWriteTimeUtc(files[0], File.GetLastWriteTimeUtc(files[0]).AddTicks(1));
        File.WriteAllText(files[1], "new content with another size");
        File.Delete(files[2]);

        var services = new ServiceCollection();
        services.AddLogging();
        using var serviceProvider = services.BuildServiceProvider();
        var refresh = new RefreshRunner(new MyDupFinderRefreshJobDTO { JobName = "EndToEnd", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _treeDir, DatabaseFile = _dbPath, ReportPath = _reportDir }, serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider);
        refresh.Start(CancellationToken.None);

        Assert.Equal(2, refresh.UpdatedCount);
        Assert.Equal(1, refresh.RemovedCount);
        Assert.Equal(17, refresh.UnchangedCount);
        Assert.Equal(0, refresh.ProtectedCount);
    }
}
