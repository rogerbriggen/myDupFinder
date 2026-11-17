// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The scan job. With <see cref="MyDupFinderScanJobDTO.HashWorkers"/> = 1 (the default) it reads and hashes one file after the other in the calling thread
/// (<see cref="Scan"/>, also throttling the hdd disk access a bit). With more workers it runs the pipeline in ScanRunner.Pipeline.cs: one producer, N hash workers and
/// one database writer connected by bounded queues. Both modes share the per-file logic (<see cref="HashItem"/> and <see cref="RecordOutcome"/>), so a file ends in
/// the same rows, logs and counters in both.
/// </summary>
internal partial class ScanRunner : BasicRunner<ScanRunner>, IScanRunner
{

    public ScanRunner(MyDupFinderScanJobDTO scanJobDTO, ILogger<ScanRunner>? logger, IServiceProvider serviceProvider) : this(scanJobDTO, logger, serviceProvider, null)
    {
    }

    /// <param name="dbInserts">Only for tests: a ready made object (for example with a small batch size). The runner disposes it.</param>
    /// <param name="fileHasher">Only for tests: a hasher with hooks or another buffer size. Default: <see cref="FileHasher.Default"/>.</param>
    /// <param name="volumeIdentityProvider">Only for tests: the identity of the volume of the base path. Default: the file system of this computer.</param>
    internal ScanRunner(MyDupFinderScanJobDTO scanJobDTO, ILogger<ScanRunner>? logger, IServiceProvider serviceProvider, ScanJobDBInserts? dbInserts, IFileEnumerationSource? fileSource = null, FileHasher? fileHasher = null, IVolumeIdentityProvider? volumeIdentityProvider = null) : base(logger, serviceProvider)
    {
        ScanJobDTO = scanJobDTO;
        _fileSource = fileSource;
        _fileHasher = fileHasher ?? FileHasher.Default;
        _volumeIdentityProvider = volumeIdentityProvider ?? SystemVolumeIdentityProvider.Instance;
        ScanJobDBInserts = dbInserts ?? new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        Throughput = new ThroughputTracker(_logger);
    }

    // To detect redundant calls
    private bool _disposed = false;
    private MyDupFinderScanJobDTO ScanJobDTO { get; set; }

    private ScanJobDBInserts ScanJobDBInserts { get; set; }

    private BlockingCollection<ScanItemDto> _scanItemCollection = new BlockingCollection<ScanItemDto>();

    private IEnumerateFilesToIndex? _enumerateFiles;

    private readonly IFileEnumerationSource? _fileSource;

    private readonly FileHasher _fileHasher;
    private readonly IVolumeIdentityProvider _volumeIdentityProvider;

    /// <summary>
    /// Files, bytes and stage times of the current run. Thread safe (the pipeline uses it from several threads).
    /// </summary>
    internal ThroughputTracker Throughput { get; private set; }

    public override void Start(CancellationToken token)
    {
        base.Start(token);

        try
        {
            //Setup DB
            ScanJobDBInserts.SetupDB(ScanJobDTO.DatabaseFile, ScanJobDTO.DatabaseSynchronous);
            _logger.LogInformation("Database state: {diagnostics}", ScanJobDBInserts.GetDatabaseDiagnostics());
            Throughput = new ThroughputTracker(_logger);
            if (!VolumeIdentityGuard.Verify(_volumeIdentityProvider, ScanJobDBInserts, ScanJobDTO.OriginComputer, ScanJobDTO.ScanName, ScanJobDTO.BasePath, ScanJobDTO.AcceptVolumeChange, readOnly: false, _logger))
            {
                // Not the volume that was scanned: nothing is enumerated and nothing is written
                RunnerState = IService.EServiceState.failed;
                return;
            }
            try
            {
                _enumerateFiles = new EnumerateFilesFromDiscToIndex(ScanJobDTO, _serviceProvider.GetService<ILogger<EnumerateFilesFromDiscToIndex>>(), _fileSource);
                if (ScanJobDTO.HashWorkers > 1)
                {
                    // Returns only when all its threads have ended
                    ScanWithPipeline();
                }
                else
                {
                    Scan();
                }
            }
            finally
            {
                (_enumerateFiles as IDisposable)?.Dispose();
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            // The database could not be set up, or something outside the file loop failed (the filter initialisation, the dispose of the enumerator)
            _logger.LogError(ex, "Scanning failed before or after the file loop.");
            FailIfStillRunning();
        }
        finally
        {
            DisposeDbInserts();
        }
    }

    /// <summary>
    /// The run ended without reaching finished or cancelled (an exception that was not handled where it happened): it is failed.
    /// </summary>
    private void FailIfStillRunning()
    {
        if (RunnerState == IService.EServiceState.running)
        {
            RunnerState = IService.EServiceState.failed;
        }
    }

    private void DisposeDbInserts()
    {
        try
        {
            ScanJobDBInserts.Dispose();
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Releasing the database failed.");
        }
    }

    private void Scan()
    {
        //No throttling when ThrottlePercent is 0
        DiscThrottle? discThrottle = DiscThrottle.CreateForJob(ScanJobDTO.ThrottlePercent);
        bool bFinish = false;
        //Don't filter if db is empty... Otherwise always look up every file. There is no base path shortcut: base paths can nest or overlap
        //(rows of C:\data\sub, now scanning C:\data), so a base path without rows of its own can still contain files that are already scanned.
        bool bFilter = !ScanJobDBInserts.IsEmptyScanItemTable();
        try
        {
            while (!bFinish)
            {
                //CancelToken?
                if (CancelToken.IsCancellationRequested)
                {
                    WriteChanges();
                    _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  {BuildCountsText()}");
                    Throughput.LogSummary("Scanning cancelled");
                    LogUnreadableSummary(complete: false);
                    RunnerState = IService.EServiceState.cancelled;
                    return;
                }

                //Throttle
                discThrottle?.Throttle(CancelToken);

                //Enumerate
                if (!(_enumerateFiles?.HasMore == true))
                {
                    bFinish = true;
                    continue;
                }
                long enumerateStart = Throughput.StartStage();
                _enumerateFiles.EnumerateFiles();
                Throughput.EndStage(ThroughputStage.Enumerate, enumerateStart);



                //CalcHash
                while (true)
                {
                    //Get item
                    ScanItemDto? si;
                    if (!_enumerateFiles.ScanItemCollection.TryDequeue(out si))
                    {
                        break;
                    }

                    //Authoritative size and timestamps from the file itself (unless TrustDirectoryMetadata), read right before the file is processed
                    long metadataStart = Throughput.StartStage();
                    bool metadataOk = _enumerateFiles.TryRefreshMetadata(si);
                    Throughput.EndStage(ThroughputStage.Enumerate, metadataStart);
                    if (!metadataOk)
                    {
                        continue;
                    }

                    //Filter (one lookup per file): a file that is already scanned is skipped, a changed one carries the rows it will replace
                    ResumeLookup? existing = null;
                    if (bFilter && ResumeSkips(si, out existing))
                    {
                        Throughput.AddFile();
                        Throughput.LogProgressIfDue();
                        continue;
                    }

                    //Calc hash
                    CalcHash(si, existing);
                    Throughput.LogProgressIfDue();

                    //CancelToken?
                    if (CancelToken.IsCancellationRequested)
                    {
                        WriteChanges();
                        _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  {BuildCountsText()}");
                        Throughput.LogSummary("Scanning cancelled");
                        LogUnreadableSummary(complete: false);
                        RunnerState = IService.EServiceState.cancelled;
                        return;
                    }

                    //Throttle
                    discThrottle?.Throttle(CancelToken);
                }
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace($"Scanning... FileEnumeration currentCount={_enumerateFiles.CurrentCount}  errorCount={_enumerateFiles.ErrorCount}  {BuildCountsText()}");
                }
            }
            WriteChanges();
            RunnerState = IService.EServiceState.finished;
            LogFinished();
            LogUnreadableSummary(complete: true);
        }
        catch (BatchWriteException ex)
        {
            LogDatabaseWriteFailure(ex);
            RunnerState = IService.EServiceState.failed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Scan!");
            RunnerState = IService.EServiceState.failed;
        }
    }

    private void LogFinished()
    {
        _logger.LogInformation($"Scanning finished... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  {BuildCountsText()}");
        Throughput.LogSummary("Scanning finished");
    }

    private void LogDatabaseWriteFailure(BatchWriteException ex)
    {
        // Fatal: the database cannot be written, so continuing to hash would be pointless. RunnerState is set to failed by the caller.
        _logger.LogError(ex, "Scanning stopped because of a database write failure. {PendingCount} operations could not be written and are lost unless the problem is fixed and the scan is started again. Files already committed stay in the database, a new scan will skip them.", ex.PendingOperationCount);
    }

    /// <summary>
    /// Directories that could not be read were already logged one by one when they were found. This repeats them in one place at the end,
    /// because their files were NOT scanned and a long log is easy to miss.
    /// </summary>
    private void LogUnreadableSummary(bool complete)
    {
        var unreadable = _enumerateFiles?.UnreadableDirectories;
        if (unreadable is null || unreadable.Count == 0)
        {
            return;
        }
        _logger.LogWarning(
            "Scanning: {count} directories could not be read completely{scope}, the files below them were NOT scanned: {directories}",
            unreadable.Count,
            complete ? string.Empty : " (so far, the scan was cancelled before the end)",
            UnreadableDirectoryLog.Join(unreadable));
    }

    private void WriteChanges()
    {
        long start = Throughput.StartStage();
        try
        {
            ScanJobDBInserts.WriteChanges();
        }
        finally
        {
            Throughput.EndStage(ThroughputStage.Database, start);
        }
    }

    /// <summary>
    /// Existing rows that were updated in place because their file changed since the previous scan (resumed scan). Counted when the update is queued.
    /// </summary>
    public int ExistingRowsUpdatedCount { get; private set; }

    /// <summary>
    /// Surplus rows (more than one row for a path, a state that older versions produced) that were removed by a resumed scan, so that one row per path remains. Counted when the removal is queued.
    /// </summary>
    public int SurplusRowsRemovedCount { get; private set; }

    /// <summary>
    /// The counters of the log lines: what reached the database (inserted or updated rows, error items) plus what the resume filter did to existing rows.
    /// SuccessCount counts every row that was written as a result of a scanned file: a new row and an updated row each count 1 (the same as an update in refresh),
    /// a removed surplus row counts nothing.
    /// </summary>
    private string BuildCountsText() =>
        $"AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount} UpdatedExistingRows={ExistingRowsUpdatedCount} RemovedSurplusRows={SurplusRowsRemovedCount}";

    /// <summary>
    /// The resume filter for one file, shared by the sequential scan and the pipeline producer (one lookup, in the thread that enumerates).
    /// Returns true when nothing is left to do for the file: it is already scanned and its path has no surplus rows.
    /// <paramref name="existing"/> is what travels with the file otherwise: null for a new file (insert), the lookup for an already scanned file with surplus rows
    /// (only the surplus rows are removed, the file is not hashed) and for a changed file (hash, update the primary row, remove the others).
    /// </summary>
    private bool ResumeSkips(ScanItemDto item, out ResumeLookup? existing)
    {
        long filterStart = Throughput.StartStage();
        ResumeLookup lookup;
        try
        {
            lookup = ScanJobDBInserts.LookupForResume(item);
        }
        finally
        {
            Throughput.EndStage(ThroughputStage.Database, filterStart);
        }
        existing = lookup.Kind == ResumeLookupKind.NotInDatabase ? null : lookup;
        return lookup.Kind == ResumeLookupKind.AlreadyScanned && lookup.SurplusIds.Count == 0;
    }

    private void CalcHash(ScanItemDto item, ResumeLookup? existing)
    {
        ScanHashOutcome outcome = HashOrSkip(item, existing, CancelToken);
        if (outcome.Kind == ScanHashOutcomeKind.Cancelled)
        {
            // Cancelled inside a big file: nothing is stored, no error item, no error count. The caller looks at the token right after this
            // method returns, writes the pending items and ends the run as cancelled, the same as for a cancel between two files.
            return;
        }
        RecordOutcome(item, existing, outcome);
    }

    /// <summary>
    /// How hashing one file ended.
    /// </summary>
    private enum ScanHashOutcomeKind
    {
        /// <summary>The hash was computed and put into the item.</summary>
        Hashed,

        /// <summary>The file could not be hashed (<see cref="ScanHashOutcome.Error"/>).</summary>
        Failed,

        /// <summary>The token was cancelled while the file was read: nothing is stored, nothing is counted.</summary>
        Cancelled,

        /// <summary>The file is already scanned (its row matches) and was not opened. Only the surplus rows of its path are removed.</summary>
        AlreadyScanned,
    }

    private readonly record struct ScanHashOutcome(ScanHashOutcomeKind Kind, long HashedBytes, Exception? Error);

    private static readonly ScanHashOutcome s_alreadyScanned = new ScanHashOutcome(ScanHashOutcomeKind.AlreadyScanned, 0, null);

    /// <summary>
    /// The first half of the per-file logic for the sequential scan and the pipeline workers: hash the file, unless it is already scanned (then it is not opened).
    /// </summary>
    private ScanHashOutcome HashOrSkip(ScanItemDto item, ResumeLookup? existing, CancellationToken token) =>
        existing?.Kind == ResumeLookupKind.AlreadyScanned ? s_alreadyScanned : HashItem(item, token);

    /// <summary>
    /// Hashes one file and puts the hash into the item (size and time stay as they were read before hashing). The first half of the per-file logic,
    /// shared by the sequential scan and the pipeline workers. It only touches the item and the hash stage time of the (thread safe) tracker, never the database
    /// or the counters, so any number of threads can call it for different items. Does not throw: a failure is the outcome.
    /// A database failure must not end up here (it is not a hashing error), which is why the database is not touched in this method.
    /// </summary>
    private ScanHashOutcome HashItem(ScanItemDto item, CancellationToken token)
    {
        try
        {
            // The hash stage covers opening, reading, hashing and closing the file
            long hashStart = Throughput.StartStage();
            FileHashResult hashResult;
            try
            {
                hashResult = _fileHasher.Compute(item.FilenameAndPath, token);
            }
            finally
            {
                Throughput.EndStage(ThroughputStage.Hash, hashStart);
            }

            item.FileSha512Hash = hashResult.Hash;
            return new ScanHashOutcome(ScanHashOutcomeKind.Hashed, hashResult.BytesRead, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new ScanHashOutcome(ScanHashOutcomeKind.Cancelled, 0, null);
        }
#pragma warning disable CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        catch (Exception e)
#pragma warning restore CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        {
            return new ScanHashOutcome(ScanHashOutcomeKind.Failed, 0, e);
        }
    }

    /// <summary>
    /// Stores what <see cref="HashItem"/> found: the scan item or the scan error item, the counters and the log lines. The second half of the per-file logic,
    /// shared by the sequential scan and the pipeline writer. Must only be called by one thread at a time (the writer): it uses the database inserts, the
    /// throughput counters. A <see cref="BatchWriteException"/> of the database is not caught here, it ends the run.
    /// </summary>
    private void RecordOutcome(ScanItemDto item, ResumeLookup? existing, ScanHashOutcome outcome)
    {
        if (outcome.Kind == ScanHashOutcomeKind.AlreadyScanned)
        {
            // Not hashed: the row of the file is current. Only rows of the same path that are surplus (an old database state) go.
            long cleanStart = Throughput.StartStage();
            try
            {
                RemoveSurplusRows(item, existing!);
            }
            finally
            {
                Throughput.EndStage(ThroughputStage.Database, cleanStart);
            }
            Throughput.AddFile();
            return;
        }
        Exception? hashError = outcome.Error;
        if (hashError is null)
        {
            long dbStart = Throughput.StartStage();
            try
            {
                if (existing is { Kind: ResumeLookupKind.Changed })
                {
                    ReplaceExistingRows(item, existing);
                }
                else
                {
                    ScanJobDBInserts.Enqueue(item);
                }
            }
            finally
            {
                Throughput.EndStage(ThroughputStage.Database, dbStart);
            }
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("File {file} successfull finished", item.FilenameAndPath);
            }
            Throughput.AddFile(outcome.HashedBytes);
        }
        else
        {
            _logger.LogError(hashError, "Hashing of {file} failed.", item.FilenameAndPath);
            long errorDbStart = Throughput.StartStage();
            try
            {
                ScanJobDBInserts.Enqueue(new ScanErrorItemDto(item, hashError, _runStarted));
            }
            finally
            {
                Throughput.EndStage(ThroughputStage.Database, errorDbStart);
            }
            Throughput.AddFile(0);
        }
    }

    /// <summary>
    /// A changed file (its path has rows, none has its size and last write time) was hashed: one row per path remains. The surplus rows are removed FIRST and the primary
    /// row is updated after them, so that a run that ends between the two (a failed flush, a crash) leaves a state that the next resumed scan repairs (the row is still
    /// stale and the file is hashed again) instead of one where the stale rows are not found any more. The update writes size, last write time, hash and both scan dates
    /// (the scan time of this item), not the creation time (the same as the update of refresh). The row that is kept is the one the lookup chose
    /// (latest LastScanDateUTC, see <see cref="PrimaryRowChooser"/>). Must be called by the thread that writes (see <see cref="RecordOutcome"/>).
    /// </summary>
    private void ReplaceExistingRows(ScanItemDto item, ResumeLookup existing)
    {
        RemoveSurplusRows(item, existing);
        // Only the key of the stub counts: the queued operation captures the new values now
        ScanJobDBInserts.UpdateItem(new ScanItemDto { Id = existing.PrimaryId }, item.FileSize, item.FileLastModificationUTC, item.FileSha512Hash, item.LastScanDateUTC);
        ExistingRowsUpdatedCount++;
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("File {file} changed since the last scan: row {id} updated, {surplus} surplus rows removed", item.FilenameAndPath, existing.PrimaryId, existing.SurplusIds.Count);
        }
    }

    private void RemoveSurplusRows(ScanItemDto item, ResumeLookup existing)
    {
        foreach (Guid surplusId in existing.SurplusIds)
        {
            ScanJobDBInserts.RemoveItem(new ScanItemDto { Id = surplusId });
            SurplusRowsRemovedCount++;
        }
        if (existing.SurplusIds.Count > 0 && _logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("File {file} had {count} surplus rows besides row {id}, they are removed", item.FilenameAndPath, existing.SurplusIds.Count, existing.PrimaryId);
        }
    }


    // Protected implementation of Dispose pattern.
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // Dispose managed state (managed objects).
            _scanItemCollection.Dispose();
        }

        // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
        // TODO: set large fields to null.
        _disposed = true;

        // Call base class implementation.
        base.Dispose(disposing);
    }


}
