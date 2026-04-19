// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Threading;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class DiscThrottleTest
{
    [Fact]
    public void WaitTest()
    {
        DiscThrottle disc = new DiscThrottle(10, TimeSpan.FromMinutes(2).TotalMilliseconds);

        DateTime start = DateTime.Now;
        disc.Throttle(null);

        while (true)
        {
            //less than 2 minutes?...
            var ts = DateTime.Now - start;
            if (ts < TimeSpan.FromSeconds(118))
            {
                DateTime throttle = DateTime.Now;
                disc.Throttle(null);
                Assert.InRange((DateTime.Now - throttle).TotalSeconds, 0, 1);
            }
            else if ((ts > TimeSpan.FromSeconds(121)) && (ts < TimeSpan.FromSeconds(130)))
            {
                DateTime throttle = DateTime.Now;
                disc.Throttle(null);
                var res = (DateTime.Now - throttle);
                Assert.InRange(res.TotalSeconds, 12, 13);
            }
            else if ((ts > TimeSpan.FromSeconds(140)) && (ts < TimeSpan.FromSeconds(145)))
            {
                DateTime throttle = DateTime.Now;
                disc.Throttle(null);
                Assert.InRange((DateTime.Now - throttle).TotalSeconds, 0, 1);
            }
            else if (ts > TimeSpan.FromSeconds(150))
            {
                break;
            }
        }
    }

    [Fact]
    public void ZeroPercentThrottle_NoWaiting()
    {
        // With 0% throttle, Throttle() should return immediately even after minimum time is elapsed
        DiscThrottle disc = new DiscThrottle(0, 0);
        disc.Throttle(null); // First call - starts the timer

        // Wait a short time to exceed the minimum work time (0ms)
        Thread.Sleep(10);

        DateTime before = DateTime.Now;
        disc.Throttle(null);
        var elapsed = DateTime.Now - before;

        // With 0% throttle there should be no waiting
        Assert.InRange(elapsed.TotalMilliseconds, 0, 500);
    }

    [Fact]
    public void ThrottlePercentCappedAt70_NoException()
    {
        // Passing a value > 70 should be capped to 70 (no exception thrown)
        DiscThrottle disc = new DiscThrottle(100, 0);
        disc.Throttle(null); // Start timer
        Assert.NotNull(disc);
    }

    [Fact]
    public void CancellationToken_CancelsThrottle()
    {
        // With a high throttle percentage and short minimum time,
        // a cancelled token should cut the wait short
        DiscThrottle disc = new DiscThrottle(70, 0);
        disc.Throttle(null); // First call - starts the timer

        // Wait enough to exceed minimum time
        Thread.Sleep(100);

        using var cts = new CancellationTokenSource();
        // Cancel immediately
        cts.Cancel();

        DateTime before = DateTime.Now;
        disc.Throttle(cts.Token);
        var elapsed = DateTime.Now - before;

        // With immediate cancellation, the throttle should stop within 2 seconds
        Assert.InRange(elapsed.TotalSeconds, 0, 2);
    }

    [Fact]
    public void FirstCall_ReturnsImmediately()
    {
        DiscThrottle disc = new DiscThrottle(50, 1000);
        DateTime before = DateTime.Now;
        disc.Throttle(null); // First call should always return immediately
        var elapsed = DateTime.Now - before;
        Assert.InRange(elapsed.TotalMilliseconds, 0, 500);
    }

    [Fact]
    public void BelowMinimumWorkTime_NoThrottling()
    {
        // With a very high minimum work time, throttle should not wait
        DiscThrottle disc = new DiscThrottle(50, TimeSpan.FromHours(1).TotalMilliseconds);
        disc.Throttle(null); // First call - start timer

        DateTime before = DateTime.Now;
        disc.Throttle(null); // Should not throttle since minimum time hasn't elapsed
        var elapsed = DateTime.Now - before;

        Assert.InRange(elapsed.TotalMilliseconds, 0, 500);
    }
}
