// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Threading;

namespace RogerBriggen.MyDupFinderLib;

public class DiscThrottle
{
    private DateTime? StartTime { get; set; }

    private double MinimumWorkTimeInMs { get; set; }


    private uint _throttlePercent = 0;
    private uint ThrottlePercent
    {
        get => _throttlePercent;
        set => SetThrottlePercent(value);
    }

    private void SetThrottlePercent(uint value)
    {
        if (value > 70)
        {
            _throttlePercent = 70;
        }
        else
        {
            _throttlePercent = value;
        }
    }

    /// <summary>
    /// Call it everytime you think it could be good to throttle
    /// </summary>
    /// <param name="throttlePercent">1 - 70 Percent of throtteling</param>
    /// <param name="MinimumWorkTimeInMs">No thotteling until this time in miliseconds was worked</param>
    public DiscThrottle(uint throttlePercent, double minimumWorkTimeInMs)
    {
        ThrottlePercent = throttlePercent;
        MinimumWorkTimeInMs = minimumWorkTimeInMs;
    }

    public void Throttle(CancellationToken? cancelToken = null)
    {
        //Not started... start it
        if (StartTime is null)
        {
            StartTime = DateTime.Now;
            return;
        }
        //We have to wait
        var timeSpan = DateTime.Now - StartTime;
        if (timeSpan is null)
        {
            return;
        }
        //Check if minimum is done
        if (timeSpan.Value.TotalMilliseconds < MinimumWorkTimeInMs)
        {
            return;
        }
        //Alright, we have to wait...
        double percent = ThrottlePercent / 100.0;
        double totalWaitTime = 0;
        if (percent > 0)
        {
            totalWaitTime = timeSpan.Value.TotalMilliseconds * percent;
        }
        //Throttle by sleeping
        while (true)
        {
            double SliceMS = 1000; //We want to be able to cancel after 1 sec.
            double waitTime = (totalWaitTime > SliceMS) ? SliceMS : totalWaitTime;

            System.Threading.Thread.Sleep(Convert.ToInt32(waitTime));
            totalWaitTime -= waitTime;
            if (totalWaitTime <= 1)
            {
                //We are done
                break;
            }
            if (cancelToken?.IsCancellationRequested == true)
            {
                //We are done
                break;
            }
        }

        //Restart Time
        StartTime = DateTime.Now;

    }

}
