// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The stages of a run whose time is measured by the <see cref="ThroughputTracker"/>.
/// </summary>
public enum ThroughputStage
{
    /// <summary>Walking the file system and reading file attributes.</summary>
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
/// Not thread safe, use it from the thread that runs the job.
/// </summary>
public sealed class ThroughputTracker
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _logInterval;
    private readonly TimeSpan[] _stageTimes = new TimeSpan[Enum.GetValues<ThroughputStage>().Length];
    private readonly long _startTimestamp;

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
    public long Files { get; private set; }

    /// <summary>
    /// Sum of the sizes of the files that were actually read and hashed successfully.
    /// Skipped, unchanged and failed files add nothing here.
    /// </summary>
    public long BytesHashed { get; private set; }

    /// <summary>
    /// Wall clock time since the tracker was created.
    /// </summary>
    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_startTimestamp);

    /// <summary>
    /// Accumulated time of one stage. Stages are measured by the caller with <see cref="StartStage"/> and
    /// <see cref="EndStage"/>, so the sum of all stages is less than <see cref="Elapsed"/>
    /// (the rest is time that no stage claims, for example sleeping in the throttle).
    /// </summary>
    public TimeSpan GetStageTime(ThroughputStage stage) => _stageTimes[(int)stage];

    /// <summary>
    /// Counts one handled file.
    /// </summary>
    /// <param name="bytesHashed">Size of the file when it was hashed, 0 when it was skipped or failed.</param>
    public void AddFile(long bytesHashed = 0)
    {
        Files++;
        BytesHashed += bytesHashed;
    }

    /// <summary>
    /// Returns a timestamp to be passed to <see cref="EndStage"/>.
    /// </summary>
    public long StartStage() => _timeProvider.GetTimestamp();

    /// <summary>
    /// Adds the time since <paramref name="startTimestamp"/> to the stage.
    /// </summary>
    public void EndStage(ThroughputStage stage, long startTimestamp) => _stageTimes[(int)stage] += _timeProvider.GetElapsedTime(startTimestamp);

    /// <summary>
    /// Writes a progress line when at least the log interval has passed since the last one (or since the start).
    /// The rates in the line are calculated over the time since the last line.
    /// </summary>
    /// <returns>true when a line was written</returns>
    public bool LogProgressIfDue()
    {
        long now = _timeProvider.GetTimestamp();
        TimeSpan sinceLast = _timeProvider.GetElapsedTime(_lastLogTimestamp, now);
        if (sinceLast < _logInterval)
        {
            return false;
        }

        double seconds = sinceLast.TotalSeconds;
        double filesPerSecond = (Files - _lastLogFiles) / seconds;
        double megabytesPerSecond = (BytesHashed - _lastLogBytes) / BytesPerMegabyte / seconds;
        _logger.LogInformation(
            "Progress: {files} files, {megabytes} MB hashed in total. Last {seconds} s: {filesPerSecond} files/s, {megabytesPerSecond} MB/s",
            Files,
            Format(BytesHashed / BytesPerMegabyte),
            Format(seconds),
            Format(filesPerSecond),
            Format(megabytesPerSecond));

        _lastLogTimestamp = now;
        _lastLogFiles = Files;
        _lastLogBytes = BytesHashed;
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
        double filesPerSecond = seconds > 0 ? Files / seconds : 0;
        double megabytesPerSecond = seconds > 0 ? BytesHashed / BytesPerMegabyte / seconds : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} files, {1} MB hashed in {2} s. Average {3} files/s, {4} MB/s. Stage time: enumerate={5} s, hash={6} s, database={7} s",
            Files,
            Format(BytesHashed / BytesPerMegabyte),
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
