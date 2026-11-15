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
    internal ScanRunner(MyDupFinderScanJobDTO scanJobDTO, ILogger<ScanRunner>? logger, IServiceProvider serviceProvider, ScanJobDBInserts? dbInserts, IFileEnumerationSource? fileSource = null, FileHasher? fileHasher = null) : base(logger, serviceProvider)
    {
        ScanJobDTO = scanJobDTO;
        _fileSource = fileSource;
        _fileHasher = fileHasher ?? FileHasher.Default;
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

    /// <summary>
    /// Files, bytes and stage times of the current run. Thread safe (the pipeline uses it from several threads).
    /// </summary>
    internal ThroughputTracker Throughput { get; private set; }

    /// <summary>
    /// Files that changed while they were read, twice in a row. They get no scan item; a scan error item records them (it is also counted in the error count of the database writer).
    /// </summary>
    internal int UnstableCount { get; private set; }

    public override void Start(CancellationToken token)
    {
        base.Start(token);

        //Setup DB
        ScanJobDBInserts.SetupDB(ScanJobDTO.DatabaseFile);
        _logger.LogInformation("Database state: {diagnostics}", ScanJobDBInserts.GetDatabaseDiagnostics());
        Throughput = new ThroughputTracker(_logger);
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
        ScanJobDBInserts.Dispose();
    }

    private void Scan()
    {
        //No throttling when ThrottlePercent is 0
        DiscThrottle? discThrottle = DiscThrottle.CreateForJob(ScanJobDTO.ThrottlePercent);
        bool bFinish = false;
        //Don't filter if db is empty...
        bool bFilter = !ScanJobDBInserts.IsEmptyScanItemTable();
        if (bFilter)
        {
            //Check if there is at least one item from this base path... if no, we don't filter
            bFilter = ScanJobDBInserts.IsBasePathAlreadyInDB(ScanJobDTO.BasePath);
        }
        try
        {
            while (!bFinish)
            {
                //CancelToken?
                if (CancelToken.IsCancellationRequested)
                {
                    WriteChanges();
                    _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount} UnstableFiles={UnstableCount}");
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

                    //Filter
                    if (bFilter)
                    {
                        long filterStart = Throughput.StartStage();
                        bool alreadyInDB = ScanJobDBInserts.IsAlreadyInDB(si);
                        Throughput.EndStage(ThroughputStage.Database, filterStart);
                        if (alreadyInDB)
                        {
                            Throughput.AddFile();
                            Throughput.LogProgressIfDue();
                            continue;
                        }
                    }

                    //Calc hash
                    CalcHash(si);
                    Throughput.LogProgressIfDue();

                    //CancelToken?
                    if (CancelToken.IsCancellationRequested)
                    {
                        WriteChanges();
                        _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
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
                    _logger.LogTrace($"Scanning... FileEnumeration currentCount={_enumerateFiles.CurrentCount}  errorCount={_enumerateFiles.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Scan!");
        }
    }

    private void LogFinished()
    {
        _logger.LogInformation($"Scanning finished... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount} UnstableFiles={UnstableCount}");
        Throughput.LogSummary("Scanning finished");
    }

    private void LogDatabaseWriteFailure(BatchWriteException ex)
    {
        // Fatal: the database cannot be written, so continuing to hash would be pointless. RunnerState is left as it is (running).
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

    private void CalcHash(ScanItemDto item)
    {
        ScanHashOutcome outcome = HashItem(item, CancelToken);
        if (outcome.Kind == ScanHashOutcomeKind.Cancelled)
        {
            // Cancelled inside a big file: nothing is stored, no error item, no error count. The caller looks at the token right after this
            // method returns, writes the pending items and ends the run as cancelled, the same as for a cancel between two files.
            return;
        }
        RecordOutcome(item, outcome);
    }

    /// <summary>
    /// How hashing one file ended.
    /// </summary>
    private enum ScanHashOutcomeKind
    {
        /// <summary>The hash was computed and put into the item (with the stored size and time rule applied).</summary>
        Hashed,

        /// <summary>The file could not be hashed (<see cref="ScanHashOutcome.Error"/>).</summary>
        Failed,

        /// <summary>The token was cancelled while the file was read: nothing is stored, nothing is counted.</summary>
        Cancelled,
    }

    private readonly record struct ScanHashOutcome(ScanHashOutcomeKind Kind, long HashedBytes, Exception? Error);

    /// <summary>
    /// Hashes one file and applies the result to the item (hash, and size and time unless the job trusts the directory metadata). The first half of the per-file logic,
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

            if (hashResult.IsStable)
            {
                item.FileSha512Hash = hashResult.Hash!;
                if (!ScanJobDTO.TrustDirectoryMetadata)
                {
                    // Size and time of the file itself, read from the open file before and after the hash: they belong to the hash.
                    // (With TrustDirectoryMetadata the values of the directory listing stay, so that a later refresh, which compares the listing, sees the file as unchanged.)
                    item.FileSize = hashResult.Length;
                    item.FileLastModificationUTC = hashResult.LastWriteTimeUtc;
                }
                return new ScanHashOutcome(ScanHashOutcomeKind.Hashed, hashResult.BytesRead, null);
            }

            return new ScanHashOutcome(ScanHashOutcomeKind.Failed, 0, new FileChangedWhileReadException($"The file changed while it was read (read twice from the start, not stable either time: {hashResult.InstabilityDescription}). No hash was stored."));
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
    /// throughput counters and <see cref="UnstableCount"/>. A <see cref="BatchWriteException"/> of the database is not caught here, it ends the run.
    /// </summary>
    private void RecordOutcome(ScanItemDto item, ScanHashOutcome outcome)
    {
        Exception? hashError = outcome.Error;
        if (hashError is null)
        {
            long dbStart = Throughput.StartStage();
            try
            {
                ScanJobDBInserts.Enqueue(item);
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
            if (hashError is FileChangedWhileReadException)
            {
                UnstableCount++;
                _logger.LogWarning("Hashing of {file} was not possible: {message}", item.FilenameAndPath, hashError.Message);
            }
            else
            {
                _logger.LogError(hashError, "Hashing of {file} failed.", item.FilenameAndPath);
            }
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
