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
/// Refresh, check and scan with a file list that reports directories as unreadable (injected source, no ACLs needed).
/// A directory that could not be read must never make its files look deleted.
/// </summary>
public class UnreadableDirectoryRunnerTest : IDisposable
{
    private static readonly DateTime s_mtime = new DateTime(2022, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddTicks(1234567);

    private readonly string _tempDir;
    private readonly string _basePath;
    private readonly string _dbPath;
    private readonly string _reportDir;

    public UnreadableDirectoryRunnerTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UnreadableDirTest_" + Guid.NewGuid().ToString("N"));
        _basePath = Path.Combine(_tempDir, "base");
        _dbPath = Path.Combine(_tempDir, "db", "test.db");
        _reportDir = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(_basePath);
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

    private string P(string relative) => Path.Combine(_basePath, relative);

    private static ServiceProvider BuildServices(ILoggerProvider? loggerProvider = null, LogLevel minimumLevel = LogLevel.Information)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Puts a row for the path into the database. The file does not need to exist, the runners with the fake source never touch it.</summary>
    private void Seed(string fullPath, string? pathBase = null, long size = 10)
    {
        using var db = new ScanJobDBInserts(null);
        db.SetupDB(_dbPath);
        db.Enqueue(new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = fullPath,
            PathBase = pathBase ?? _basePath,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = size,
            FileSha512Hash = "ABCDEF",
            FileCreationUTC = s_mtime,
            FileLastModificationUTC = s_mtime,
            FirstScanDateUTC = s_mtime,
            LastScanDateUTC = s_mtime,
            LastSha512ScanDateUTC = s_mtime,
        });
        db.WriteChanges();
        SqliteConnection.ClearAllPools();
    }

    private List<string> ReadPaths()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanItems!.AsNoTracking().Select(i => i.FilenameAndPath).ToList();
    }

    // These tests are about unreadable directories and use a made-up file list (the listed files do not exist on disk), so the listed values are
    // trusted. The default mode, which reads every file, is covered by TrustDirectoryMetadataTest.
    private MyDupFinderRefreshJobDTO RefreshJob() => new MyDupFinderRefreshJobDTO
    {
        TrustDirectoryMetadata = true,
        JobName = "UnreadableTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _basePath,
        DatabaseFile = _dbPath,
        ReportPath = _reportDir,
    };

    private MyDupFinderCheckJobDTO CheckJob(bool ignoreBasePath = false, string? basePath = null) => new MyDupFinderCheckJobDTO
    {
        ScanJobDTO = new MyDupFinderScanJobDTO
        {
            JobName = "UnreadableTest",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = basePath ?? _basePath,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
            TrustDirectoryMetadata = true,
        },
        SkipHashCheck = true,
        IgnoreBasePath = ignoreBasePath,
    };

    private RefreshRunner RunRefresh(FakeFileEnumerationSource source, ListLoggerProvider? log = null, CancellationToken token = default)
    {
        using var serviceProvider = BuildServices(log, LogLevel.Trace);
        var runner = new RefreshRunner(RefreshJob(), serviceProvider.GetService<ILogger<RefreshRunner>>(), serviceProvider, null, source);
        runner.Start(token);
        return runner;
    }

    private (CheckRunner Runner, CheckReportRow[] Rows) RunCheck(FakeFileEnumerationSource source, MyDupFinderCheckJobDTO job, ListLoggerProvider? log = null, CancellationToken token = default)
    {
        using var serviceProvider = BuildServices(log, LogLevel.Trace);
        var runner = new CheckRunner(job, serviceProvider.GetService<ILogger<CheckRunner>>(), serviceProvider, source);
        runner.Start(token);
        var reports = Directory.GetFiles(_reportDir, "check-*.csv");
        Assert.Single(reports);
        var (_, rows) = CheckReportCsv.Read(reports[0]);
        var rowArray = rows.ToArray();
        File.Delete(reports[0]);
        return (runner, rowArray);
    }

    /// <summary>
    /// Rows: under the unreadable directories a and c\x (including the sibling-prefix names ab and A, a path with forward slashes and a deeper one),
    /// deleted files elsewhere, and one file that is still there and unchanged.
    /// </summary>
    private FakeFileEnumerationSource SeedScenario()
    {
        string[] underUnreadable =
        {
            P(@"a\f1.txt"), P(@"a\deep\er\f2.txt"), P(@"c\x\g1.txt"), P(@"c\x\inner\g3.txt"), P(@"A\upper.txt"), _basePath + "/a/slash.txt",
        };
        string[] deletedElsewhere =
        {
            P(@"ab\f3.txt"), P(@"c\y\g2.txt"), P(@"d\gone.txt"), P(@"c\xy\g4.txt"),
        };
        foreach (string path in underUnreadable.Concat(deletedElsewhere).Append(P(@"c\keep.txt")))
        {
            Seed(path);
        }
        var source = new FakeFileEnumerationSource();
        source.Files.Add(FakeFileEnumerationSource.Entry(P(@"c\keep.txt"), 10, s_mtime));
        source.UnreadableDirectories.Add(P("a"));
        source.UnreadableDirectories.Add(P(@"c\x"));
        return source;
    }

    [Fact]
    public void Refresh_KeepsRowsBelowUnreadableDirectories_RemovesRealDeletions_AndCountsBoth()
    {
        var source = SeedScenario();
        var log = new ListLoggerProvider();

        var runner = RunRefresh(source, log);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        // Removed: ab\ (sibling prefix of a\, NOT protected), c\y, c\xy (sibling prefix of c\x), d\
        Assert.Equal(4, runner.RemovedCount);
        // Kept: a\f1, a\deep\er\f2, c\x\g1, c\x\inner\g3, A\upper (other case), a/slash (other separator)
        Assert.Equal(6, runner.ProtectedCount);
        Assert.Equal(1, runner.UnchangedCount);
        Assert.Equal(0, runner.AddedCount);
        Assert.Equal(0, runner.ErrorCount);
        Assert.Equal(2, runner.UnreadableDirectories.Count);
        var remaining = ReadPaths();
        Assert.Equal(7, remaining.Count);
        Assert.Contains(P(@"c\keep.txt"), remaining);
        Assert.Contains(P(@"a\f1.txt"), remaining);
        Assert.Contains(P(@"a\deep\er\f2.txt"), remaining);
        Assert.Contains(P(@"c\x\g1.txt"), remaining);
        Assert.Contains(P(@"c\x\inner\g3.txt"), remaining);
        Assert.Contains(P(@"A\upper.txt"), remaining);
        Assert.Contains(_basePath + "/a/slash.txt", remaining);
        Assert.DoesNotContain(P(@"ab\f3.txt"), remaining);
        Assert.DoesNotContain(P(@"c\y\g2.txt"), remaining);
        Assert.DoesNotContain(P(@"d\gone.txt"), remaining);
        Assert.DoesNotContain(P(@"c\xy\g4.txt"), remaining);
        // Logged: a warning per directory and the counts in the summary
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(P("a"), StringComparison.Ordinal) && e.Message.Contains("could not be read", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(P(@"c\x"), StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Refresh finished.", StringComparison.Ordinal) && e.Message.Contains("Removed=4", StringComparison.Ordinal) && e.Message.Contains("KeptBecauseDirectoryUnreadable=6", StringComparison.Ordinal) && e.Message.Contains("UnreadableDirectories=2", StringComparison.Ordinal));
    }

    [Fact]
    public void Refresh_WithoutUnreadableDirectories_RemovesAllDeletedRows()
    {
        var source = SeedScenario();
        source.UnreadableDirectories.Clear();

        var runner = RunRefresh(source);

        Assert.Equal(10, runner.RemovedCount);
        Assert.Equal(0, runner.ProtectedCount);
        Assert.Equal(new[] { P(@"c\keep.txt") }, ReadPaths());
    }

    [Fact]
    public void Refresh_UnreadableDirectoryFoundEarly_GivesTheSameResultAsFoundLate()
    {
        var source = SeedScenario();
        source.Files.Add(FakeFileEnumerationSource.Entry(P(@"d\new-file.txt"), 5, s_mtime));
        source.UnreadableFoundAtFileIndex = 0;

        var runner = RunRefresh(source);

        Assert.Equal(4, runner.RemovedCount);
        Assert.Equal(6, runner.ProtectedCount);
    }

    [Fact]
    public void Refresh_BasePathCannotBeListed_RemovesNothing()
    {
        var source = SeedScenario();
        source.BasePathError = new DirectoryNotFoundException("simulated");
        var log = new ListLoggerProvider();

        var runner = RunRefresh(source, log);

        Assert.Equal(IService.EServiceState.failed, runner.RunnerState);
        Assert.Equal(0, runner.RemovedCount);
        Assert.Equal(11, ReadPaths().Count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Error enumerating files", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Refresh finished.", StringComparison.Ordinal));
    }

    [Fact]
    public void Refresh_CancelledDuringTheEnumeration_RemovesNothing_EvenWhenFilesWereDeleted()
    {
        var source = SeedScenario();
        // 40 more files that are on disk, so there is an enumeration to cancel in
        for (int i = 0; i < 40; i++)
        {
            source.Files.Add(FakeFileEnumerationSource.Entry(P($@"new\n{i}.bin"), 3, s_mtime));
        }
        using var cts = new CancellationTokenSource();
        source.BeforeFile = index =>
        {
            if (index == 10)
            {
                cts.Cancel();
            }
        };

        var runner = RunRefresh(source, token: cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.Equal(0, runner.RemovedCount);
        // All 11 seeded rows are still there, the genuinely deleted ones too: the enumeration was incomplete
        var remaining = ReadPaths();
        foreach (string deleted in new[] { P(@"ab\f3.txt"), P(@"c\y\g2.txt"), P(@"d\gone.txt"), P(@"c\xy\g4.txt") })
        {
            Assert.Contains(deleted, remaining);
        }
    }

    [Fact]
    public void Check_DoesNotReportRowsBelowUnreadableDirectoriesAsMissing_ButReportsRealMissingFiles()
    {
        var source = SeedScenario();
        var log = new ListLoggerProvider();

        var (runner, rows) = RunCheck(source, CheckJob(), log);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        var missing = rows.Where(r => r.Category == CheckCategory.MissingOnDisk).Select(r => r.FilenameAndPathDB).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { P(@"ab\f3.txt"), P(@"c\xy\g4.txt"), P(@"c\y\g2.txt"), P(@"d\gone.txt") }.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            missing);
        Assert.Equal(4, runner.MissingOnDiskCount);
        Assert.Equal(6, runner.UnreadableProtectedCount);
        Assert.Equal(1, runner.OkCount);
        Assert.Equal(2, runner.UnreadableDirectories.Count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(P("a"), StringComparison.Ordinal) && e.Message.Contains("could not be read", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Check finished.", StringComparison.Ordinal) && e.Message.Contains("Missing=4", StringComparison.Ordinal) && e.Message.Contains("UnreadableDirectories=2", StringComparison.Ordinal) && e.Message.Contains("NotReportedMissingBecauseUnreadable=6", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_WithoutUnreadableDirectories_ReportsAllMissingRows()
    {
        var source = SeedScenario();
        source.UnreadableDirectories.Clear();

        var (runner, _) = RunCheck(source, CheckJob());

        Assert.Equal(10, runner.MissingOnDiskCount);
        Assert.Equal(0, runner.UnreadableProtectedCount);
    }

    [Fact]
    public void Check_BasePathCannotBeListed_ReportsNothing()
    {
        var source = SeedScenario();
        source.BasePathError = new DirectoryNotFoundException("simulated");
        var log = new ListLoggerProvider();

        var (runner, rows) = RunCheck(source, CheckJob(), log);

        Assert.Equal(IService.EServiceState.failed, runner.RunnerState);
        Assert.Empty(rows);
        Assert.Equal(0, runner.MissingOnDiskCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Error enumerating files", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_CancelledDuringTheEnumeration_ReportsNoMissingRows()
    {
        var source = SeedScenario();
        for (int i = 0; i < 20; i++)
        {
            source.Files.Add(FakeFileEnumerationSource.Entry(P($@"new\n{i}.bin"), 3, s_mtime));
        }
        using var cts = new CancellationTokenSource();
        source.BeforeFile = index =>
        {
            if (index == 5)
            {
                cts.Cancel();
            }
        };

        var (runner, rows) = RunCheck(source, CheckJob(), token: cts.Token);

        Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
        Assert.DoesNotContain(rows, r => r.Category == CheckCategory.MissingOnDisk);
        Assert.Equal(0, runner.MissingOnDiskCount);
    }

    [Fact]
    public void Check_IgnoreBasePath_ProtectsRowsOfAnOldBasePathByTheirRelativePath()
    {
        // Rows from the old base path (the tree was moved). The unreadable directory is below the new base path.
        string oldBase = Path.Combine(_tempDir, "oldbase");
        string[] underUnreadable = { Path.Combine(oldBase, "a", "f1.txt"), Path.Combine(oldBase, "a", "deep", "f2.txt") };
        string[] gone = { Path.Combine(oldBase, "ab", "f3.txt"), Path.Combine(oldBase, "d", "gone.txt") };
        foreach (string path in underUnreadable.Concat(gone))
        {
            Seed(path, oldBase);
        }
        // A row whose path is not below its own PathBase: nobody knows where it would be, it is kept as soon as a directory is unreadable
        string odd = Path.Combine(_tempDir, "elsewhere", "odd.txt");
        Seed(odd, oldBase);
        var source = new FakeFileEnumerationSource();
        source.UnreadableDirectories.Add(P("a"));

        var (runner, rows) = RunCheck(source, CheckJob(ignoreBasePath: true));

        var missing = rows.Where(r => r.Category == CheckCategory.MissingOnDisk).Select(r => r.FilenameAndPathDB).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(gone.OrderBy(p => p, StringComparer.Ordinal).ToArray(), missing);
        Assert.Equal(3, runner.UnreadableProtectedCount);
    }

    [Fact]
    public void Scan_WithAnUnreadableDirectory_FinishesStoresTheReadableFilesAndLogsWarningAndSummary()
    {
        var tree = TestTreeGenerator.Generate(_basePath, new TestTreeOptions
        {
            FileCount = 30,
            DuplicateShare = 0.1,
            DirectoryCount = 5,
            SizeBuckets = new[] { new SizeBucket(1.0, 64, 256) },
        });
        // The real files, plus a directory that "could not be read": its files are not in the list
        var source = new FakeFileEnumerationSource();
        using (var real = new FileTreeEnumeration(_basePath))
        {
            while (real.MoveNext())
            {
                source.Files.Add(real.Current);
            }
        }
        string locked = P("locked");
        source.UnreadableDirectories.Add(locked);
        source.UnreadableFoundAtFileIndex = 3;
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log, LogLevel.Information);
        var job = new MyDupFinderScanJobDTO
        {
            JobName = "UnreadableTest",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = _basePath,
            DatabaseFile = _dbPath,
            ReportPath = _reportDir,
        };
        var runner = new ScanRunner(job, serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, null, source);

        runner.Start(CancellationToken.None);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(tree.Files.Count, ReadPaths().Count);
        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        // One warning when it was found, one in the summary at the end
        Assert.Equal(2, warnings.Count(w => w.Message.Contains(locked, StringComparison.Ordinal)));
        Assert.Contains(warnings, w => w.Message.Contains("could not be read completely", StringComparison.Ordinal) && w.Message.Contains("Directory", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.StartsWith("Scanning: 1 directories could not be read completely", StringComparison.Ordinal) && w.Message.Contains("NOT scanned", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Scanning finished", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_WithoutUnreadableDirectories_LogsNoWarning()
    {
        TestTreeGenerator.Generate(_basePath, new TestTreeOptions
        {
            FileCount = 10,
            DirectoryCount = 2,
            SizeBuckets = new[] { new SizeBucket(1.0, 64, 256) },
        });
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log, LogLevel.Information);
        var job = new MyDupFinderScanJobDTO { JobName = "UnreadableTest", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _basePath, DatabaseFile = _dbPath, ReportPath = _reportDir };
        var runner = new ScanRunner(job, serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider);

        runner.Start(CancellationToken.None);

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(10, ReadPaths().Count);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }
}
