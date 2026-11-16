// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Threading;

namespace RogerBriggen.MyDupFinderLib;

public class DiscThrottle
{
    // Timestamp of the clock (monotonic: not affected by a change of the wall clock, for example at a daylight-saving change) when the current work period started
    private long? _startTimestamp;

    private readonly TimeProvider _timeProvider;

    private readonly Action<int> _sleep;

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
    public DiscThrottle(uint throttlePercent, double minimumWorkTimeInMs) : this(throttlePercent, minimumWorkTimeInMs, null, null)
    {
    }

    /// <param name="throttlePercent">1 - 70 Percent of throtteling</param>
    /// <param name="minimumWorkTimeInMs">No thotteling until this time in miliseconds was worked</param>
    /// <param name="timeProvider">Only for tests: the clock. Default: <see cref="TimeProvider.System"/>.</param>
    /// <param name="sleep">Only for tests: called with the milliseconds to wait (one slice of at most 1000 ms). Default: <see cref="Thread.Sleep(int)"/>. A test advances its fake clock here.</param>
    internal DiscThrottle(uint throttlePercent, double minimumWorkTimeInMs, TimeProvider? timeProvider, Action<int>? sleep)
    {
        ThrottlePercent = throttlePercent;
        MinimumWorkTimeInMs = minimumWorkTimeInMs;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sleep = sleep ?? (ms => Thread.Sleep(ms));
    }

    /// <summary>
    /// Minimum time worked before the jobs start to throttle.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumWorkTime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates the throttle for a job, or returns null when the job does not want throttling (0 percent).
    /// </summary>
    /// <param name="throttlePercent">The ThrottlePercent job setting (0 - 70)</param>
    public static DiscThrottle? CreateForJob(uint throttlePercent) => throttlePercent > 0 ? new DiscThrottle(throttlePercent, DefaultMinimumWorkTime.TotalMilliseconds) : null;

    public void Throttle(CancellationToken? cancelToken = null)
    {
        //0 percent means no throttling at all... don't even look at the clock
        if (ThrottlePercent == 0)
        {
            return;
        }

        //Not started... start it
        if (_startTimestamp is null)
        {
            _startTimestamp = _timeProvider.GetTimestamp();
            return;
        }
        //We have to wait
        TimeSpan timeSpan = _timeProvider.GetElapsedTime(_startTimestamp.Value);
        //Check if minimum is done
        if (timeSpan.TotalMilliseconds < MinimumWorkTimeInMs)
        {
            return;
        }
        //Alright, we have to wait...
        double percent = ThrottlePercent / 100.0;
        double totalWaitTime = 0;
        if (percent > 0)
        {
            totalWaitTime = timeSpan.TotalMilliseconds * percent;
        }
        //Throttle by sleeping
        while (true)
        {
            double SliceMS = 1000; //We want to be able to cancel after 1 sec.
            double waitTime = (totalWaitTime > SliceMS) ? SliceMS : totalWaitTime;

            _sleep(Convert.ToInt32(waitTime));
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
        _startTimestamp = _timeProvider.GetTimestamp();

    }

}
