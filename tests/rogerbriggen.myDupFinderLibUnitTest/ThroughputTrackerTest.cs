// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class ThroughputTrackerTest
{
    private const long MiB = 1024 * 1024;

    private readonly FakeTimeProvider _time = new FakeTimeProvider();
    private readonly ListLoggerProvider _log = new ListLoggerProvider();

    private ThroughputTracker CreateTracker(int intervalSeconds = 10) => new ThroughputTracker(_log.CreateLogger("test"), TimeSpan.FromSeconds(intervalSeconds), _time);

    [Fact]
    public void AddFile_CountsFilesAndHashedBytes()
    {
        var tracker = CreateTracker();

        tracker.AddFile(1000);
        tracker.AddFile(); // skipped file: counts as a file, no bytes
        tracker.AddFile(500);

        Assert.Equal(3, tracker.Files);
        Assert.Equal(1500, tracker.BytesHashed);
    }

    [Fact]
    public void LogProgressIfDue_BeforeInterval_WritesNothing()
    {
        var tracker = CreateTracker();
        tracker.AddFile(MiB);

        _time.Advance(TimeSpan.FromSeconds(9.9));

        Assert.False(tracker.LogProgressIfDue());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void LogProgressIfDue_AfterInterval_WritesOneInformationLineWithRates()
    {
        var tracker = CreateTracker();
        for (int i = 0; i < 100; i++)
        {
            tracker.AddFile(MiB / 2); // 100 files, 50 MB
        }

        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(tracker.LogProgressIfDue());
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Progress: 100 files, 50.0 MB hashed in total. Last 10.0 s: 10.0 files/s, 5.0 MB/s", entry.Message);
    }

    [Fact]
    public void LogProgressIfDue_RightAfterALine_WritesNothingUntilNextInterval()
    {
        var tracker = CreateTracker();
        tracker.AddFile(MiB);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(tracker.LogProgressIfDue());

        // Called after every file: must stay quiet
        tracker.AddFile(MiB);
        Assert.False(tracker.LogProgressIfDue());
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(tracker.LogProgressIfDue());

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(tracker.LogProgressIfDue());
        Assert.Equal(2, _log.Entries.Count);
    }

    [Fact]
    public void LogProgressIfDue_SecondLine_UsesOnlyTheTimeAndCountsSinceTheLastLine()
    {
        var tracker = CreateTracker();
        for (int i = 0; i < 100; i++)
        {
            tracker.AddFile(MiB / 2);
        }
        _time.Advance(TimeSpan.FromSeconds(10));
        tracker.LogProgressIfDue();

        // The next window is longer than the interval: 30 files and 20 MB in 20 s
        for (int i = 0; i < 30; i++)
        {
            tracker.AddFile(i < 20 ? MiB : 0);
        }
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(tracker.LogProgressIfDue());

        Assert.Equal("Progress: 130 files, 70.0 MB hashed in total. Last 20.0 s: 1.5 files/s, 1.0 MB/s", _log.Entries.Last().Message);
    }

    [Fact]
    public void StageTimes_AreAccumulatedPerStage()
    {
        var tracker = CreateTracker();

        long t = tracker.StartStage();
        _time.Advance(TimeSpan.FromSeconds(2));
        tracker.EndStage(ThroughputStage.Hash, t);

        t = tracker.StartStage();
        _time.Advance(TimeSpan.FromSeconds(1));
        tracker.EndStage(ThroughputStage.Database, t);

        t = tracker.StartStage();
        _time.Advance(TimeSpan.FromSeconds(3));
        tracker.EndStage(ThroughputStage.Hash, t);

        Assert.Equal(TimeSpan.Zero, tracker.GetStageTime(ThroughputStage.Enumerate));
        Assert.Equal(TimeSpan.FromSeconds(5), tracker.GetStageTime(ThroughputStage.Hash));
        Assert.Equal(TimeSpan.FromSeconds(1), tracker.GetStageTime(ThroughputStage.Database));
    }

    [Fact]
    public void BuildSummary_ContainsTotalsAverageRatesAndStageTimes()
    {
        var tracker = CreateTracker();
        for (int i = 0; i < 200; i++)
        {
            tracker.AddFile(MiB / 2); // 200 files, 100 MB
        }
        AddStageTime(tracker, ThroughputStage.Enumerate, 2);
        AddStageTime(tracker, ThroughputStage.Hash, 8);
        AddStageTime(tracker, ThroughputStage.Database, 4);
        _time.Advance(TimeSpan.FromSeconds(6)); // 20 s in total, 6 s of it in no stage (for example sleeping)

        Assert.Equal(TimeSpan.FromSeconds(20), tracker.Elapsed);
        Assert.Equal("200 files, 100.0 MB hashed in 20.0 s. Average 10.0 files/s, 5.0 MB/s. Stage time: enumerate=2.0 s, hash=8.0 s, database=4.0 s", tracker.BuildSummary());
    }

    [Fact]
    public void LogSummary_WritesOneInformationLineWithTitle()
    {
        var tracker = CreateTracker();
        tracker.AddFile(MiB);
        _time.Advance(TimeSpan.FromSeconds(1));

        tracker.LogSummary("Scanning finished");

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith("Scanning finished. 1 files, 1.0 MB hashed in 1.0 s.", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSummary_WithoutElapsedTime_DoesNotDivideByZero()
    {
        var tracker = CreateTracker();
        tracker.AddFile(MiB);

        Assert.Contains("Average 0.0 files/s, 0.0 MB/s", tracker.BuildSummary(), StringComparison.Ordinal);
    }

    private void AddStageTime(ThroughputTracker tracker, ThroughputStage stage, int seconds)
    {
        long t = tracker.StartStage();
        _time.Advance(TimeSpan.FromSeconds(seconds));
        tracker.EndStage(stage, t);
    }
}
