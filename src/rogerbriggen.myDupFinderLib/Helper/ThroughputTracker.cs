// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The stages of a run whose time is measured by the <see cref="ThroughputTracker"/>.
/// </summary>
public enum ThroughputStage
{
    /// <summary>
    /// Walking the directories, including the metadata (size, creation and last write time) of each file. The same in scan, refresh and check:
    /// the directory listing, and (unless the job trusts the directory metadata) the one call per file that reads the metadata from the file itself.
    /// </summary>
    Enumerate = 0,

    /// <summary>Opening, reading, hashing and closing the files (SHA-512).</summary>
    Hash = 1,

    /// <summary>Everything the runner does with the database: resume lookups, inserts, updates, removals and commits.</summary>
    Database = 2,
}

/// <summary>
/// Counts files and bytes of a run, measures the time spent per stage and logs a progress line
/// from time to time. There is no timer thread: the runner calls <see cref="LogProgressIfDue"/>
/// after each file and the line is only written when the interval has elapsed.
/// Thread safe: the scan pipeline (several hash workers) uses one tracker from several threads. The file and byte counters and the state of the
/// progress line are protected by one lock (an uncontended lock costs about 20 ns, negligible against the work per file), the stage times are added with
/// <see cref="Interlocked"/>. With several threads the time of a stage is the sum over all threads, so it can exceed <see cref="Elapsed"/>
/// (for example the hash time of 4 workers that were busy for 10 s each is 40 s).
/// </summary>
public sealed class ThroughputTracker
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _logInterval;
    private readonly long[] _stageTicks = new long[Enum.GetValues<ThroughputStage>().Length];
    private readonly long _startTimestamp;
    private readonly object _sync = new object();

    // Guarded by _sync (_lastLogTimestamp is also read without the lock for the cheap "not due yet" check)
    private long _files;
    private long _bytesHashed;
    private long _lastLogTimestamp;
    private long _lastLogFiles;
    private long _lastLogBytes;

    /// <param name="logger">Where the progress and summary lines are written (Information level).</param>
    /// <param name="logInterval">Minimum time between two progress lines. Default: 10 seconds.</param>
    /// <param name="timeProvider">Clock, only to be replaced in tests. Default: <see cref="TimeProvider.System"/>.</param>
    public ThroughputTracker(ILogger logger, TimeSpan? logInterval = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _logInterval = logInterval ?? TimeSpan.FromSeconds(10);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startTimestamp = _timeProvider.GetTimestamp();
        _lastLogTimestamp = _startTimestamp;
    }

    /// <summary>
    /// Number of files handled so far. This includes files that were skipped (already in the database,
    /// unchanged) and files that failed, so it is the progress through the file list.
    /// </summary>
    public long Files
    {
        get
        {
            lock (_sync)
            {
                return _files;
            }
        }
    }

    /// <summary>
    /// Sum of the sizes of the files that were actually read and hashed successfully.
    /// Skipped, unchanged and failed files add nothing here.
    /// </summary>
    public long BytesHashed
    {
        get
        {
            lock (_sync)
            {
                return _bytesHashed;
            }
        }
    }

    /// <summary>
    /// Wall clock time since the tracker was created.
    /// </summary>
    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_startTimestamp);

    /// <summary>
    /// Accumulated time of one stage. Stages are measured by the caller with <see cref="StartStage"/> and
    /// <see cref="EndStage"/>, so the sum of all stages is less than <see cref="Elapsed"/>
    /// (the rest is time that no stage claims, for example sleeping in the throttle).
    /// When several threads measure the same stage (several hash workers) the value is the sum over all of them and can exceed <see cref="Elapsed"/>.
    /// </summary>
    public TimeSpan GetStageTime(ThroughputStage stage) => TimeSpan.FromTicks(Interlocked.Read(ref _stageTicks[(int)stage]));

    /// <summary>
    /// Counts one handled file.
    /// </summary>
    /// <param name="bytesHashed">Size of the file when it was hashed, 0 when it was skipped or failed.</param>
    public void AddFile(long bytesHashed = 0)
    {
        lock (_sync)
        {
            _files++;
            _bytesHashed += bytesHashed;
        }
    }

    /// <summary>
    /// Returns a timestamp to be passed to <see cref="EndStage"/>.
    /// </summary>
    public long StartStage() => _timeProvider.GetTimestamp();

    /// <summary>
    /// Adds the time since <paramref name="startTimestamp"/> to the stage.
    /// </summary>
    public void EndStage(ThroughputStage stage, long startTimestamp) => Interlocked.Add(ref _stageTicks[(int)stage], _timeProvider.GetElapsedTime(startTimestamp).Ticks);

    /// <summary>
    /// Writes a progress line when at least the log interval has passed since the last one (or since the start).
    /// The rates in the line are calculated over the time since the last line. When several threads call it at the same moment, only one of them writes the line.
    /// </summary>
    /// <returns>true when a line was written</returns>
    public bool LogProgressIfDue()
    {
        long now = _timeProvider.GetTimestamp();
        // Cheap check without the lock, the common case
        if (_timeProvider.GetElapsedTime(Volatile.Read(ref _lastLogTimestamp), now) < _logInterval)
        {
            return false;
        }

        long files;
        long bytes;
        double seconds;
        double filesPerSecond;
        double megabytesPerSecond;
        lock (_sync)
        {
            TimeSpan sinceLast = _timeProvider.GetElapsedTime(_lastLogTimestamp, now);
            if (sinceLast < _logInterval)
            {
                // Another thread wrote the line in the meantime
                return false;
            }
            files = _files;
            bytes = _bytesHashed;
            seconds = sinceLast.TotalSeconds;
            filesPerSecond = (files - _lastLogFiles) / seconds;
            megabytesPerSecond = (bytes - _lastLogBytes) / BytesPerMegabyte / seconds;
            _lastLogFiles = files;
            _lastLogBytes = bytes;
            Volatile.Write(ref _lastLogTimestamp, now);
        }

        _logger.LogInformation(
            "Progress: {files} files, {megabytes} MB hashed in total. Last {seconds} s: {filesPerSecond} files/s, {megabytesPerSecond} MB/s",
            files,
            Format(bytes / BytesPerMegabyte),
            Format(seconds),
            Format(filesPerSecond),
            Format(megabytesPerSecond));
        return true;
    }

    /// <summary>
    /// Totals, average rates over the whole run and the time per stage as one line.
    /// MB is 1024 * 1024 bytes. The rates are totals divided by <see cref="Elapsed"/>, so they include sleeping.
    /// </summary>
    public string BuildSummary()
    {
        TimeSpan elapsed = Elapsed;
        double seconds = elapsed.TotalSeconds;
        long files;
        long bytesHashed;
        lock (_sync)
        {
            files = _files;
            bytesHashed = _bytesHashed;
        }
        double filesPerSecond = seconds > 0 ? files / seconds : 0;
        double megabytesPerSecond = seconds > 0 ? bytesHashed / BytesPerMegabyte / seconds : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} files, {1} MB hashed in {2} s. Average {3} files/s, {4} MB/s. Stage time: enumerate={5} s, hash={6} s, database={7} s",
            files,
            Format(bytesHashed / BytesPerMegabyte),
            Format(seconds),
            Format(filesPerSecond),
            Format(megabytesPerSecond),
            Format(GetStageTime(ThroughputStage.Enumerate).TotalSeconds),
            Format(GetStageTime(ThroughputStage.Hash).TotalSeconds),
            Format(GetStageTime(ThroughputStage.Database).TotalSeconds));
    }

    /// <summary>
    /// Writes <see cref="BuildSummary"/> as an Information line.
    /// </summary>
    /// <param name="title">Start of the line, for example "Scan finished" or "Scan cancelled".</param>
    public void LogSummary(string title) => _logger.LogInformation("{title}. {summary}", title, BuildSummary());

    private static string Format(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
