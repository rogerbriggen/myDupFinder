// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The scan pipeline (HashWorkers greater than 1).
/// <code>
/// producer thread --work queue (bounded)--> N hash workers --result queue (bounded)--> writer (the runner's own thread)
/// </code>
/// <list type="bullet">
/// <item><b>Producer</b> (one dedicated thread): enumeration, the metadata read, the resume filter, the throttle and the progress line, exactly the work that the
/// sequential loop does before hashing. A file that passes goes to the work queue.</item>
/// <item><b>Hash workers</b> (dedicated threads): take a file, hash it with <see cref="HashItem"/> and put the item and the outcome on the result queue. They never touch the
/// database or the counters.</item>
/// <item><b>Writer</b> (the thread that called <c>Start</c>): takes the results and calls <see cref="RecordOutcome"/>, the second half of the per-file logic. It is the only
/// thread that writes to the database, owns the counters, the batches and the final flush, and sets the runner state, so state events and the end of the run happen
/// on the caller's thread, and <c>Start</c> returns when everything has ended.</item>
/// </list>
/// Both queues are bounded and writers wait when they are full (back-pressure), so the number of files in flight never exceeds <see cref="InFlightLimit"/>
/// whatever the size of the tree.
/// <para><b>How a stage wakes up.</b> Three tokens: the user's token, <c>stop</c> (linked to it, also cancelled when a worker fails unexpectedly and, at the end,
/// by the writer) and <c>writerGone</c> (cancelled when the writer leaves). A stage that waits for room in a queue or for an item waits on a token that is cancelled when the
/// stage on the other side of the queue dies: the producer and the workers wait on <c>stop</c> (the writer cancels it when it leaves), the workers wait for room in the
/// result queue on <c>writerGone</c>, the writer waits for results without a token, but the result queue is always completed, by the last worker to leave (a counter in
/// the <c>finally</c> of each worker), and the work queue is always completed by the producer's <c>finally</c>. Every stage catches everything and ends in a <c>finally</c>.</para>
/// <para>After a user cancel the results that were already completed are still written (workers hand over a finished file even when <c>stop</c> is set, the writer
/// drains until the result queue is completed), the files in progress are dropped (no row, no error item).</para>
/// </summary>
internal partial class ScanRunner
{
    /// <summary>Capacity of each of the two queues per hash worker.</summary>
    internal const int QueueCapacityPerWorker = 4;

    /// <summary>Only for tests: called by a hash worker with the item it has just taken from the work queue, outside the handling of a single file (an exception here is an unexpected worker failure).</summary>
    internal Action<ScanItemDto>? WorkerItemHook { get; set; }

    /// <summary>Only for tests: called by the writer with the item of a result it has just taken from the result queue, before it is recorded.</summary>
    internal Action<ScanItemDto>? WriterResultHook { get; set; }

    /// <summary>
    /// Upper bound of the files that are in the pipeline at the same time (counted from the moment the producer holds a file that passed the filter until the writer has recorded it):
    /// the work queue, the files in the hands of the workers, the result queue, the one the writer holds and the one the producer holds while it waits for room. 0 before a pipeline run.
    /// </summary>
    internal int InFlightLimit { get; private set; }

    /// <summary>Only for tests: the highest number of files that were in the pipeline at the same time during the last pipeline run. Never above <see cref="InFlightLimit"/>.</summary>
    internal int MaxItemsInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Only for tests: number of producer and worker threads that are running right now. Must be 0 when <c>Start</c> has returned.</summary>
    internal int RunningStageCount => Volatile.Read(ref _runningStages);

    private readonly record struct PipelineResult(ScanItemDto Item, ScanHashOutcome Outcome);

    private int _runningStages;
    private int _inFlight;
    private int _maxInFlight;
    private int _activeWorkers;

    // Set by any stage: true when the run did not process every file because a stage stopped early (a cancel or a failure).
    // A run is only "finished" when this is still false.
    private volatile bool _interrupted;

    // Set when a stage failed with an unexpected exception (already logged). The state is then left as it is (running), like "Error in Scan!" in the sequential scan.
    private volatile bool _failed;

    private void ScanWithPipeline()
    {
        int workers = ScanJobDTO.HashWorkers;
        //Don't filter if db is empty... (same decision as the sequential scan, made before any other thread starts)
        bool bFilter = !ScanJobDBInserts.IsEmptyScanItemTable();
        if (bFilter)
        {
            //Check if there is at least one item from this base path... if no, we don't filter
            bFilter = ScanJobDBInserts.IsBasePathAlreadyInDB(ScanJobDTO.BasePath);
        }

        int workCapacity = workers * QueueCapacityPerWorker;
        int resultCapacity = workers * QueueCapacityPerWorker;
        InFlightLimit = workCapacity + workers + resultCapacity + 2;
        _inFlight = 0;
        _maxInFlight = 0;
        _interrupted = false;
        _failed = false;

        var work = Channel.CreateBounded<ScanItemDto>(new BoundedChannelOptions(workCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false });
        var results = Channel.CreateBounded<PipelineResult>(new BoundedChannelOptions(resultCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = true });
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(CancelToken);
        using var writerGoneCts = new CancellationTokenSource();
        DiscThrottle? discThrottle = DiscThrottle.CreateForJob(ScanJobDTO.ThrottlePercent);
        _activeWorkers = workers;
        var tasks = new List<Task>(workers + 1);
        try
        {
            tasks.Add(Task.Factory.StartNew(() => ProduceFiles(bFilter, discThrottle, work.Writer, stopCts.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            for (int i = 0; i < workers; i++)
            {
                tasks.Add(Task.Factory.StartNew(() => HashWorker(work.Reader, results.Writer, stopCts, writerGoneCts.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }

            // The writer: this thread. Returns when the result queue is completed, i.e. when every worker has ended.
            WriteResults(results.Reader);
            JoinStages(tasks);

            WriteChanges();
            if (_failed)
            {
                // Already logged where it happened. The state is left as it is (running), the same as "Error in Scan!" in the sequential scan.
                return;
            }
            if (CancelToken.IsCancellationRequested && _interrupted)
            {
                _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount} UnstableFiles={UnstableCount}");
                Throughput.LogSummary("Scanning cancelled");
                LogUnreadableSummary(complete: false);
                RunnerState = IService.EServiceState.cancelled;
                return;
            }
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
        finally
        {
            // Whatever happened: no stage may keep running (they wake up on these tokens) and every task is observed before Start goes on to dispose the enumerator and the database.
            writerGoneCts.Cancel();
            stopCts.Cancel();
            JoinStages(tasks);
        }
    }

    private void JoinStages(List<Task> tasks)
    {
        try
        {
            Task.WaitAll(tasks.ToArray());
        }
        catch (AggregateException ex)
        {
            // The stages catch everything themselves, so this is not expected. Observe and log it so that nothing vanishes.
            _failed = true;
            foreach (Exception inner in ex.InnerExceptions)
            {
                _logger.LogError(inner, "A scan pipeline thread ended with an unexpected exception.");
            }
        }
    }

    /// <summary>The producer: the first half of the sequential loop, then the file goes to the work queue.</summary>
    private void ProduceFiles(bool bFilter, DiscThrottle? discThrottle, ChannelWriter<ScanItemDto> work, CancellationToken stop)
    {
        Interlocked.Increment(ref _runningStages);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                //Throttle
                discThrottle?.Throttle(stop);

                //Enumerate
                if (!(_enumerateFiles?.HasMore == true))
                {
                    // The natural end: every file was handed over
                    return;
                }
                long enumerateStart = Throughput.StartStage();
                _enumerateFiles.EnumerateFiles();
                Throughput.EndStage(ThroughputStage.Enumerate, enumerateStart);

                while (true)
                {
                    //Get item
                    ScanItemDto? si;
                    if (!_enumerateFiles.ScanItemCollection.TryDequeue(out si))
                    {
                        break;
                    }
                    if (stop.IsCancellationRequested)
                    {
                        _interrupted = true;
                        return;
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

                    //Hand over to the workers (waits while the work queue is full)
                    int inFlight = Interlocked.Increment(ref _inFlight);
                    RaiseMaxInFlight(inFlight);
                    try
                    {
                        BlockingWrite(work, si, stop);
                    }
                    catch
                    {
                        Interlocked.Decrement(ref _inFlight);
                        throw;
                    }
                    Throughput.LogProgressIfDue();

                    //Throttle
                    discThrottle?.Throttle(stop);
                }
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace($"Scanning... FileEnumeration currentCount={_enumerateFiles.CurrentCount}  errorCount={_enumerateFiles.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
                }
            }
            // The loop ended because of the token
            _interrupted = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _interrupted = true;
        }
#pragma warning disable CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        catch (Exception ex)
#pragma warning restore CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        {
            // For example the base path cannot be enumerated. What is already in the work queue is still hashed and written.
            _failed = true;
            _logger.LogError(ex, "Error in Scan!");
        }
        finally
        {
            // Always: the workers end when the queue is completed and empty
            work.TryComplete();
            Interlocked.Decrement(ref _runningStages);
        }
    }

    /// <summary>A hash worker: takes a file from the work queue, hashes it and hands the result to the writer.</summary>
    private void HashWorker(ChannelReader<ScanItemDto> work, ChannelWriter<PipelineResult> results, CancellationTokenSource stopCts, CancellationToken writerGone)
    {
        CancellationToken stop = stopCts.Token;
        Interlocked.Increment(ref _runningStages);
        try
        {
            while (true)
            {
                if (stop.IsCancellationRequested)
                {
                    // Files that are still in the work queue are dropped
                    _interrupted = true;
                    return;
                }
                if (!work.TryRead(out ScanItemDto? item))
                {
                    try
                    {
                        if (!work.WaitToReadAsync(stop).AsTask().GetAwaiter().GetResult())
                        {
                            // Completed and empty: the natural end
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        _interrupted = true;
                        return;
                    }
                    continue;
                }

                WorkerItemHook?.Invoke(item);

                ScanHashOutcome outcome = HashItem(item, stop);
                if (outcome.Kind == ScanHashOutcomeKind.Cancelled)
                {
                    // Cancelled inside the file: nothing is stored, no error item, no error count
                    _interrupted = true;
                    Interlocked.Decrement(ref _inFlight);
                    return;
                }

                // A finished file is handed over even when stop is set: completed results are never thrown away. Waits for room while the result queue is full.
                BlockingWrite(results, new PipelineResult(item, outcome), writerGone);
            }
        }
        catch (OperationCanceledException) when (writerGone.IsCancellationRequested)
        {
            // The writer has left (database failure): nothing can be written any more
            _interrupted = true;
        }
#pragma warning disable CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        catch (Exception ex)
#pragma warning restore CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        {
            // Not about one file (a failing file is an outcome of HashItem): stop the pipeline. Results that are already completed are still written.
            _failed = true;
            _logger.LogError(ex, "Scanning stopped because a hash worker failed unexpectedly.");
            stopCts.Cancel();
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeWorkers) == 0)
            {
                // The last worker has left: the writer can end when it has drained the queue
                results.TryComplete();
            }
            Interlocked.Decrement(ref _runningStages);
        }
    }

    /// <summary>The writer: runs on the thread of the runner. Ends when the result queue is completed and empty.</summary>
    private void WriteResults(ChannelReader<PipelineResult> results)
    {
        while (true)
        {
            if (!results.TryRead(out PipelineResult result))
            {
                // No token on purpose: results that were already completed must be written even after a cancel. The queue is completed by the last worker.
                if (!results.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    return;
                }
                continue;
            }

            WriterResultHook?.Invoke(result.Item);
            RecordOutcome(result.Item, result.Outcome);
            Interlocked.Decrement(ref _inFlight);
            Throughput.LogProgressIfDue();
        }
    }

    /// <summary>Writes the item, waiting while the queue is full. Throws <see cref="OperationCanceledException"/> when the token is cancelled while it waits.</summary>
    private static void BlockingWrite<T>(ChannelWriter<T> writer, T item, CancellationToken token)
    {
        if (writer.TryWrite(item))
        {
            return;
        }
        writer.WriteAsync(item, token).AsTask().GetAwaiter().GetResult();
    }

    private void RaiseMaxInFlight(int value)
    {
        int current = Volatile.Read(ref _maxInFlight);
        while (value > current)
        {
            int seen = Interlocked.CompareExchange(ref _maxInFlight, value, current);
            if (seen == current)
            {
                return;
            }
            current = seen;
        }
    }
}
