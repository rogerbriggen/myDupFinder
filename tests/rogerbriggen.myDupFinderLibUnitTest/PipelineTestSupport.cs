// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit.Sdk;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>A scan item without the parts that differ between two runs (the Id and the scan time stamps).</summary>
internal sealed record ScanRowSnapshot(string FilenameAndPath, string Filename, long Size, long LastWriteTicks, long CreationTicks, string Hash, string PathBase, string OriginComputer, string ScanName, string ScanExecutionComputer);

/// <summary>A scan error item without the parts that differ between two runs. The kind of error is the type name of the exception.</summary>
internal sealed record ScanErrorSnapshot(string FilenameAndPath, string PathBase, long Size, string ExceptionType, string OriginComputer, string ScanName, string ScanExecutionComputer);

/// <summary>
/// Helpers for the tests of the scan pipeline: every wait on another thread has a timeout, so a deadlock fails the test instead of hanging the suite.
/// </summary>
internal static class PipelineTestSupport
{
    /// <summary>Longest a test waits for a signal or for the end of a run. A healthy run takes well under 10 seconds.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static ServiceProvider BuildServices(ILoggerProvider? loggerProvider = null, LogLevel minimumLevel = LogLevel.Information)
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

    /// <summary>Starts the runner on its own thread and returns the task. The test thread stays free to give signals.</summary>
    public static Task StartInBackground(ScanRunner runner, CancellationToken token) =>
        Task.Factory.StartNew(() => runner.Start(token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Waits for the end of <c>Start</c>. Fails (and does not hang) when it does not return in time; rethrows an exception of Start.</summary>
    public static void WaitForEnd(Task startTask, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(startTask);
        if (!startTask.Wait(timeout ?? Timeout))
        {
            throw new XunitException("ScanRunner.Start did not return in time: deadlock in the pipeline?");
        }
        startTask.GetAwaiter().GetResult();
    }

    /// <summary>Runs the scan to its end on its own thread, guarded by the timeout.</summary>
    public static void StartGuarded(ScanRunner runner, CancellationToken token, TimeSpan? timeout = null) => WaitForEnd(StartInBackground(runner, token), timeout);

    public static void Wait(ManualResetEventSlim signal, string what)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!signal.Wait(Timeout))
        {
            throw new XunitException($"Timeout waiting for: {what}");
        }
    }

    public static void Wait(CountdownEvent signal, string what)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!signal.Wait(Timeout))
        {
            throw new XunitException($"Timeout waiting for: {what}");
        }
    }

    public static void WaitUntil(Func<bool> condition, string what)
    {
        if (!SpinWait.SpinUntil(condition, Timeout))
        {
            throw new XunitException($"Timeout waiting for: {what}");
        }
    }

    public static (List<ScanRowSnapshot> Rows, List<ScanErrorSnapshot> Errors) ReadSnapshot(string databaseFile)
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(databaseFile);
        var rows = context.ScanItems!.AsNoTracking().ToList()
            .Select(s => new ScanRowSnapshot(s.FilenameAndPath, s.Filename, s.FileSize, s.FileLastModificationUTC.Ticks, s.FileCreationUTC.Ticks, s.FileSha512Hash, s.PathBase, s.OriginComputer, s.ScanName, s.ScanExecutionComputer))
            .OrderBy(r => r.FilenameAndPath, StringComparer.Ordinal)
            .ToList();
        var errors = context.ScanErrorItems!.AsNoTracking().ToList()
            .Select(e => new ScanErrorSnapshot(e.FilenameAndPath, e.PathBase, e.FileSize, ExceptionType(e.MyException), e.OriginComputer, e.ScanName, e.ScanExecutionComputer))
            .OrderBy(e => e.FilenameAndPath, StringComparer.Ordinal)
            .ToList();
        return (rows, errors);
    }

    private static string ExceptionType(string exceptionText)
    {
        int end = exceptionText.IndexOf(':', StringComparison.Ordinal);
        return end < 0 ? exceptionText : exceptionText[..end];
    }
}
