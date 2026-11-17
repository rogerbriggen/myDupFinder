// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// - Skipping hash recalculation for files where size and date are unchanged (such a file is not opened at all)
/// </summary>
internal class RefreshRunner : BasicRunner<RefreshRunner>, IRefreshRunner
{
    public RefreshRunner(MyDupFinderRefreshJobDTO refreshJobDTO, ILogger<RefreshRunner>? logger, IServiceProvider serviceProvider) : this(refreshJobDTO, logger, serviceProvider, null)
    {
    }

    /// <param name="dbInserts">Only for tests: a ready made object (for example with a small batch size). The runner disposes it.</param>
    /// <param name="fileSource">Only for tests: where the files come from (for example with unreadable directories). Default: the real file system.</param>
    /// <param name="fileHasher">Only for tests: a hasher with hooks or another buffer size. Default: <see cref="FileHasher.Default"/>.</param>
    internal RefreshRunner(MyDupFinderRefreshJobDTO refreshJobDTO, ILogger<RefreshRunner>? logger, IServiceProvider serviceProvider, ScanJobDBInserts? dbInserts, IFileEnumerationSource? fileSource = null, FileHasher? fileHasher = null) : base(logger, serviceProvider)
    {
        RefreshJobDTO = refreshJobDTO;
        _fileSource = fileSource ?? FileTreeEnumerationSource.Instance;
        _fileHasher = fileHasher ?? FileHasher.Default;
        ScanJobDBInserts = dbInserts ?? new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        Throughput = new ThroughputTracker(_logger);
    }

    // To detect redundant calls
    private bool _disposed = false;
    private MyDupFinderRefreshJobDTO RefreshJobDTO { get; set; }

    private ScanJobDBInserts ScanJobDBInserts { get; set; }

    private readonly IFileEnumerationSource _fileSource;

    private readonly FileHasher _fileHasher;

    public int AddedCount { get; private set; }
    public int UpdatedCount { get; private set; }
    public int RemovedCount { get; private set; }
    public int UnchangedCount { get; private set; }
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Surplus rows that were removed although their file exists: a path that has more than one row (a state that older versions of the scan produced) keeps
    /// one row (see <see cref="PrimaryRowChooser"/>), the others are removed. Not included in <see cref="RemovedCount"/> (that is the rows of files that are gone).
    /// </summary>
    public int SurplusRowsRemovedCount { get; private set; }

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
        ScanJobDBInserts.SetupDB(RefreshJobDTO.DatabaseFile, RefreshJobDTO.DatabaseSynchronous);
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
            // The identity rule (the one place in this runner): only the rows of this job's OriginComputer and ScanName take part. The database can hold the scans of other
            // computers or other named scans with equal path strings under the same base path; their rows are never updated, touched, removed or counted as surplus.
            var ownItems = existingItems.Where(i => PrimaryRowChooser.HasIdentity(i, RefreshJobDTO.OriginComputer, RefreshJobDTO.ScanName)).ToList();
            int foreignCount = existingItems.Count - ownItems.Count;
            _logger.LogInformation("Refresh: Found {count} existing items in database for base path {basePath}, {foreign} of them belong to another OriginComputer/ScanName than this job ({origin}/{scan}) and are left alone", ownItems.Count, RefreshJobDTO.BasePath, foreignCount, RefreshJobDTO.OriginComputer, RefreshJobDTO.ScanName);
            if (existingItems.Count > 0 && ownItems.Count == 0)
            {
                // Rows exist under this base path but none is of this job: refreshing would add every file again as a second set of rows. Change nothing (like a base path that cannot be listed).
                var identities = existingItems.Select(i => $"{i.OriginComputer}/{i.ScanName}").Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal);
                _logger.LogError("Refresh: the database has {count} rows for base path {basePath}, but none belongs to this job's OriginComputer/ScanName ({origin}/{scan}). The identities that exist under this base path: {identities}. Nothing was changed. Fix OriginComputer and ScanName of the job.", existingItems.Count, RefreshJobDTO.BasePath, RefreshJobDTO.OriginComputer, RefreshJobDTO.ScanName, string.Join(", ", identities));
                WriteChanges();
                RunnerState = IService.EServiceState.finished;
                return;
            }
            // All rows of a path are kept (older versions of the scan could leave several rows for one path)
            var existingByPath = PrimaryRowChooser.GroupByPath(ownItems);

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

                    if (existingByPath.TryGetValue(currentFile, out var existingRows))
                    {
                        // File exists in DB - the primary row of the path is the one that is kept, touched or updated; all other rows of the path are surplus
                        var existingItem = PrimaryRowChooser.Choose(existingRows, fileSize, fileLastModificationUTC);
                        if (PrimaryRowChooser.Matches(existingItem, fileSize, fileLastModificationUTC))
                        {
                            // Size and date unchanged - skip hash recalculation, just touch
                            long dbStart = Throughput.StartStage();
                            RemoveSurplusRows(currentFile, existingRows, existingItem);
                            ScanJobDBInserts.TouchItem(existingItem, DateTime.UtcNow);
                            Throughput.EndStage(ThroughputStage.Database, dbStart);
                            UnchangedCount++;
                        }
                        else
                        {
                            // Size or date changed - recalculate hash. If that fails nothing of the path is changed or removed (the exception leaves this block before any change is queued).
                            FileHashResult hashed = CalcHash(currentFile);
                            long oldSize = existingItem.FileSize;
                            long dbStart = Throughput.StartStage();
                            RemoveSurplusRows(currentFile, existingRows, existingItem);
                            ScanJobDBInserts.UpdateItem(existingItem, fileSize, fileLastModificationUTC, hashed.Hash, DateTime.UtcNow);
                            Throughput.EndStage(ThroughputStage.Database, dbStart);
                            hashedBytes = hashed.BytesRead;
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

                        FileHashResult hashed = CalcHash(currentFile);
                        si.FileSha512Hash = hashed.Hash;
                        long dbStart = Throughput.StartStage();
                        ScanJobDBInserts.Enqueue(si);
                        Throughput.EndStage(ThroughputStage.Database, dbStart);
                        hashedBytes = hashed.BytesRead;
                        AddedCount++;
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Refresh: Added new file {file}", currentFile);
                        }
                    }
                }
                catch (OperationCanceledException) when (CancelToken.IsCancellationRequested)
                {
                    // Cancelled inside a big file: nothing is stored or counted for it (no error), the pending writes of the files before it are flushed,
                    // and the run ends as cancelled, the same as for a cancel between two files
                    WriteChanges();
                    _logger.LogInformation("Refresh was cancelled by user while a file was read... Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
                    Throughput.LogSummary("Refresh cancelled");
                    RunnerState = IService.EServiceState.cancelled;
                    return;
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
                    // The file is gone: ALL rows of the path go, each of them subject to the protection of unreadable directories
                    foreach (ScanItemDto row in kvp.Value)
                    {
                        if (unreadableMatcher.IsProtected(row.FilenameAndPath))
                        {
                            ProtectedCount++;
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.LogTrace("Refresh: Kept row {id} of {file}, it lies below a directory that could not be read", row.Id, kvp.Key);
                            }
                            continue;
                        }
                        try
                        {
                            long dbStart = Throughput.StartStage();
                            ScanJobDBInserts.RemoveItem(row);
                            Throughput.EndStage(ThroughputStage.Database, dbStart);
                            RemovedCount++;
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.LogTrace("Refresh: Removed deleted file {file} (row {id})", kvp.Key, row.Id);
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
            }

            WriteChanges();
            RunnerState = IService.EServiceState.finished;
            if (UnreadableDirectories.Count > 0)
            {
                _logger.LogWarning("Refresh: {count} directories could not be read completely. {kept} database rows below them were kept (not removed, even if the file is gone). Directories: {directories}", UnreadableDirectories.Count, ProtectedCount, UnreadableDirectoryLog.Join(UnreadableDirectories));
            }
            if (SurplusRowsRemovedCount > 0)
            {
                _logger.LogWarning("Refresh: {surplus} surplus rows were removed. Some paths had more than one row in the database (an old database state). One row per path was kept: the one that matches the file (size and last write time), otherwise the one scanned last.", SurplusRowsRemovedCount);
            }
            _logger.LogInformation("Refresh finished. Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors} KeptBecauseDirectoryUnreadable={kept} UnreadableDirectories={unreadable} SurplusRowsRemoved={surplus}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount, ProtectedCount, UnreadableDirectories.Count, SurplusRowsRemovedCount);
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

    /// <summary>
    /// Queues the removal of every row of the path except the primary row (the file exists, so these rows are stale duplicates). Called only when the path is
    /// going to be touched or updated, never when hashing the changed file failed.
    /// </summary>
    private void RemoveSurplusRows(string file, List<ScanItemDto> rowsOfPath, ScanItemDto primary)
    {
        if (rowsOfPath.Count < 2)
        {
            return;
        }
        int removed = 0;
        foreach (ScanItemDto row in rowsOfPath)
        {
            if (row.Id != primary.Id)
            {
                ScanJobDBInserts.RemoveItem(row);
                SurplusRowsRemovedCount++;
                removed++;
            }
        }
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Refresh: {file} had {count} rows, kept row {id}, removed {removed} surplus rows", file, rowsOfPath.Count, primary.Id, removed);
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

    /// <exception cref="OperationCanceledException">The run was cancelled while the file was read.</exception>
    private FileHashResult CalcHash(string filePath)
    {
        // The hash stage covers opening, reading, hashing and closing the file
        long hashStart = Throughput.StartStage();
        try
        {
            return _fileHasher.Compute(filePath, CancelToken);
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
