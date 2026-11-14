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

    /// <summary>
    /// One queued write. Inserts keep the object (EF assigns the Id to it). Touch, Update and Remove carry only immutable
    /// values that were captured when the operation was queued, never a reference to the caller's object.
    /// </summary>
    private readonly record struct PendingOperation(
        PendingKind Kind,
        ScanItemDto? Item = null,
        ScanErrorItemDto? ErrorItem = null,
        Guid Id = default,
        long FileSize = 0,
        DateTime FileLastModificationUTC = default,
        string? FileSha512Hash = null,
        DateTime LastScanDateUTC = default,
        DateTime LastSha512ScanDateUTC = default);

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
            DatabaseMigrator.Migrate(_dbContext, _logger, databaseFile);
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
    /// The object is kept by reference, so it must not be changed after it was queued.
    /// If its Id is empty, a new Id is assigned now (when it is queued), so the key is known from this moment and the
    /// row can be touched, updated or removed with this object before the flush.
    /// Throws <see cref="BatchWriteException"/> if this call triggered a flush that failed.
    /// </summary>
    public void Enqueue(ScanItemDto si)
    {
        ArgumentNullException.ThrowIfNull(si);
        if (si.Id == Guid.Empty)
        {
            si.Id = Guid.NewGuid();
        }
        AddPending(new PendingOperation(PendingKind.InsertItem, Item: si), nameof(Enqueue));
    }

    /// <summary>
    /// Adds a scan error item to the pending operations. It reaches the database with the next flush.
    /// The object is kept by reference (EF assigns the Id to it when it is inserted), so it must not be changed after it was queued.
    /// Error items are only ever inserted and never addressed by key afterwards, so their Id is left to EF.
    /// Throws <see cref="BatchWriteException"/> if this call triggered a flush that failed.
    /// </summary>
    public void Enqueue(ScanErrorItemDto sei)
    {
        AddPending(new PendingOperation(PendingKind.InsertError, ErrorItem: sei), nameof(Enqueue));
    }

    /// <summary>
    /// A row without a key can never be addressed, so touch, update and remove fail at the call, not at the flush.
    /// </summary>
    private static void ThrowIfNoKey(ScanItemDto item, string paramName)
    {
        if (item.Id == Guid.Empty)
        {
            throw new ArgumentException("The scan item has no Id (it was never stored or queued with Enqueue), so the row cannot be addressed.", paramName);
        }
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
                            ApplyModification(op, false, trackedById, removedIds);
                            break;
                        case PendingKind.Update:
                            if (ApplyModification(op, true, trackedById, removedIds))
                            {
                                successCount++;
                            }
                            break;
                        case PendingKind.Remove:
                            if (ApplyRemove(op.Id, trackedById, removedIds))
                            {
                                // The row was inserted earlier in this batch and is dropped again: nothing was written
                                successCount--;
                            }
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
    /// Applies a touch (only LastScanDateUTC) or an update (the five update columns) from the values captured in the
    /// operation. One entity is tracked per primary key: if the key is already tracked in this batch (for example because
    /// the row was inserted earlier in the batch), the values are set on that entity and the modified flags are added
    /// (an entity that is Added stays Added and simply gets the values). Otherwise a stub entity that carries only the Id
    /// is attached (state Unchanged), the values are set on it and exactly those properties are flagged as modified, so
    /// EF writes only these columns and never the default values of the other properties.
    /// The change detection is off, so the flags are set explicitly.
    /// Must be called with the lock held. Returns false if the operation was ignored because the row is removed in this batch.
    /// </summary>
    private bool ApplyModification(PendingOperation op, bool fullUpdate, Dictionary<Guid, ScanItemDto> trackedById, HashSet<Guid> removedIds)
    {
        if (removedIds.Contains(op.Id))
        {
            return false;
        }
        if (!trackedById.TryGetValue(op.Id, out var target))
        {
            if (op.Id == Guid.Empty)
            {
                throw new InvalidOperationException("A pending touch or update has no key. It must not be applied, EF would insert a blank row.");
            }
            target = new ScanItemDto { Id = op.Id };
            _dbContext!.Attach(target);
            trackedById[op.Id] = target;
        }

        target.LastScanDateUTC = op.LastScanDateUTC;
        if (fullUpdate)
        {
            target.FileSize = op.FileSize;
            target.FileLastModificationUTC = op.FileLastModificationUTC;
            target.FileSha512Hash = op.FileSha512Hash!;
            target.LastSha512ScanDateUTC = op.LastSha512ScanDateUTC;
        }

        var entry = _dbContext!.Entry(target);
        if (entry.State == EntityState.Added)
        {
            // The row is inserted with the current values of the entity, nothing to flag
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
    /// Applies a remove by primary key. Works on whatever entity is tracked for the key, otherwise on a stub entity
    /// that carries only the Id; operations on the key that come later in the same batch are ignored.
    /// Returns true if the row was inserted earlier in this batch (so nothing is written at all).
    /// Must be called with the lock held.
    /// </summary>
    private bool ApplyRemove(Guid id, Dictionary<Guid, ScanItemDto> trackedById, HashSet<Guid> removedIds)
    {
        if (!removedIds.Add(id))
        {
            return false;
        }
        if (!trackedById.TryGetValue(id, out var target))
        {
            if (id == Guid.Empty)
            {
                throw new InvalidOperationException("A pending remove has no key. It must not be applied, EF would treat the stub as a new row.");
            }
            target = new ScanItemDto { Id = id };
            _dbContext!.Attach(target);
        }
        // Removing an entity that is Added drops it again: nothing is inserted
        bool insertedInThisBatch = _dbContext!.Entry(target).State == EntityState.Added;
        _dbContext.Remove(target);
        return insertedInThisBatch;
    }

    public bool IsEmptyScanItemTable()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("IsEmptyScanItemTable called without SetupDB!");
        }
        lock (dbContextLock)
        {
            return IsEmptyScanItemTable(_dbContext);
        }
    }

    // The queries are static and take the context, so the tests can run them with the real code and look at the SQL and the query plan.

    internal static bool IsEmptyScanItemTable(DubFinderContext context) => !context.Set<ScanItemDto>().Any();

    internal static int CountAlreadyInDB(DubFinderContext context, ScanItemDto si)
    {
        string filenameAndPath = si.FilenameAndPath;
        long fileSize = si.FileSize;
        string scanExecutionComputer = si.ScanExecutionComputer;
        // "FileSize + 0" has the same value as FileSize, but keeps SQLite from using the (much less selective) FileSize index.
        // Without table statistics SQLite chose that index for this query instead of the FilenameAndPath index (see the query plan test).
        return context.Set<ScanItemDto>().Count(s => (s.FilenameAndPath == filenameAndPath) && (s.FileSize + 0 == fileSize) && (s.ScanExecutionComputer == scanExecutionComputer));
    }

    internal static bool IsBasePathAlreadyInDB(DubFinderContext context, string basePath) => context.Set<ScanItemDto>().Any(s => s.PathBase == basePath);

    internal static List<ScanItemDto> GetAllItemsByBasePath(DubFinderContext context, string basePath) => context.Set<ScanItemDto>().AsNoTracking().Where(s => s.PathBase == basePath).ToList();

    internal static List<ScanItemDto> GetAllItemsByScanIdentity(DubFinderContext context, string originComputer, string scanName) =>
        context.Set<ScanItemDto>().AsNoTracking().Where(s => s.OriginComputer == originComputer && s.ScanName == scanName).ToList();

    internal static ScanItemDto? GetItemByPath(DubFinderContext context, string filenameAndPath) => context.Set<ScanItemDto>().FirstOrDefault(s => s.FilenameAndPath == filenameAndPath);

    internal static List<ScanItemDto> GetItemsByRelativeSuffix(DubFinderContext context, string relativeSuffix) => context.Set<ScanItemDto>().Where(s => s.FilenameAndPath.EndsWith(relativeSuffix)).ToList();

    internal static ScanItemDto? GetItemById(DubFinderContext context, Guid id) => context.Set<ScanItemDto>().FirstOrDefault(s => s.Id == id);

    public bool IsAlreadyInDB(ScanItemDto si)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("IsAlreadyInDB called without SetupDB!");
        }
        lock (dbContextLock)
        {
            int itemCount = CountAlreadyInDB(_dbContext, si);
            if (itemCount == 0)
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
            //true if there are already some in the database
            return IsBasePathAlreadyInDB(_dbContext, basePath);
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
            return GetAllItemsByBasePath(_dbContext, basePath);
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
            return GetAllItemsByScanIdentity(_dbContext, originComputer, scanName);
        }
    }

    /// <summary>
    /// Updates an existing scan item in the database (queued, written with the next flush).
    /// Used by refresh when file size or modification date has changed and the hash needs recalculation.
    /// Only FileSize, FileLastModificationUTC, FileSha512Hash, LastScanDateUTC and LastSha512ScanDateUTC are written.
    /// The given object is changed immediately; what is written is captured now, so later changes of the object do not change it.
    /// Several operations on the same row in one batch are applied in the order they were added (the later value wins per column).
    /// If the row is removed earlier in the same batch (<see cref="RemoveItem"/>), this operation is ignored and does not count as success.
    /// </summary>
    public void UpdateItem(ScanItemDto existingItem, long newFileSize, DateTime newFileLastModificationUTC, string newFileSha512Hash, DateTime scanDateUTC)
    {
        ArgumentNullException.ThrowIfNull(existingItem);
        ThrowIfNoKey(existingItem, nameof(existingItem));
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
            AddPending(new PendingOperation(PendingKind.Update, Id: existingItem.Id, FileSize: newFileSize, FileLastModificationUTC: newFileLastModificationUTC, FileSha512Hash: newFileSha512Hash, LastScanDateUTC: scanDateUTC, LastSha512ScanDateUTC: scanDateUTC), nameof(UpdateItem));
        }
    }

    /// <summary>
    /// Updates the LastScanDateUTC of an existing scan item without recalculating the hash (queued, written with the next flush).
    /// Used by refresh when file size and date are unchanged (the file has not changed). Only LastScanDateUTC is written.
    /// The given object is changed immediately; what is written is captured now, so later changes of the object do not change it.
    /// Several operations on the same row in one batch are applied in the order they were added.
    /// If the row is removed earlier in the same batch (<see cref="RemoveItem"/>), this operation is ignored.
    /// </summary>
    public void TouchItem(ScanItemDto existingItem, DateTime scanDateUTC)
    {
        ArgumentNullException.ThrowIfNull(existingItem);
        ThrowIfNoKey(existingItem, nameof(existingItem));
        if (_dbContext is null)
        {
            throw new InvalidOperationException("TouchItem called without SetupDB!");
        }
        lock (dbContextLock)
        {
            existingItem.LastScanDateUTC = scanDateUTC;
            AddPending(new PendingOperation(PendingKind.Touch, Id: existingItem.Id, LastScanDateUTC: scanDateUTC), nameof(TouchItem));
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
        ThrowIfNoKey(item, nameof(item));
        if (_dbContext is null)
        {
            throw new InvalidOperationException("RemoveItem called without SetupDB!");
        }
        lock (dbContextLock)
        {
            AddPending(new PendingOperation(PendingKind.Remove, Id: item.Id), nameof(RemoveItem));
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
            return GetItemByPath(_dbContext, filenameAndPath);
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
            return GetItemsByRelativeSuffix(_dbContext, relativeSuffix);
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
            return GetItemById(_dbContext, id);
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
