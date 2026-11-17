// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>An identity provider whose answer the test sets.</summary>
internal sealed class FakeVolumeIdentityProvider : IVolumeIdentityProvider
{
    public FakeVolumeIdentityProvider(string identity) => Result = Available(identity);

    public VolumeIdentityResult Result { get; set; }

    public int Calls { get; private set; }

    public static VolumeIdentityResult Available(string identity) => new VolumeIdentityResult(VolumeIdentityStatus.Available, identity, "fake");

    public static VolumeIdentityResult Unavailable() => new VolumeIdentityResult(VolumeIdentityStatus.PathUnavailable, null, "fake: not mounted");

    public static VolumeIdentityResult NotSupported() => new VolumeIdentityResult(VolumeIdentityStatus.NotSupported, null, "fake: no identity");

    public VolumeIdentityResult GetIdentity(string basePath)
    {
        Calls++;
        return Result;
    }
}

/// <summary>
/// The volume identity check of scan, refresh and check: what is recorded, and that a job refuses (fails, writes nothing) when the volume is not the recorded one.
/// </summary>
public class VolumeIdentityTest : IDisposable
{
    private const string Origin = "TestPC";
    private const string ScanName = "TestScan";
    private const string IdA = "test-volume:A";
    private const string IdB = "test-volume:B";

    private static readonly DateTime s_t0 = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly string _base;
    private readonly string _dbPath;
    private readonly string _reportDir;

    public VolumeIdentityTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "VolumeIdentity_" + Guid.NewGuid().ToString("N"));
        _base = FileHelper.AddDirectoryDelimiter(Path.Combine(_tempDir, "base"));
        _dbPath = Path.Combine(_tempDir, "db", "test.db");
        _reportDir = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(_base);
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

    // ---- helpers ----

    private string WriteFile(string name, int seed = 1)
    {
        string full = Path.Combine(_base, name);
        var bytes = new byte[100];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
        File.SetLastWriteTimeUtc(full, s_t0);
        return full;
    }

    private ScanItemDto Row(string fullPath, DateTime lastScan) => RowsPerPathSupport.Row(fullPath, _base, "HASH", 100, s_t0, lastScan);

    private void SeedRows(params ScanItemDto[] rows) => RowsPerPathSupport.Insert(_dbPath, rows);

    private void StoreIdentity(string identity)
    {
        using (var db = new ScanJobDBInserts(null))
        {
            db.SetupDB(_dbPath);
            db.SaveScanVolume(Origin, ScanName, _base, identity, s_t0);
        }
        SqliteConnection.ClearAllPools();
    }

    private ScanVolumeDto? ReadVolume()
    {
        ScanVolumeDto? volume;
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath))
        {
            volume = context.ScanVolumes!.AsNoTracking().SingleOrDefault();
        }
        SqliteConnection.ClearAllPools();
        return volume;
    }

    private int VolumeCount()
    {
        int count;
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath))
        {
            count = context.ScanVolumes!.Count();
        }
        SqliteConnection.ClearAllPools();
        return count;
    }

    private MyDupFinderRefreshJobDTO RefreshJob(bool accept = false) => new MyDupFinderRefreshJobDTO
    {
        JobName = "VolumeIdentity",
        OriginComputer = Origin,
        ScanName = ScanName,
        BasePath = _base,
        DatabaseFile = _dbPath,
        ReportPath = _reportDir,
        AcceptVolumeChange = accept,
    };

    private MyDupFinderScanJobDTO ScanJob(bool accept = false) => new MyDupFinderScanJobDTO
    {
        JobName = "VolumeIdentity",
        OriginComputer = Origin,
        ScanName = ScanName,
        BasePath = _base,
        DatabaseFile = _dbPath,
        ReportPath = _reportDir,
        AcceptVolumeChange = accept,
    };

    private (RefreshRunner Runner, ListLoggerProvider Log) RunRefresh(IVolumeIdentityProvider provider, IService.EServiceState expected, bool accept = false)
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = PipelineTestSupport.BuildServices(log, LogLevel.Trace);
        var runner = new RefreshRunner(RefreshJob(accept), serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider, null, null, null, provider);
        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        Assert.Equal(expected, runner.RunnerState);
        return (runner, log);
    }

    private (ScanRunner Runner, ListLoggerProvider Log) RunScan(IVolumeIdentityProvider provider, IService.EServiceState expected, bool accept = false, int hashWorkers = 1)
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = PipelineTestSupport.BuildServices(log, LogLevel.Trace);
        MyDupFinderScanJobDTO job = ScanJob(accept);
        job.HashWorkers = hashWorkers;
        var runner = new ScanRunner(job, serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, null, null, provider);
        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        Assert.Equal(expected, runner.RunnerState);
        return (runner, log);
    }

    private (CheckRunner Runner, ListLoggerProvider Log) RunCheck(IVolumeIdentityProvider provider, IService.EServiceState expected, bool accept = false)
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = PipelineTestSupport.BuildServices(log, LogLevel.Trace);
        var job = new MyDupFinderCheckJobDTO { ScanJobDTO = ScanJob(accept) };
        var runner = new CheckRunner(job, serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider, null, null, provider);
        runner.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        Assert.Equal(expected, runner.RunnerState);
        return (runner, log);
    }

    private static bool HasLog(ListLoggerProvider log, LogLevel level, params string[] parts) =>
        log.Entries.Any(e => e.Level == level && parts.All(p => e.Message.Contains(p, StringComparison.Ordinal)));

    // ---- refresh ----

    [Fact]
    public void Refresh_IdentityMatches_Finishes_RemovesAsBefore_AndUpdatesLastVerified()
    {
        string present = WriteFile("a.bin");
        string gone = Path.Combine(_base, "gone.bin");
        SeedRows(Row(present, s_t0), Row(gone, s_t0));
        StoreIdentity(IdA);

        var (runner, _) = RunRefresh(new FakeVolumeIdentityProvider(IdA), IService.EServiceState.finished);

        Assert.Equal(1, runner.RemovedCount);
        Assert.Equal(present, Assert.Single(RowsPerPathSupport.ReadRows(_dbPath)).FilenameAndPath);
        var volume = ReadVolume()!;
        Assert.Equal(IdA, volume.VolumeIdentity);
        Assert.Equal(s_t0, volume.FirstRecordedUTC);
        Assert.True(volume.LastVerifiedUTC > s_t0);
    }

    [Fact]
    public void Refresh_IdentityDiffers_Fails_AndWritesNothing()
    {
        string present = WriteFile("a.bin");
        string gone = Path.Combine(_base, "gone.bin");
        SeedRows(Row(present, s_t0), Row(gone, s_t0));
        WriteFile("new.bin", 2);
        var before = RowsPerPathSupport.Fingerprints(RowsPerPathSupport.ReadRows(_dbPath));
        StoreIdentity(IdA);

        var (runner, log) = RunRefresh(new FakeVolumeIdentityProvider(IdB), IService.EServiceState.failed);

        Assert.Equal(before, RowsPerPathSupport.Fingerprints(RowsPerPathSupport.ReadRows(_dbPath)));
        Assert.Equal(0, runner.AddedCount + runner.UpdatedCount + runner.RemovedCount + runner.UnchangedCount);
        var volume = ReadVolume()!;
        Assert.Equal(IdA, volume.VolumeIdentity);
        Assert.Equal(s_t0, volume.LastVerifiedUTC);
        Assert.True(HasLog(log, LogLevel.Error, IdA, IdB, "AcceptVolumeChange"));
    }

    [Fact]
    public void Refresh_PathUnavailable_Fails_AndWritesNothing()
    {
        string gone = Path.Combine(_base, "gone.bin");
        SeedRows(Row(gone, s_t0));
        var before = RowsPerPathSupport.Fingerprints(RowsPerPathSupport.ReadRows(_dbPath));
        StoreIdentity(IdA);

        var provider = new FakeVolumeIdentityProvider(IdA) { Result = FakeVolumeIdentityProvider.Unavailable() };
        var (_, log) = RunRefresh(provider, IService.EServiceState.failed);

        Assert.Equal(before, RowsPerPathSupport.Fingerprints(RowsPerPathSupport.ReadRows(_dbPath)));
        Assert.Equal(IdA, ReadVolume()!.VolumeIdentity);
        Assert.True(HasLog(log, LogLevel.Error, "not available", IdA));
    }

    [Fact]
    public void Refresh_NothingStored_ProceedsWarnsAndRecordsTheIdentity()
    {
        string present = WriteFile("a.bin");
        string gone = Path.Combine(_base, "gone.bin");
        SeedRows(Row(present, s_t0), Row(gone, s_t0));
        Assert.Null(ReadVolume());

        var (runner, log) = RunRefresh(new FakeVolumeIdentityProvider(IdA), IService.EServiceState.finished);

        Assert.Equal(1, runner.RemovedCount);
        Assert.Equal(IdA, ReadVolume()!.VolumeIdentity);
        Assert.True(HasLog(log, LogLevel.Warning, "not recorded before", IdA));
    }

    [Fact]
    public void Refresh_DifferingIdentity_WithAcceptVolumeChange_ProceedsAndRecordsTheNewIdentity()
    {
        string present = WriteFile("a.bin");
        SeedRows(Row(present, s_t0));
        StoreIdentity(IdA);

        var (runner, log) = RunRefresh(new FakeVolumeIdentityProvider(IdB), IService.EServiceState.finished, accept: true);

        Assert.Equal(1, runner.UnchangedCount);
        var volume = ReadVolume()!;
        Assert.Equal(IdB, volume.VolumeIdentity);
        Assert.True(volume.FirstRecordedUTC > s_t0, "FirstRecordedUTC is reset when the identity changed");
        Assert.True(HasLog(log, LogLevel.Warning, IdA, IdB));
    }

    [Fact]
    public void Refresh_NotSupported_NothingStored_ProceedsAndRecordsNothing()
    {
        string present = WriteFile("a.bin");
        string gone = Path.Combine(_base, "gone.bin");
        SeedRows(Row(present, s_t0), Row(gone, s_t0));

        var provider = new FakeVolumeIdentityProvider(IdA) { Result = FakeVolumeIdentityProvider.NotSupported() };
        var (runner, log) = RunRefresh(provider, IService.EServiceState.finished);

        Assert.Equal(1, runner.RemovedCount);
        Assert.Equal(0, VolumeCount());
        Assert.True(HasLog(log, LogLevel.Warning, "cannot be determined"));
    }

    [Fact]
    public void Refresh_NotSupported_Stored_Fails_UnlessAccepted()
    {
        string gone = Path.Combine(_base, "gone.bin");
        SeedRows(Row(gone, s_t0));
        StoreIdentity(IdA);
        var provider = new FakeVolumeIdentityProvider(IdA) { Result = FakeVolumeIdentityProvider.NotSupported() };

        RunRefresh(provider, IService.EServiceState.failed);
        Assert.Single(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(IdA, ReadVolume()!.VolumeIdentity);

        RunRefresh(provider, IService.EServiceState.finished, accept: true);
        Assert.Empty(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(IdA, ReadVolume()!.VolumeIdentity);
    }

    // ---- scan ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Scan_FirstScan_RecordsTheIdentity(int hashWorkers)
    {
        WriteFile("a.bin");
        WriteFile("b.bin", 2);

        RunScan(new FakeVolumeIdentityProvider(IdA), IService.EServiceState.finished, hashWorkers: hashWorkers);

        Assert.Equal(2, RowsPerPathSupport.ReadRows(_dbPath).Count);
        var volume = ReadVolume()!;
        Assert.Equal(IdA, volume.VolumeIdentity);
        Assert.Equal(_base, volume.BasePath);
        Assert.Equal(Origin, volume.OriginComputer);
        Assert.Equal(ScanName, volume.ScanName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Scan_IdentityDiffers_Fails_AndWritesNothing(int hashWorkers)
    {
        WriteFile("a.bin");
        StoreIdentity(IdA);

        var (_, log) = RunScan(new FakeVolumeIdentityProvider(IdB), IService.EServiceState.failed, hashWorkers: hashWorkers);

        Assert.Empty(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(IdA, ReadVolume()!.VolumeIdentity);
        Assert.True(HasLog(log, LogLevel.Error, IdA, IdB));
    }

    [Fact]
    public void Scan_PathUnavailable_Fails_AndWritesNothing()
    {
        var provider = new FakeVolumeIdentityProvider(IdA) { Result = FakeVolumeIdentityProvider.Unavailable() };

        RunScan(provider, IService.EServiceState.failed);

        Assert.Empty(RowsPerPathSupport.ReadRows(_dbPath));
        Assert.Equal(0, VolumeCount());
    }

    // ---- check ----

    [Fact]
    public void Check_IdentityDiffers_Fails_AndWritesNoReport()
    {
        string present = WriteFile("a.bin");
        SeedRows(Row(present, s_t0));
        StoreIdentity(IdA);

        var (runner, log) = RunCheck(new FakeVolumeIdentityProvider(IdB), IService.EServiceState.failed);

        Assert.Empty(Directory.GetFiles(_reportDir));
        Assert.Null(runner.ReportFilePath);
        Assert.Equal(IdA, ReadVolume()!.VolumeIdentity);
        Assert.Equal(s_t0, ReadVolume()!.LastVerifiedUTC);
        Assert.True(HasLog(log, LogLevel.Error, IdA, IdB));
    }

    [Fact]
    public void Check_NothingStored_WritesTheReport_AndRecordsNothing()
    {
        string present = WriteFile("a.bin");
        SeedRows(Row(present, s_t0));

        var (_, log) = RunCheck(new FakeVolumeIdentityProvider(IdA), IService.EServiceState.finished);

        Assert.Single(Directory.GetFiles(_reportDir));
        Assert.Equal(0, VolumeCount());
        Assert.True(HasLog(log, LogLevel.Warning, "not recorded before"));
    }

    [Fact]
    public void Check_IdentityMatches_WritesTheReport_AndDoesNotUpdateLastVerified()
    {
        string present = WriteFile("a.bin");
        SeedRows(Row(present, s_t0));
        StoreIdentity(IdA);

        RunCheck(new FakeVolumeIdentityProvider(IdA), IService.EServiceState.finished);

        Assert.Single(Directory.GetFiles(_reportDir));
        Assert.Equal(s_t0, ReadVolume()!.LastVerifiedUTC);
    }

    // ---- ScanJobDBInserts ----

    [Fact]
    public void SaveScanVolume_InsertsThenUpdates_AndKeepsFirstRecordedUntilTheIdentityChanges()
    {
        using (var db = new ScanJobDBInserts(null))
        {
            db.SetupDB(_dbPath);
            Assert.Null(db.GetScanVolume(Origin, ScanName, _base));

            db.SaveScanVolume(Origin, ScanName, _base, IdA, s_t0);
            db.SaveScanVolume(Origin, ScanName, _base, IdA, s_t0.AddDays(1));
            var same = db.GetScanVolume(Origin, ScanName, _base)!;
            Assert.Equal(s_t0, same.FirstRecordedUTC);
            Assert.Equal(s_t0.AddDays(1), same.LastVerifiedUTC);

            db.SaveScanVolume(Origin, ScanName, _base, IdB, s_t0.AddDays(2));
            var changed = db.GetScanVolume(Origin, ScanName, _base)!;
            Assert.Equal(IdB, changed.VolumeIdentity);
            Assert.Equal(s_t0.AddDays(2), changed.FirstRecordedUTC);

            Assert.Equal(0, db.TrackedEntityCount);
            Assert.Null(db.GetScanVolume(Origin, "OtherScan", _base));
        }
        Assert.Equal(1, VolumeCount());
    }

    [Fact]
    public void ScanVolumeMethods_WithoutSetupDB_Throw()
    {
        using var db = new ScanJobDBInserts(null);
        Assert.Throws<InvalidOperationException>(() => db.GetScanVolume(Origin, ScanName, _base));
        Assert.Throws<InvalidOperationException>(() => db.SaveScanVolume(Origin, ScanName, _base, IdA, s_t0));
    }

    // ---- migration ----

    [Fact]
    public void ExistingDatabase_IsMigrated_GetsTheScanVolumesTable_AndKeepsItsRows()
    {
        const string previousMigration = "20261114120000_AddFilenameAndPathIndex";
        var item = RowsPerPathSupport.Row(Path.Combine(_base, "a.bin"), _base, "HASH", 100, s_t0, s_t0);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using (var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath))
        {
            context.GetService<IMigrator>().Migrate(previousMigration);
            context.ScanItems!.Add(item);
            context.SaveChanges();
        }
        SqliteConnection.ClearAllPools();

        using (var db = new ScanJobDBInserts(null))
        {
            db.SetupDB(_dbPath);
            Assert.Null(db.GetScanVolume(Origin, ScanName, _base));
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(item.Id, Assert.Single(RowsPerPathSupport.ReadRows(_dbPath)).Id);
        Assert.Equal(0, VolumeCount());
        var options = new DbContextOptionsBuilder<DubFinderContext>().UseSqlite($"Data Source={_dbPath}").Options;
        using (var context = new DubFinderContext(options))
        {
            Assert.Empty(context.Database.GetPendingMigrations());
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Contains(context.Database.GetAppliedMigrations(), m => m.EndsWith("_AddScanVolumes", StringComparison.Ordinal));
        }
        SqliteConnection.ClearAllPools();
    }

    // ---- the provider of this computer ----

    [Fact]
    public void SystemProvider_ExistingDirectory_IsAvailable_AndStable()
    {
        var first = SystemVolumeIdentityProvider.Instance.GetIdentity(_base);
        var second = SystemVolumeIdentityProvider.Instance.GetIdentity(_base);

        Assert.Equal(VolumeIdentityStatus.Available, first.Status);
        Assert.False(string.IsNullOrWhiteSpace(first.Identity));
        Assert.Equal(first.Identity, second.Identity);
    }

    [Fact]
    public void SystemProvider_MissingDirectory_IsPathUnavailable()
    {
        var result = SystemVolumeIdentityProvider.Instance.GetIdentity(Path.Combine(_tempDir, "does", "not", "exist"));

        Assert.Equal(VolumeIdentityStatus.PathUnavailable, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void SystemProvider_Windows_UsesTheVolumeGuidOrSerialNumber()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var result = SystemVolumeIdentityProvider.Instance.GetIdentity(_base);
        Assert.Equal(VolumeIdentityStatus.Available, result.Status);
        Assert.StartsWith("win-", result.Identity, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\?\C:\dir\sub", @"C:\dir\sub")]
    [InlineData(@"\\?\UNC\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"\\?\unc\server\share", @"\\server\share")]
    [InlineData(@"\\?\Volume{1234}\dir", @"\\?\Volume{1234}\dir")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void StripWin32Prefix_ConvertsTheFinalPathNameBack(string input, string expected) =>
        Assert.Equal(expected, SystemVolumeIdentityProvider.StripWin32Prefix(input));

    [Fact]
    public void SystemProvider_DirectoryLink_GivesTheIdentityOfItsTarget()
    {
        string target = Path.Combine(_tempDir, "target");
        string link = Path.Combine(_tempDir, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating a symbolic link needs administrator rights or developer mode on Windows: nothing to test here
            return;
        }
        var viaLink = SystemVolumeIdentityProvider.Instance.GetIdentity(link);
        var direct = SystemVolumeIdentityProvider.Instance.GetIdentity(target);

        Assert.Equal(VolumeIdentityStatus.Available, viaLink.Status);
        Assert.Equal(direct.Identity, viaLink.Identity);
        Assert.Equal(direct.Detail, viaLink.Detail);
    }
}
