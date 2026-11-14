// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderDB;

public class ScanJobDBInserts : IDisposable
{

    private DubFinderContext? _dbContext;
    private DbContextOptions<DubFinderContext>? _dbContextOptions;
    /// <summary>Default number of pending operations that triggers a flush.</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>Default time after which the next added operation triggers a flush.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

    private enum PendingKind
    {
        InsertItem,
        InsertError,
        Touch,
        Update,
        Remove,
    }

    private readonly record struct PendingOperation(PendingKind Kind, ScanItemDto? Item, ScanErrorItemDto? ErrorItem);

    // All operations that are not yet committed, in the order they were added
    private readonly List<PendingOperation> _pending = new List<PendingOperation>();
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly TimeProvider _timeProvider;
    private long _lastFlushTimestamp;
    private readonly object dbContextLock = new object();
    private readonly ILogger<ScanJobDBInserts> _logger;
    private bool _disposedValue;
    public int TotalSuccessCount { get; private set; }
    public int TotalErrorCount { get; private set; }


    /// <summary>
    /// Creates the object. Writes are collected and committed in one transaction per batch.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="batchSize">Number of pending operations that triggers a flush (default 500).</param>
    /// <param name="flushInterval">Time since the last flush after which the next added operation triggers a flush (default 5 seconds). There is no timer: this is only checked when an operation is added.</param>
    /// <param name="timeProvider">Clock, for tests. Default is <see cref="TimeProvider.System"/>.</param>
    public ScanJobDBInserts(ILogger<ScanJobDBInserts>? logger, int batchSize = DefaultBatchSize, TimeSpan? flushInterval = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<ScanJobDBInserts>();
        _batchSize = batchSize;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastFlushTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets the number of operations that were added but are not yet committed to the database.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (dbContextLock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Gets the number of entities the change tracker currently holds (for tests and diagnostics).
    /// </summary>
    internal int TrackedEntityCount
    {
        get
        {
            lock (dbContextLock)
            {
                return _dbContext?.ChangeTracker.Entries().Count() ?? 0;
            }
        }
    }

    public void SetupDB(string databaseFile)
    {
        lock (dbContextLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databaseFile) ?? "");
            _dbContextOptions = DubFinderContextFactory.GetDbContextOptions(databaseFile);
            _dbContext = new DubFinderContext(_dbContextOptions);
            _dbContext.Database.Migrate();
            _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            TotalSuccessCount = 0;
            TotalErrorCount = 0;
            _lastFlushTimestamp = _timeProvider.GetTimestamp();
        }

    }

    /// <summary>
    /// Reads journal mode, synchronous setting and the indexes of the ScanItems table. Changes nothing.
    /// </summary>
    public DatabaseDiagnostics GetDatabaseDiagnostics()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("GetDatabaseDiagnostics called without SetupDB!");
        }
        lock (dbContextLock)
        {
            var connection = _dbContext.Database.GetDbConnection();
            bool wasClosed = connection.State != ConnectionState.Open;
            if (wasClosed)
            {
                connection.Open();
            }
            try
            {
                string journalMode = Convert.ToString(ExecuteScalar(connection, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture) ?? string.Empty;
                long synchronous = Convert.ToInt64(ExecuteScalar(connection, "PRAGMA synchronous;"), CultureInfo.InvariantCulture);

                var indexes = new List<string>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'ScanItems' ORDER BY name;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        indexes.Add(reader.GetString(0));
                    }
                }
                return new DatabaseDiagnostics(journalMode, synchronous, indexes);
            }
            finally
            {
                if (wasClosed)
                {
                    connection.Close();
                }
            }
        }
    }

    private static object? ExecuteScalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Adds a scan item to the pending operations. It reaches the database with the next flush.
    /// Throws <see cref="BatchWriteException"/> if this call triggered a flush that failed.
    /// </summary>
    public void Enqueue(ScanItemDto si)
    {
        AddPending(new PendingOperation(PendingKind.InsertItem, si, null), nameof(Enqueue));
    }

    /// <summary>
    /// Adds a scan error item to the pending operations. It reaches the database with the next flush.
    /// Throws <see cref="BatchWriteException"/> if this call triggered a flush that failed.
    /// </summary>
    public void Enqueue(ScanErrorItemDto sei)
    {
        AddPending(new PendingOperation(PendingKind.InsertError, null, sei), nameof(Enqueue));
    }

    private void AddPending(PendingOperation operation, string callerName)
    {
        if ((_dbContext is null) || (_dbContextOptions is null))
        {
            throw new InvalidOperationException($"{callerName} called without SetupDB!");
        }
        lock (dbContextLock)
        {
            _pending.Add(operation);
            if ((_pending.Count >= _batchSize) || (_timeProvider.GetElapsedTime(_lastFlushTimestamp) >= _flushInterval))
            {
                FlushInternal();
            }
        }
    }

    /// <summary>
    /// Writes all pending operations in one transaction. Must be called with the lock held.
    /// On success the pending operations are cleared, the counters advance and the change tracker is emptied.
    /// On failure everything is rolled back, the operations stay pending, the counters do not change and a
    /// <see cref="BatchWriteException"/> is thrown.
    /// </summary>
    private void FlushInternal()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("Enqueue or WriteChanges called without SetupDB!");
        }
        if (_pending.Count == 0)
        {
            _lastFlushTimestamp = _timeProvider.GetTimestamp();
            return;
        }

        int successCount = 0;
        int errorCount = 0;
        // The entity instance that is tracked for a primary key in this batch, and the keys removed in this batch.
        // Needed because the same row can be touched, updated and removed several times (also through different objects).
        var trackedById = new Dictionary<Guid, ScanItemDto>();
        var removedIds = new HashSet<Guid>();
        try
        {
            using var transaction = _dbContext.Database.BeginTransaction();
            try
            {
                foreach (PendingOperation op in _pending)
                {
                    switch (op.Kind)
                    {
                        case PendingKind.InsertItem:
                            _dbContext.Add(op.Item!);
                            trackedById[op.Item!.Id] = op.Item!;
                            successCount++;
                            break;
                        case PendingKind.InsertError:
                            _dbContext.Add(op.ErrorItem!);
                            errorCount++;
                            break;
                        case PendingKind.Touch:
                            ApplyModification(op.Item!, false, trackedById, removedIds);
                            break;
                        case PendingKind.Update:
                            if (ApplyModification(op.Item!, true, trackedById, removedIds))
                            {
                                successCount++;
                            }
                            break;
                        case PendingKind.Remove:
                            ApplyRemove(op.Item!, trackedById, removedIds);
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown pending operation {op.Kind}");
                    }
                }
                _dbContext.SaveChanges();
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
#pragma warning disable CA1031 // The original exception is more important than a failing rollback (disposing the transaction rolls back anyway)
                catch (Exception rollbackException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(rollbackException, "Rollback of the failed batch also failed.");
                }
                throw;
            }
        }
#pragma warning disable CA1031 // Every failure of the batch has the same consequence
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Forget what was attached, so a later retry does not add the same entities twice
            _dbContext.ChangeTracker.Clear();
            int pendingCount = _pending.Count;
            _logger.LogError(ex, "Writing a batch of {PendingCount} operations to the database failed. The batch was rolled back, the operations stay pending.", pendingCount);
            throw new BatchWriteException($"Writing a batch of {pendingCount} operations to the database failed: {ex.Message}", pendingCount, ex);
        }

        // Only now, after the commit, the batch counts as written
        _pending.Clear();
        TotalSuccessCount += successCount;
        TotalErrorCount += errorCount;
        _dbContext.ChangeTracker.Clear();
        _lastFlushTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Applies a touch (only LastScanDateUTC) or an update (the five update columns) to the change tracker.
    /// An entity is attached only if its key is not tracked yet; otherwise only the modified flags are added to the
    /// tracked entry (an entity that is Added stays Added). If a different object with the same key is already
    /// tracked, the written values of the given object are copied onto the tracked one, so the later operation wins
    /// for the columns it writes and the columns only written earlier are kept.
    /// Must be called with the lock held. Returns false if the operation was ignored because the row is removed in this batch.
    /// </summary>
    private bool ApplyModification(ScanItemDto item, bool fullUpdate, Dictionary<Guid, ScanItemDto> trackedById, HashSet<Guid> removedIds)
    {
        if (removedIds.Contains(item.Id))
        {
            return false;
        }
        ScanItemDto target;
        if (trackedById.TryGetValue(item.Id, out var tracked))
        {
            target = tracked;
            if (!ReferenceEquals(tracked, item))
            {
                tracked.LastScanDateUTC = item.LastScanDateUTC;
                if (fullUpdate)
                {
                    tracked.FileSize = item.FileSize;
                    tracked.FileLastModificationUTC = item.FileLastModificationUTC;
                    tracked.FileSha512Hash = item.FileSha512Hash;
                    tracked.LastSha512ScanDateUTC = item.LastSha512ScanDateUTC;
                }
            }
        }
        else
        {
            target = item;
            _dbContext!.Attach(item);
            trackedById[item.Id] = item;
        }

        var entry = _dbContext!.Entry(target);
        if (entry.State == EntityState.Added)
        {
            // The row is inserted with the current values of the object, nothing to flag
            return true;
        }
        entry.Property(i => i.LastScanDateUTC).IsModified = true;
        if (fullUpdate)
        {
            entry.Property(i => i.FileSize).IsModified = true;
            entry.Property(i => i.FileLastModificationUTC).IsModified = true;
            entry.Property(i => i.FileSha512Hash).IsModified = true;
            entry.Property(i => i.LastSha512ScanDateUTC).IsModified = true;
        }
        return true;
    }

    /// <summary>
    /// Applies a remove to the change tracker. Works on whatever instance is tracked for the key; operations
    /// on the key that come later in the same batch are ignored. Must be called with the lock held.
    /// </summary>
    private void ApplyRemove(ScanItemDto item, Dictionary<Guid, ScanItemDto> trackedById, HashSet<Guid> removedIds)
    {
        if (!removedIds.Add(item.Id))
        {
            return;
        }
        if (trackedById.TryGetValue(item.Id, out var tracked))
        {
            // Removing an entity that is Added drops it again: nothing is inserted
            _dbContext!.Remove(tracked);
        }
        else
        {
            _dbContext!.Attach(item);
            _dbContext.Remove(item);
        }
    }

    public bool IsEmptyScanItemTable()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("IsEmptyScanItemTable called without SetupDB!");
        }
        lock (dbContextLock)
        {
            var itemCount = _dbContext.ScanItems?.Count();
            if (itemCount is null)
            {
                return true;
            }
            else if (itemCount == 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }

    public bool IsAlreadyInDB(ScanItemDto si)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("IsAlreadyInDB called without SetupDB!");
        }
        lock (dbContextLock)
        {
            var itemCount = _dbContext.ScanItems?.Where(s => ((s.FilenameAndPath == si.FilenameAndPath) && (s.FileSize == si.FileSize) && (s.ScanExecutionComputer == si.ScanExecutionComputer))).Count();
            if (itemCount is null)
            {
                return false;
            }
            else if (itemCount == 0)
            {
                return false;
            }
            else if (itemCount == 1)
            {
                return true;
            }
            else
            {
                //Strange
                _logger.LogWarning($"IsAlreadyInDB had a strange itemCount of {itemCount}...");
                return true;
            }
        }

    }

    public bool IsBasePathAlreadyInDB(string basePath)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("IsBasePathAlreadyInDB called without SetupDB!");
        }
        lock (dbContextLock)
        {

            var itemCount = _dbContext.ScanItems?.Where(s => (s.PathBase == basePath)).Count();

            if (itemCount is null)
            {
                return false;
            }
            else if (itemCount == 0)
            {
                return false;
            }
            else
            {
                //There are already some in the database
                return true;
            }
        }

    }

    /// <summary>
    /// Gets all scan items from the database that belong to the specified base path.
    /// Used by refresh to know what files are currently tracked.
    /// </summary>
    /// <param name="basePath">The base path to filter by.</param>
    /// <returns>A list of scan items with the specified base path, or an empty list if none found.</returns>
    public List<ScanItemDto> GetAllItemsByBasePath(string basePath)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("GetAllItemsByBasePath called without SetupDB!");
        }
        lock (dbContextLock)
        {
            return _dbContext.ScanItems?.AsNoTracking().Where(s => s.PathBase == basePath).ToList() ?? new List<ScanItemDto>();
        }
    }

    /// <summary>
    /// Gets all scan items that belong to the specified logical scan identity.
    /// Used by check jobs with IgnoreBasePath so moved trees can still report missing old-base rows.
    /// </summary>
    public List<ScanItemDto> GetAllItemsByScanIdentity(string originComputer, string scanName)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("GetAllItemsByScanIdentity called without SetupDB!");
        }
        lock (dbContextLock)
        {
            return _dbContext.ScanItems?
                .AsNoTracking()
                .Where(s => s.OriginComputer == originComputer && s.ScanName == scanName)
                .ToList() ?? new List<ScanItemDto>();
        }
    }

    /// <summary>
    /// Updates an existing scan item in the database (queued, written with the next flush).
    /// Used by refresh when file size or modification date has changed and the hash needs recalculation.
    /// Only FileSize, FileLastModificationUTC, FileSha512Hash, LastScanDateUTC and LastSha512ScanDateUTC are written.
    /// The given object is changed immediately.
    /// Several operations on the same row in one batch are applied in the order they were added (the later value wins per column).
    /// If the row is removed earlier in the same batch (<see cref="RemoveItem"/>), this operation is ignored and does not count as success.
    /// </summary>
    public void UpdateItem(ScanItemDto existingItem, long newFileSize, DateTime newFileLastModificationUTC, string newFileSha512Hash, DateTime scanDateUTC)
    {
        ArgumentNullException.ThrowIfNull(existingItem);
        if (_dbContext is null)
        {
            throw new InvalidOperationException("UpdateItem called without SetupDB!");
        }
        lock (dbContextLock)
        {
            existingItem.FileSize = newFileSize;
            existingItem.FileLastModificationUTC = newFileLastModificationUTC;
            existingItem.FileSha512Hash = newFileSha512Hash;
            existingItem.LastScanDateUTC = scanDateUTC;
            existingItem.LastSha512ScanDateUTC = scanDateUTC;
            AddPending(new PendingOperation(PendingKind.Update, existingItem, null), nameof(UpdateItem));
        }
    }

    /// <summary>
    /// Updates the LastScanDateUTC of an existing scan item without recalculating the hash (queued, written with the next flush).
    /// Used by refresh when file size and date are unchanged (the file has not changed). Only LastScanDateUTC is written.
    /// The given object is changed immediately.
    /// Several operations on the same row in one batch are applied in the order they were added.
    /// If the row is removed earlier in the same batch (<see cref="RemoveItem"/>), this operation is ignored.
    /// </summary>
    public void TouchItem(ScanItemDto existingItem, DateTime scanDateUTC)
    {
        ArgumentNullException.ThrowIfNull(existingItem);
        if (_dbContext is null)
        {
            throw new InvalidOperationException("TouchItem called without SetupDB!");
        }
        lock (dbContextLock)
        {
            existingItem.LastScanDateUTC = scanDateUTC;
            AddPending(new PendingOperation(PendingKind.Touch, existingItem, null), nameof(TouchItem));
        }
    }

    /// <summary>
    /// Removes a scan item from the database (queued, written with the next flush).
    /// Used by refresh when a file no longer exists on disk.
    /// A removed row stays removed: touches and updates of the same row that are added later in the same batch are ignored.
    /// </summary>
    public void RemoveItem(ScanItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_dbContext is null)
        {
            throw new InvalidOperationException("RemoveItem called without SetupDB!");
        }
        lock (dbContextLock)
        {
            AddPending(new PendingOperation(PendingKind.Remove, item, null), nameof(RemoveItem));
        }
    }

    /// <summary>
    /// Finds the scan item with the exact FilenameAndPath, or null if none exists.
    /// </summary>
    public ScanItemDto? GetItemByPath(string filenameAndPath)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("GetItemByPath called without SetupDB!");
        }
        lock (dbContextLock)
        {
            return _dbContext.ScanItems?.FirstOrDefault(s => s.FilenameAndPath == filenameAndPath);
        }
    }

    /// <summary>
    /// Finds scan items whose FilenameAndPath ends with the given relative sub-path.
    /// Used when IgnoreBasePath is set so files moved to a different drive/computer
    /// can still be matched by their position under their original base path.
    /// </summary>
    public List<ScanItemDto> GetItemsByRelativeSuffix(string relativeSuffix)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("GetItemsByRelativeSuffix called without SetupDB!");
        }
        lock (dbContextLock)
        {
            return _dbContext.ScanItems?.Where(s => s.FilenameAndPath.EndsWith(relativeSuffix)).ToList() ?? new List<ScanItemDto>();
        }
    }

    /// <summary>
    /// Looks up a scan item by its primary key.
    /// </summary>
    public ScanItemDto? GetItemById(Guid id)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("GetItemById called without SetupDB!");
        }
        lock (dbContextLock)
        {
            return _dbContext.ScanItems?.FirstOrDefault(s => s.Id == id);
        }
    }

    /// <summary>
    /// Applies the field updates carried by a check-report row to an existing scan item.
    /// Any non-null argument overwrites the existing value; nulls are left as they are.
    /// </summary>
    public void ApplyUpdateById(
        Guid id,
        long? newFileSize,
        DateTime? newFileLastModificationUTC,
        string? newFileSha512Hash,
        DateTime scanDateUTC,
        bool hashWasRecomputed,
        string? newFilenameAndPath,
        string? newPathBase)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("ApplyUpdateById called without SetupDB!");
        }
        lock (dbContextLock)
        {
            var existingItem = _dbContext.ScanItems?.FirstOrDefault(s => s.Id == id);
            if (existingItem is null)
            {
                throw new InvalidOperationException($"No ScanItem with Id {id} found.");
            }
            if (newFilenameAndPath is not null)
            {
                existingItem.FilenameAndPath = newFilenameAndPath;
            }
            if (newPathBase is not null)
            {
                existingItem.PathBase = newPathBase;
            }
            if (newFileSize.HasValue)
            {
                existingItem.FileSize = newFileSize.Value;
            }
            if (newFileLastModificationUTC.HasValue)
            {
                existingItem.FileLastModificationUTC = newFileLastModificationUTC.Value;
            }
            if (newFileSha512Hash is not null)
            {
                existingItem.FileSha512Hash = newFileSha512Hash;
            }
            existingItem.LastScanDateUTC = scanDateUTC;
            if (hashWasRecomputed)
            {
                existingItem.LastSha512ScanDateUTC = scanDateUTC;
            }
            _dbContext.Entry(existingItem).State = EntityState.Modified;
            _dbContext.SaveChanges();
            _dbContext.Entry(existingItem).State = EntityState.Detached;
            TotalSuccessCount++;
        }
    }

    /// <summary>
    /// Removes the scan item with the given primary key. Returns true when a row was removed.
    /// </summary>
    public bool RemoveItemById(Guid id)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("RemoveItemById called without SetupDB!");
        }
        lock (dbContextLock)
        {
            var existingItem = _dbContext.ScanItems?.FirstOrDefault(s => s.Id == id);
            if (existingItem is null)
            {
                return false;
            }
            _dbContext.Remove(existingItem);
            _dbContext.SaveChanges();
            return true;
        }
    }

    /// <summary>
    /// Inserts a brand new scan item, used by applyCheck for NewOnDisk rows.
    /// </summary>
    public void InsertItem(ScanItemDto item)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("InsertItem called without SetupDB!");
        }
        lock (dbContextLock)
        {
            _dbContext.Add(item);
            _dbContext.SaveChanges();
            _dbContext.Entry(item).State = EntityState.Detached;
            TotalSuccessCount++;
        }
    }


    /// <summary>
    /// Writes all pending operations now, in one transaction.
    /// Throws <see cref="BatchWriteException"/> if that fails; the operations then stay pending.
    /// </summary>
    public void WriteChanges()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("WriteChanges called without SetupDB!");
        }
        lock (dbContextLock)
        {
            FlushInternal();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                if (_dbContext is not null)
                {
                    lock (dbContextLock)
                    {
                        if (_pending.Count > 0)
                        {
                            // Last resort. A runner should have called WriteChanges before.
                            _logger.LogWarning("ScanJobDBInserts is disposed with {PendingCount} pending operations. Trying to write them now (a runner forgot to call WriteChanges).", _pending.Count);
                            try
                            {
                                FlushInternal();
                            }
#pragma warning disable CA1031 // Dispose must not throw
                            catch (Exception ex)
#pragma warning restore CA1031
                            {
                                _logger.LogError(ex, "The last resort write of {PendingCount} pending operations in Dispose failed. These operations are lost.", _pending.Count);
                            }
                        }
                        _dbContext.Dispose();
                    }
                }
            }

            // TODO: Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
            // TODO: Große Felder auf NULL setzen
            _disposedValue = true;
        }
    }

    // // TODO: Finalizer nur überschreiben, wenn "Dispose(bool disposing)" Code für die Freigabe nicht verwalteter Ressourcen enthält
    // ~ScanRunner()
    // {
    //     // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
