// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Refreshes an existing database by:
/// - Adding new files that are not yet in the database
/// - Updating existing files whose size or modification date has changed (recalculates hash)
/// - Removing files from the database that no longer exist on disk
/// - Skipping hash recalculation for files where size and date are unchanged
/// </summary>
internal class RefreshRunner : BasicRunner<RefreshRunner>, IRefreshRunner
{
    public RefreshRunner(MyDupFinderRefreshJobDTO refreshJobDTO, ILogger<RefreshRunner>? logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
    {
        RefreshJobDTO = refreshJobDTO;
        ScanJobDBInserts = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        Throughput = new ThroughputTracker(_logger);
    }

    // To detect redundant calls
    private bool _disposed = false;
    private MyDupFinderRefreshJobDTO RefreshJobDTO { get; set; }

    private ScanJobDBInserts ScanJobDBInserts { get; set; }

    public int AddedCount { get; private set; }
    public int UpdatedCount { get; private set; }
    public int RemovedCount { get; private set; }
    public int UnchangedCount { get; private set; }
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Files, bytes and stage times of the current run. Unchanged files count as files but add no hashed bytes.
    /// The enumerate time covers the directory listing only.
    /// </summary>
    internal ThroughputTracker Throughput { get; private set; }

    public override void Start(CancellationToken token)
    {
        base.Start(token);

        //Setup DB
        ScanJobDBInserts.SetupDB(RefreshJobDTO.DatabaseFile);
        _logger.LogInformation("Database state: {diagnostics}", ScanJobDBInserts.GetDatabaseDiagnostics());
        Throughput = new ThroughputTracker(_logger);
        Refresh();
        ScanJobDBInserts.Dispose();
    }

    private void Refresh()
    {
        //No throttling when ThrottlePercent is 0
        DiscThrottle? discThrottle = DiscThrottle.CreateForJob(RefreshJobDTO.ThrottlePercent);
        try
        {
            // Step 1: Get all existing items from the database for this base path
            long loadStart = Throughput.StartStage();
            var existingItems = ScanJobDBInserts.GetAllItemsByBasePath(RefreshJobDTO.BasePath);
            Throughput.EndStage(ThroughputStage.Database, loadStart);
            var existingByPath = new Dictionary<string, ScanItemDto>(StringComparer.Ordinal);
            foreach (var item in existingItems)
            {
                existingByPath[item.FilenameAndPath] = item;
            }

            _logger.LogInformation("Refresh: Found {count} existing items in database for base path {basePath}", existingItems.Count, RefreshJobDTO.BasePath);

            // Step 2: Enumerate files on disk and process them
            var filesOnDisk = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(RefreshJobDTO.BasePath, "*", SearchOption.AllDirectories);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error enumerating files in {basePath}", RefreshJobDTO.BasePath);
                RunnerState = IService.EServiceState.finished;
                return;
            }

            using var fileEnumerator = files.GetEnumerator();
            while (true)
            {
                long enumerateStart = Throughput.StartStage();
                bool hasNext = fileEnumerator.MoveNext();
                Throughput.EndStage(ThroughputStage.Enumerate, enumerateStart);
                if (!hasNext)
                {
                    break;
                }
                string currentFile = fileEnumerator.Current;

                //CancelToken?
                if (CancelToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Refresh was cancelled by user... Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
                    Throughput.LogSummary("Refresh cancelled");
                    RunnerState = IService.EServiceState.cancelled;
                    return;
                }

                filesOnDisk.Add(currentFile);

                long hashedBytes = 0;
                try
                {
                    var fileInfo = new FileInfo(currentFile);
                    long fileSize = fileInfo.Length;
                    DateTime fileLastModificationUTC = File.GetLastWriteTimeUtc(currentFile);

                    if (existingByPath.TryGetValue(currentFile, out var existingItem))
                    {
                        // File exists in DB - check if it changed
                        if (existingItem.FileSize == fileSize && existingItem.FileLastModificationUTC == fileLastModificationUTC)
                        {
                            // Size and date unchanged - skip hash recalculation, just touch
                            long dbStart = Throughput.StartStage();
                            ScanJobDBInserts.TouchItem(existingItem, DateTime.UtcNow);
                            Throughput.EndStage(ThroughputStage.Database, dbStart);
                            UnchangedCount++;
                        }
                        else
                        {
                            // Size or date changed - recalculate hash
                            string newHash = CalcHash(currentFile);
                            long oldSize = existingItem.FileSize;
                            long dbStart = Throughput.StartStage();
                            ScanJobDBInserts.UpdateItem(existingItem, fileSize, fileLastModificationUTC, newHash, DateTime.UtcNow);
                            Throughput.EndStage(ThroughputStage.Database, dbStart);
                            hashedBytes = fileSize;
                            UpdatedCount++;
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.LogTrace("Refresh: Updated {file} (size: {oldSize}->{newSize}, date changed)", currentFile, oldSize, fileSize);
                            }
                        }
                    }
                    else
                    {
                        // New file - add to DB
                        DateTime currDate = DateTime.UtcNow;
                        var si = new ScanItemDto
                        {
                            PathBase = RefreshJobDTO.BasePath,
                            FilenameAndPath = currentFile,
                            FirstScanDateUTC = currDate,
                            LastScanDateUTC = currDate,
                            LastSha512ScanDateUTC = currDate,
                            OriginComputer = RefreshJobDTO.OriginComputer,
                            ScanName = RefreshJobDTO.ScanName,
                            ScanExecutionComputer = Environment.MachineName,
                            FileCreationUTC = File.GetCreationTimeUtc(currentFile),
                            FileLastModificationUTC = fileLastModificationUTC,
                            FileSize = fileSize
                        };

                        string hash = CalcHash(currentFile);
                        si.FileSha512Hash = hash;
                        long dbStart = Throughput.StartStage();
                        ScanJobDBInserts.Enqueue(si);
                        Throughput.EndStage(ThroughputStage.Database, dbStart);
                        hashedBytes = fileSize;
                        AddedCount++;
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Refresh: Added new file {file}", currentFile);
                        }
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    _logger.LogError(ex, "Refresh: Error processing file {file}", currentFile);
                    ErrorCount++;
                }

                Throughput.AddFile(hashedBytes);
                Throughput.LogProgressIfDue();

                //Throttle
                discThrottle?.Throttle(CancelToken);
            }

            // Step 3: Remove items from DB that no longer exist on disk
            foreach (var kvp in existingByPath)
            {
                if (CancelToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Refresh was cancelled by user during removal phase... Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
                    Throughput.LogSummary("Refresh cancelled");
                    RunnerState = IService.EServiceState.cancelled;
                    return;
                }

                if (!filesOnDisk.Contains(kvp.Key))
                {
                    try
                    {
                        long dbStart = Throughput.StartStage();
                        ScanJobDBInserts.RemoveItem(kvp.Value);
                        Throughput.EndStage(ThroughputStage.Database, dbStart);
                        RemovedCount++;
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Refresh: Removed deleted file {file}", kvp.Key);
                        }
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        _logger.LogError(ex, "Refresh: Error removing file {file} from database", kvp.Key);
                        ErrorCount++;
                    }
                }
            }

            long writeStart = Throughput.StartStage();
            ScanJobDBInserts.WriteChanges();
            Throughput.EndStage(ThroughputStage.Database, writeStart);
            RunnerState = IService.EServiceState.finished;
            _logger.LogInformation("Refresh finished. Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
            Throughput.LogSummary("Refresh finished");
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Error in Refresh!");
        }
    }

    private string CalcHash(string filePath)
    {
        // The hash stage covers opening, reading, hashing and closing the file
        long hashStart = Throughput.StartStage();
        try
        {
            using var sha512 = SHA512.Create();
            using var stream = File.OpenRead(filePath);
            return BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", "", StringComparison.Ordinal);
        }
        finally
        {
            Throughput.EndStage(ThroughputStage.Hash, hashStart);
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
        }

        // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
        // TODO: set large fields to null.
        _disposed = true;

        // Call base class implementation.
        base.Dispose(disposing);
    }
}
