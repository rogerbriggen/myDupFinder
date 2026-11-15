// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public RefreshRunner(MyDupFinderRefreshJobDTO refreshJobDTO, ILogger<RefreshRunner>? logger, IServiceProvider serviceProvider) : this(refreshJobDTO, logger, serviceProvider, null)
    {
    }

    /// <param name="dbInserts">Only for tests: a ready made object (for example with a small batch size). The runner disposes it.</param>
    /// <param name="fileSource">Only for tests: where the files come from (for example with unreadable directories). Default: the real file system.</param>
    internal RefreshRunner(MyDupFinderRefreshJobDTO refreshJobDTO, ILogger<RefreshRunner>? logger, IServiceProvider serviceProvider, ScanJobDBInserts? dbInserts, IFileEnumerationSource? fileSource = null) : base(logger, serviceProvider)
    {
        RefreshJobDTO = refreshJobDTO;
        _fileSource = fileSource ?? FileTreeEnumerationSource.Instance;
        ScanJobDBInserts = dbInserts ?? new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        Throughput = new ThroughputTracker(_logger);
    }

    // To detect redundant calls
    private bool _disposed = false;
    private MyDupFinderRefreshJobDTO RefreshJobDTO { get; set; }

    private ScanJobDBInserts ScanJobDBInserts { get; set; }

    private readonly IFileEnumerationSource _fileSource;

    public int AddedCount { get; private set; }
    public int UpdatedCount { get; private set; }
    public int RemovedCount { get; private set; }
    public int UnchangedCount { get; private set; }
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Rows that were NOT removed although their file was not enumerated, because they lie below a directory that could not be read.
    /// Only a complete enumeration may delete: these files might well still exist.
    /// </summary>
    public int ProtectedCount { get; private set; }

    /// <summary>
    /// Directories that could not be read completely during the enumeration (final after the enumeration ended).
    /// </summary>
    public IReadOnlyList<UnreadableDirectory> UnreadableDirectories { get; private set; } = Array.Empty<UnreadableDirectory>();

    /// <summary>
    /// Files, bytes and stage times of the current run. Unchanged files count as files but add no hashed bytes.
    /// The enumerate time covers the directory listing and the metadata (size, timestamps) of the files (one call per file unless TrustDirectoryMetadata is set), the same as in scan and check.
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
            using var fileEnumerator = _fileSource.Open(RefreshJobDTO.BasePath);
            UnreadableDirectories = fileEnumerator.UnreadableDirectories;
            int unreadableLogged = 0;
            while (true)
            {
                long enumerateStart = Throughput.StartStage();
                bool hasNext;
                try
                {
                    hasNext = fileEnumerator.MoveNext();
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    // The base path itself cannot be listed: nothing was enumerated, so nothing may be changed in the database
                    // (Files that were handled before the failure are written, nothing is removed.)
                    _logger.LogError(ex, "Error enumerating files in {basePath}. No rows were removed.", RefreshJobDTO.BasePath);
                    WriteChanges();
                    RunnerState = IService.EServiceState.finished;
                    return;
                }
                finally
                {
                    Throughput.EndStage(ThroughputStage.Enumerate, enumerateStart);
                }
                UnreadableDirectoryLog.LogNew(_logger, fileEnumerator.UnreadableDirectories, ref unreadableLogged);
                if (!hasNext)
                {
                    break;
                }
                FileEntryInfo currentEntry = fileEnumerator.Current;
                string currentFile = currentEntry.FullPath;

                //CancelToken?
                if (CancelToken.IsCancellationRequested)
                {
                    WriteChanges();
                    _logger.LogInformation("Refresh was cancelled by user... Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
                    Throughput.LogSummary("Refresh cancelled");
                    RunnerState = IService.EServiceState.cancelled;
                    return;
                }

                filesOnDisk.Add(currentFile);

                long hashedBytes = 0;
                try
                {
                    // Authoritative values from the file itself (one call) unless TrustDirectoryMetadata is set; read now, right before the file is processed
                    long metadataStart = Throughput.StartStage();
                    FileEntryInfo metadata;
                    try
                    {
                        metadata = FileMetadata.Resolve(currentEntry, RefreshJobDTO.TrustDirectoryMetadata);
                    }
                    finally
                    {
                        Throughput.EndStage(ThroughputStage.Enumerate, metadataStart);
                    }
                    long fileSize = metadata.Length;
                    DateTime fileLastModificationUTC = metadata.LastWriteTimeUtc;

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
                            FileCreationUTC = metadata.CreationTimeUtc,
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
                catch (Exception ex) when (ex is not BatchWriteException)
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

            // Step 3: Remove items from DB that no longer exist on disk.
            // Only here the enumeration is complete (the loop above ended because MoveNext returned false; a cancel or an error
            // returned before), so the list of unreadable directories is final. A file below such a directory was not enumerated
            // although it may exist, so its row must stay.
            // (UnreadableDirectories is the live list of the enumeration, final from here on)
            var unreadableMatcher = new UnreadableDirectoryMatcher(UnreadableDirectories.Select(d => d.Path), RefreshJobDTO.BasePath);
            foreach (var kvp in existingByPath)
            {
                if (CancelToken.IsCancellationRequested)
                {
                    WriteChanges();
                    _logger.LogInformation("Refresh was cancelled by user during removal phase... Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
                    Throughput.LogSummary("Refresh cancelled");
                    RunnerState = IService.EServiceState.cancelled;
                    return;
                }

                if (!filesOnDisk.Contains(kvp.Key))
                {
                    if (unreadableMatcher.IsProtected(kvp.Key))
                    {
                        ProtectedCount++;
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Refresh: Kept {file}, it lies below a directory that could not be read", kvp.Key);
                        }
                        continue;
                    }
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
                    catch (Exception ex) when (ex is not BatchWriteException)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        _logger.LogError(ex, "Refresh: Error removing file {file} from database", kvp.Key);
                        ErrorCount++;
                    }
                }
            }

            WriteChanges();
            RunnerState = IService.EServiceState.finished;
            if (UnreadableDirectories.Count > 0)
            {
                _logger.LogWarning("Refresh: {count} directories could not be read completely. {kept} database rows below them were kept (not removed, even if the file is gone). Directories: {directories}", UnreadableDirectories.Count, ProtectedCount, UnreadableDirectoryLog.Join(UnreadableDirectories));
            }
            _logger.LogInformation("Refresh finished. Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors} KeptBecauseDirectoryUnreadable={kept} UnreadableDirectories={unreadable}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount, ProtectedCount, UnreadableDirectories.Count);
            Throughput.LogSummary("Refresh finished");
        }
        catch (BatchWriteException ex)
        {
            // Fatal: the database cannot be written. RunnerState is left as it is (running).
            _logger.LogError(ex, "Refresh stopped because of a database write failure. {PendingCount} operations could not be written. Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors} (counted when queued, not all of them are in the database).", ex.PendingOperationCount, AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Error in Refresh!");
        }
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
