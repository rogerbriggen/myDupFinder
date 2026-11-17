// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Every job ends in exactly one of finished, cancelled and failed, and nothing is thrown out of the service: a database that cannot be opened, a report that cannot be created,
/// a runner that throws. Also the chain that runs the jobs of a project (<see cref="JobChain"/>).
/// </summary>
public class JobOutcomeTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _treeDir;
    private readonly string _dbPath;

    public JobOutcomeTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "JobOutcomeTest_" + Guid.NewGuid().ToString("N"));
        _treeDir = Path.Combine(_tempDir, "tree");
        _dbPath = Path.Combine(_tempDir, "db", "scan.db");
        Directory.CreateDirectory(_treeDir);
        File.WriteAllText(Path.Combine(_treeDir, "a.txt"), "content of a");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static ServiceProvider BuildServices(ILoggerProvider log)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(log);
        });
        return services.BuildServiceProvider();
    }

    private MyDupFinderScanJobDTO ScanJob(string databaseFile, int hashWorkers = 1, string? reportPath = null) => new MyDupFinderScanJobDTO
    {
        JobName = "JobOutcomeTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _treeDir,
        DatabaseFile = databaseFile,
        ReportPath = reportPath ?? _tempDir,
        HashWorkers = hashWorkers,
    };

    /// <summary>A database path that cannot be opened: a directory, or a file that is no database.</summary>
    private string UnopenableDatabase(bool directory)
    {
        string path = Path.Combine(_tempDir, directory ? "dbdir" : "garbage.db");
        if (directory)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllText(path, new string('x', 5000));
        }
        return path;
    }

    // ----- database cannot be opened -----

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 4)]
    public void ScanService_DatabaseCannotBeOpened_EndsFailed_AndThrowsNothing(bool directory, int workers)
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        using var service = new ScanService(serviceProvider.GetRequiredService<ILogger<ScanService>>(), serviceProvider);

        service.StartScan(ScanJob(UnopenableDatabase(directory), workers));

        Assert.Equal(IService.EServiceState.failed, service.ServiceState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefreshService_DatabaseCannotBeOpened_EndsFailed_AndThrowsNothing(bool directory)
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        using var service = new RefreshService(serviceProvider.GetRequiredService<ILogger<RefreshService>>(), serviceProvider);
        var job = new MyDupFinderRefreshJobDTO { JobName = "JobOutcomeTest", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _treeDir, DatabaseFile = UnopenableDatabase(directory), ReportPath = _tempDir };

        service.StartRefresh(job);

        Assert.Equal(IService.EServiceState.failed, service.ServiceState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CheckService_DatabaseCannotBeOpened_EndsFailed_AndThrowsNothing(bool directory)
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        using var service = new CheckService(serviceProvider.GetRequiredService<ILogger<CheckService>>(), serviceProvider);

        service.StartCheck(new MyDupFinderCheckJobDTO { ScanJobDTO = ScanJob(UnopenableDatabase(directory)) });

        Assert.Equal(IService.EServiceState.failed, service.ServiceState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void ScanRunner_DatabaseCannotBeOpened_RunnerStateIsFailed()
    {
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        var runner = new ScanRunner(ScanJob(UnopenableDatabase(directory: true)), serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider);

        runner.Start(CancellationToken.None);

        Assert.Equal(IService.EServiceState.failed, runner.RunnerState);
    }

    // ----- check: report cannot be created -----

    [Fact]
    public void Check_ReportCannotBeCreated_EndsFailed_AndReleasesTheDatabase()
    {
        // The database is fine (empty), the report folder is a file
        using (var prepare = new ScanJobDBInserts(null))
        {
            prepare.SetupDB(_dbPath);
        }
        string reportPathIsAFile = Path.Combine(_tempDir, "report-folder-is-a-file");
        File.WriteAllText(reportPathIsAFile, "x");
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        using var service = new CheckService(serviceProvider.GetRequiredService<ILogger<CheckService>>(), serviceProvider);

        service.StartCheck(new MyDupFinderCheckJobDTO { ScanJobDTO = ScanJob(_dbPath, reportPath: reportPathIsAFile) });

        Assert.Equal(IService.EServiceState.failed, service.ServiceState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Check failed", StringComparison.Ordinal));
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath); // would fail if the runner still held the database
        Assert.False(File.Exists(_dbPath));
    }

    // ----- refresh: rows that belong to another job -----

    [Fact]
    public void Refresh_RowsOfAnotherIdentityOnly_EndsFailed_AndChangesNothing()
    {
        string filePath = Path.Combine(_treeDir, "a.txt");
        using (var prepare = new ScanJobDBInserts(null))
        {
            prepare.SetupDB(_dbPath);
            prepare.Enqueue(new ScanItemDto
            {
                Id = Guid.NewGuid(),
                FilenameAndPath = filePath,
                PathBase = _treeDir,
                ScanExecutionComputer = "OtherPC",
                OriginComputer = "OtherPC",
                ScanName = "OtherScan",
                FileSize = 1,
                FileSha512Hash = "H",
                FileCreationUTC = DateTime.UtcNow,
                FileLastModificationUTC = DateTime.UtcNow,
                FirstScanDateUTC = DateTime.UtcNow,
                LastScanDateUTC = DateTime.UtcNow,
                LastSha512ScanDateUTC = DateTime.UtcNow,
            });
            prepare.WriteChanges();
        }
        var log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        using var service = new RefreshService(serviceProvider.GetRequiredService<ILogger<RefreshService>>(), serviceProvider);
        var job = new MyDupFinderRefreshJobDTO { JobName = "JobOutcomeTest", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _treeDir, DatabaseFile = _dbPath, ReportPath = _tempDir };

        service.StartRefresh(job);

        Assert.Equal(IService.EServiceState.failed, service.ServiceState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("none belongs to this job", StringComparison.Ordinal));
    }

    // ----- the safety net of BasicService -----

    private sealed class TestService : BasicService<TestService>
    {
        public TestService(ILogger<TestService> logger, IServiceProvider serviceProvider)
            : base(logger, serviceProvider)
        {
        }
    }

    private sealed class FakeRunner : IRunner
    {
        private readonly Action<FakeRunner> _start;
        private IService.EServiceState _state = IService.EServiceState.idle;

        public FakeRunner(Action<FakeRunner> start) => _start = start;

        public event EventHandler<int>? RunnerProgressChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<IService.EServiceState>? RunnerStateChanged;

        public IService.EServiceState RunnerState
        {
            get => _state;
            set
            {
                _state = value;
                RunnerStateChanged?.Invoke(this, value);
            }
        }

        public void Dispose()
        {
        }

        public void Start(CancellationToken token)
        {
            RunnerState = IService.EServiceState.running;
            _start(this);
        }
    }

    private static TestService NewTestService(ListLoggerProvider log, out ServiceProvider serviceProvider)
    {
        serviceProvider = BuildServices(log);
        return new TestService(serviceProvider.GetRequiredService<ILogger<TestService>>(), serviceProvider);
    }

    [Fact]
    public void BasicService_RunnerThrows_IsLogged_ServiceFailed_NothingEscapes()
    {
        var log = new ListLoggerProvider();
        using var service = NewTestService(log, out var serviceProvider);
        using (serviceProvider)
        {
            service.Start(new FakeRunner(_ => throw new InvalidOperationException("boom")));
        }

        Assert.Equal(IService.EServiceState.failed, service.ServiceState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("unexpected exception", StringComparison.Ordinal));
    }

    [Fact]
    public void BasicService_RunnerReturnsStillRunning_ServiceFailed()
    {
        var log = new ListLoggerProvider();
        using var service = NewTestService(log, out var serviceProvider);
        using (serviceProvider)
        {
            service.Start(new FakeRunner(_ => { }));
        }

        Assert.Equal(IService.EServiceState.failed, service.ServiceState);
    }

    [Theory]
    [InlineData(IService.EServiceState.finished)]
    [InlineData(IService.EServiceState.cancelled)]
    [InlineData(IService.EServiceState.failed)]
    public void BasicService_ServiceStateIsTheFinalRunnerState(IService.EServiceState final)
    {
        var log = new ListLoggerProvider();
        using var service = NewTestService(log, out var serviceProvider);
        using (serviceProvider)
        {
            service.Start(new FakeRunner(r => r.RunnerState = final));
        }

        Assert.Equal(final, service.ServiceState);
    }

    [Fact]
    public void BasicService_SubscribersThatThrow_NothingEscapes_AndTheStateStillFollowsTheRunner()
    {
        var log = new ListLoggerProvider();
        using var service = NewTestService(log, out var serviceProvider);
        using (serviceProvider)
        {
            service.ServiceStateChanged += (_, _) => throw new InvalidOperationException("subscriber of the service");
            // The service's own handler of the runner events raises ServiceStateChanged from inside the runner: that must not disturb the runner
            service.Start(new FakeRunner(r => r.RunnerState = IService.EServiceState.finished));
        }

        Assert.Equal(IService.EServiceState.finished, service.ServiceState);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("ServiceStateChanged", StringComparison.Ordinal));
    }

    [Fact]
    public void BasicService_RunnerThrowsAfterItsFinalState_KeepsThatState()
    {
        var log = new ListLoggerProvider();
        using var service = NewTestService(log, out var serviceProvider);
        using (serviceProvider)
        {
            service.Start(new FakeRunner(r =>
            {
                r.RunnerState = IService.EServiceState.cancelled;
                throw new InvalidOperationException("late");
            }));
        }

        Assert.Equal(IService.EServiceState.cancelled, service.ServiceState);
    }

    // ----- the chain of the jobs of a project -----

    private static ChainedJob Job(string name, IService.EServiceState result, List<string> ran) =>
        new ChainedJob(name, () =>
        {
            ran.Add(name);
            return result;
        });

    private static JobChainResult RunChain(IReadOnlyList<ChainedJob> jobs, out ListLoggerProvider log)
    {
        log = new ListLoggerProvider();
        using var serviceProvider = BuildServices(log);
        return JobChain.Run(jobs, serviceProvider.GetRequiredService<ILogger<JobOutcomeTest>>());
    }

    [Fact]
    public void JobChain_AllFinished_RunsEverythingInOrder_ExitCodeZero()
    {
        var ran = new List<string>();

        var result = RunChain(new[] { Job("a", IService.EServiceState.finished, ran), Job("b", IService.EServiceState.finished, ran), Job("c", IService.EServiceState.finished, ran) }, out var log);

        Assert.Equal(new[] { "a", "b", "c" }, ran);
        Assert.Equal(JobChain.ExitCodeFinished, result.ExitCode);
        Assert.Equal(0, JobChain.ExitCodeFinished);
        Assert.Equal(3, result.JobsStarted);
        Assert.Equal(0, result.JobsNotRun);
        Assert.Null(result.StoppedByJob);
        Assert.Contains(log.Entries, e => e.Message == "Job b finished!");
        Assert.Contains(log.Entries, e => e.Message == "All Jobs finished");
    }

    [Fact]
    public void JobChain_FailedJob_StopsTheRest_ExitCodeOne_AndLogsWhichJobAndHowManyWereNotRun()
    {
        var ran = new List<string>();

        var result = RunChain(new[] { Job("a", IService.EServiceState.finished, ran), Job("b", IService.EServiceState.failed, ran), Job("c", IService.EServiceState.finished, ran), Job("d", IService.EServiceState.finished, ran) }, out var log);

        Assert.Equal(new[] { "a", "b" }, ran);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, result.JobsStarted);
        Assert.Equal(2, result.JobsNotRun);
        Assert.Equal("b", result.StoppedByJob);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message == "Job b failed!");
        Assert.Contains(log.Entries, e => e.Message.Contains("stopped by job b", StringComparison.Ordinal) && e.Message.Contains("2 of 4 jobs were not run", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message == "All Jobs finished");
    }

    [Fact]
    public void JobChain_CancelledJob_StopsTheRest_ExitCodeTwo()
    {
        var ran = new List<string>();

        var result = RunChain(new[] { Job("a", IService.EServiceState.cancelled, ran), Job("b", IService.EServiceState.finished, ran) }, out var log);

        Assert.Equal(new[] { "a" }, ran);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(1, result.JobsNotRun);
        Assert.Equal("a", result.StoppedByJob);
        Assert.Contains(log.Entries, e => e.Message == "Job a cancelled!");
    }

    [Theory]
    [InlineData(IService.EServiceState.running)]
    [InlineData(IService.EServiceState.idle)]
    public void JobChain_AJobThatEndsInAnyOtherStateCountsAsFailed(IService.EServiceState state)
    {
        var ran = new List<string>();

        var result = RunChain(new[] { Job("a", state, ran), Job("b", IService.EServiceState.finished, ran) }, out _);

        Assert.Equal(new[] { "a" }, ran);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void JobChain_AJobThatThrows_CountsAsFailed_AndStopsTheRest()
    {
        var ran = new List<string>();
        var jobs = new[]
        {
            new ChainedJob("a", () => throw new InvalidOperationException("boom")),
            Job("b", IService.EServiceState.finished, ran),
        };

        var result = RunChain(jobs, out var log);

        Assert.Empty(ran);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("a", result.StoppedByJob);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("ended with an exception", StringComparison.Ordinal));
    }

    [Fact]
    public void JobChain_NoJobs_IsFinished()
    {
        var result = RunChain(Array.Empty<ChainedJob>(), out _);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.JobsStarted);
    }
}
