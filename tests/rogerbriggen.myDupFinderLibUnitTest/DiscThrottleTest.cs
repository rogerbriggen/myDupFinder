// Roger Briggen license this file to you under the MIT license.
//

using System;
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
}
