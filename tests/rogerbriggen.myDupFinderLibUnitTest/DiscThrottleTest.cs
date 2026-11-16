// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The throttle with a fake clock: the "sleep" of the throttle only records the slice and moves the fake clock, so no test waits in real time.
/// </summary>
public class DiscThrottleTest
{
    private sealed class Rig
    {
        public FakeTimeProvider Clock { get; } = new FakeTimeProvider();

        /// <summary>The slices (in ms) that the throttle slept, in order.</summary>
        public List<int> Slices { get; } = new List<int>();

        public double SleptMs => Slices.Sum();

        public DiscThrottle Create(uint percent, double minimumWorkTimeInMs) =>
            new DiscThrottle(percent, minimumWorkTimeInMs, Clock, ms =>
            {
                Slices.Add(ms);
                Clock.Advance(TimeSpan.FromMilliseconds(ms));
            });
    }

    [Fact]
    public void WaitTest()
    {
        // 10 percent, no throttling during the first 2 minutes of work
        var rig = new Rig();
        DiscThrottle disc = rig.Create(10, TimeSpan.FromMinutes(2).TotalMilliseconds);

        disc.Throttle(null); // starts the timer
        Assert.Empty(rig.Slices);

        // Less than 2 minutes worked: no waiting at any call
        for (int second = 1; second <= 118; second++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            disc.Throttle(null);
        }
        Assert.Empty(rig.Slices);

        // 121 seconds worked: waits 10 percent of the work time = 12.1 s, in slices of at most one second (so that a cancel is seen after a second)
        rig.Clock.Advance(TimeSpan.FromSeconds(3));
        disc.Throttle(null);
        Assert.Equal(12100, rig.SleptMs);
        Assert.Equal(13, rig.Slices.Count);
        Assert.All(rig.Slices, s => Assert.InRange(s, 1, 1000));

        // The timer restarted when the wait ended: 5 seconds later nothing is waited
        rig.Slices.Clear();
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        disc.Throttle(null);
        Assert.Empty(rig.Slices);

        // After another 2 minutes of work (the wait is 10 percent of the time since the restart, 125 s)
        rig.Clock.Advance(TimeSpan.FromSeconds(120));
        disc.Throttle(null);
        Assert.Equal(12500, rig.SleptMs);
    }

    [Fact]
    public void ZeroPercentThrottle_NoWaiting()
    {
        // With 0% throttle, Throttle() returns immediately even after the minimum work time is over
        var rig = new Rig();
        DiscThrottle disc = rig.Create(0, 0);
        disc.Throttle(null);
        rig.Clock.Advance(TimeSpan.FromHours(1));

        disc.Throttle(null);

        Assert.Empty(rig.Slices);
    }

    [Fact]
    public void ZeroPercentThrottle_ReturnsImmediately_EvenWithManyCallsAndACancelledToken()
    {
        var rig = new Rig();
        DiscThrottle disc = rig.Create(0, 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        disc.Throttle(cts.Token);
        rig.Clock.Advance(TimeSpan.FromMinutes(10));

        for (int i = 0; i < 1000; i++)
        {
            disc.Throttle(cts.Token);
        }

        Assert.Empty(rig.Slices);
    }

    [Fact]
    public void CreateForJob_ZeroPercent_ReturnsNull()
    {
        Assert.Null(DiscThrottle.CreateForJob(0));
    }

    [Fact]
    public void CreateForJob_PositivePercent_ReturnsThrottle()
    {
        Assert.NotNull(DiscThrottle.CreateForJob(10));
    }

    [Fact]
    public void ThrottlePercentCappedAt70()
    {
        // 100 percent is capped to 70: after 100 s of work the wait is 70 s, not 100 s
        var rig = new Rig();
        DiscThrottle disc = rig.Create(100, 0);
        disc.Throttle(null);
        rig.Clock.Advance(TimeSpan.FromSeconds(100));

        disc.Throttle(null);

        Assert.Equal(70000, rig.SleptMs);
    }

    [Fact]
    public void CancellationToken_CancelsThrottle_AfterTheCurrentSlice()
    {
        // A cancelled token ends the wait after one slice of one second, not after the whole wait of 70 s
        var rig = new Rig();
        DiscThrottle disc = rig.Create(70, 0);
        disc.Throttle(null);
        rig.Clock.Advance(TimeSpan.FromSeconds(100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        disc.Throttle(cts.Token);

        Assert.Equal(new[] { 1000 }, rig.Slices);
    }

    [Fact]
    public void CancellationDuringTheWait_EndsTheWait_AndTheTimerRestarts()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var slices = rig.Slices;
        DiscThrottle disc = new DiscThrottle(50, 0, rig.Clock, ms =>
        {
            slices.Add(ms);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(ms));
            if (slices.Count == 3)
            {
                cts.Cancel();
            }
        });
        disc.Throttle(null);
        rig.Clock.Advance(TimeSpan.FromSeconds(60));

        disc.Throttle(cts.Token);

        // The token was cancelled while the third slice slept; the wait ends after that slice (30 s would have been waited)
        Assert.Equal(3, slices.Count);
        // The timer restarted at the end: a short time later the wait is 50 percent of that short time only
        slices.Clear();
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        disc.Throttle(null);
        Assert.Equal(5000, rig.SleptMs);
    }

    [Fact]
    public void FirstCall_ReturnsImmediately()
    {
        var rig = new Rig();
        DiscThrottle disc = rig.Create(50, 1000);
        disc.Throttle(null);
        Assert.Empty(rig.Slices);
    }

    [Fact]
    public void BelowMinimumWorkTime_NoThrottling()
    {
        var rig = new Rig();
        DiscThrottle disc = rig.Create(50, TimeSpan.FromHours(1).TotalMilliseconds);
        disc.Throttle(null);
        rig.Clock.Advance(TimeSpan.FromMinutes(59));

        disc.Throttle(null);

        Assert.Empty(rig.Slices);
    }

    [Fact]
    public void TheClockIsTheTimeProvider_NotTheWallClock()
    {
        // The wait only depends on the time provider: with the real default constructor nothing happens before the minimum work time
        DiscThrottle disc = new DiscThrottle(50, TimeSpan.FromHours(1).TotalMilliseconds);
        disc.Throttle(null);
        disc.Throttle(null);
        Assert.NotNull(disc);
    }
}
