// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The tracker is used from several threads by the scan pipeline: producer, hash workers and writer. Every wait has a timeout.
/// </summary>
public class ThroughputTrackerConcurrencyTest
{
    private const int Threads = 8;
    private const int CallsPerThread = 20000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>A clock that stands still at a fixed value, so that the elapsed time of EndStage is exactly what the test passes in. Safe for any number of threads (it never changes).</summary>
    private sealed class StillTimeProvider : TimeProvider
    {
        public const long Now = 1_000_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now;
    }

    /// <summary>A clock that the test moves between rounds, while the worker threads are waiting on a barrier (so reads and writes never overlap).</summary>
    private sealed class SteppedTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan time) => Volatile.Write(ref _timestamp, _timestamp + time.Ticks);
    }

    private static void RunOnThreads(int count, Action<int> body)
    {
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Factory.StartNew(() =>
        {
            start.Wait(Timeout);
            body(i);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        start.Set();
        Assert.True(Task.WaitAll(tasks, Timeout), "the threads did not finish in time");
    }

    [Fact]
    public void AddFile_FromSeveralThreads_LosesNoCount()
    {
        var log = new ListLoggerProvider();
        var tracker = new ThroughputTracker(log.CreateLogger("test"), TimeSpan.FromDays(1), new StillTimeProvider());

        RunOnThreads(Threads, thread =>
        {
            for (int i = 0; i < CallsPerThread; i++)
            {
                // Threads alternate between a hashed file of 3 bytes and a skipped one
                tracker.AddFile((i % 2 == 0) ? 3 : 0);
            }
        });

        Assert.Equal(Threads * CallsPerThread, tracker.Files);
        Assert.Equal(Threads * (CallsPerThread / 2) * 3L, tracker.BytesHashed);
    }

    [Fact]
    public void EndStage_FromSeveralThreads_AddsUpExactly_AndStagesAreIndependent()
    {
        var log = new ListLoggerProvider();
        var tracker = new ThroughputTracker(log.CreateLogger("test"), TimeSpan.FromDays(1), new StillTimeProvider());

        RunOnThreads(Threads, thread =>
        {
            for (int i = 0; i < CallsPerThread; i++)
            {
                // Each call adds exactly 10 ticks to the stage of this thread (two threads per stage, one stage unused by the others)
                ThroughputStage stage = (ThroughputStage)(thread % 3);
                tracker.EndStage(stage, StillTimeProvider.Now - 10);
            }
        });

        // Threads 0..7 -> stages 0,1,2,0,1,2,0,1: stage 0 has 3 threads, stage 1 has 3, stage 2 has 2
        Assert.Equal(TimeSpan.FromTicks(3L * CallsPerThread * 10), tracker.GetStageTime(ThroughputStage.Enumerate));
        Assert.Equal(TimeSpan.FromTicks(3L * CallsPerThread * 10), tracker.GetStageTime(ThroughputStage.Hash));
        Assert.Equal(TimeSpan.FromTicks(2L * CallsPerThread * 10), tracker.GetStageTime(ThroughputStage.Database));
    }

    [Fact]
    public void HashStageTime_WithSeveralWorkers_IsTheSumAndCanExceedTheElapsedTime()
    {
        var time = new SteppedTimeProvider();
        var log = new ListLoggerProvider();
        var tracker = new ThroughputTracker(log.CreateLogger("test"), TimeSpan.FromDays(1), time);

        // Four workers were each busy for the whole 10 seconds
        long start = tracker.StartStage();
        time.Advance(TimeSpan.FromSeconds(10));
        RunOnThreads(4, thread => tracker.EndStage(ThroughputStage.Hash, start));

        Assert.Equal(TimeSpan.FromSeconds(10), tracker.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(40), tracker.GetStageTime(ThroughputStage.Hash));
        Assert.Contains("hash=40.0 s", tracker.BuildSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogProgressIfDue_FromSeveralThreadsAtTheSameMoment_WritesOneLinePerInterval()
    {
        var time = new SteppedTimeProvider();
        var log = new ListLoggerProvider();
        var tracker = new ThroughputTracker(log.CreateLogger("test"), TimeSpan.FromSeconds(10), time);
        const int rounds = 50;
        int written = 0;

        for (int round = 0; round < rounds; round++)
        {
            tracker.AddFile(100);
            // All threads are at the gate and the clock moves while nobody reads it, then they all ask at once
            time.Advance(TimeSpan.FromSeconds(11));
            using var barrier = new Barrier(Threads);
            RunOnThreads(Threads, thread =>
            {
                barrier.SignalAndWait(Timeout);
                if (tracker.LogProgressIfDue())
                {
                    Interlocked.Increment(ref written);
                }
            });
        }

        Assert.Equal(rounds, written);
        Assert.Equal(rounds, log.Entries.Count);
    }

    [Fact]
    public void MixedUse_FromSeveralThreads_KeepsTheCountsExact_AndNeverThrows()
    {
        var log = new ListLoggerProvider();
        // A real clock and a zero interval: every call of LogProgressIfDue may write a line, while the other threads count
        var tracker = new ThroughputTracker(log.CreateLogger("test"), TimeSpan.Zero);

        RunOnThreads(Threads, thread =>
        {
            for (int i = 0; i < 5000; i++)
            {
                long start = tracker.StartStage();
                tracker.AddFile(2);
                tracker.EndStage((ThroughputStage)(i % 3), start);
                tracker.LogProgressIfDue();
                _ = tracker.Files;
                _ = tracker.BuildSummary();
            }
        });

        Assert.Equal(Threads * 5000, tracker.Files);
        Assert.Equal(Threads * 5000 * 2L, tracker.BytesHashed);
    }
}
