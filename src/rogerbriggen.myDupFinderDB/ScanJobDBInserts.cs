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

/// <summary>What the resume filter found for a file that the scan is about to process.</summary>
public enum ResumeLookupKind
{
    /// <summary>No row for the path (of this ScanExecutionComputer): the file is new, insert it.</summary>
    NotInDatabase,

    /// <summary>A row has the size and the last write time of the file: it is already scanned, nothing to hash.</summary>
    AlreadyScanned,

    /// <summary>There are rows for the path, but none has the size and the last write time of the file: it changed, hash it and update the primary row.</summary>
    Changed,
}

/// <summary>The result of <see cref="ScanJobDBInserts.LookupForResume"/>.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="PrimaryId">The row of the path that stays (the one that matches; for a changed file the one to update). Empty for <see cref="ResumeLookupKind.NotInDatabase"/>.</param>
/// <param name="SurplusIds">All other rows of the path, they are removed.</param>
public sealed record ResumeLookup(ResumeLookupKind Kind, Guid PrimaryId, IReadOnlyList<Guid> SurplusIds)
{
    /// <summary>The result for a path without rows.</summary>
    public static ResumeLookup NotInDatabase { get; } = new ResumeLookup(ResumeLookupKind.NotInDatabase, Guid.Empty, Array.Empty<Guid>());
}

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
    private readonly Func<string, bool> _isNetworkLocation;
    private long _lastFlushTimestamp;
    private readonly object dbContextLock = new object();
    private readonly ILogger<ScanJobDBInserts> _logger;
    private bool _disposedValue;

    // The explicit transaction of BeginBatch/CommitBatch (applyCheck), null when no batch is open. Writes of the batch are counted here and only
    // added to TotalSuccessCount when the commit succeeded.
    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _batchTransaction;
    private int _batchSuccessCount;
    private int _batchWriteCount;
    public int TotalSuccessCount { get; private set; }
    public int TotalErrorCount { get; private set; }


    /// <summary>
    /// Creates the object. Writes are collected and committed in one transaction per batch.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="batchSize">Number of pending operations that triggers a flush (default 500).</param>
    /// <param name="flushInterval">Time since the last flush after which the next added operation triggers a flush (default 5 seconds). There is no timer: this is only checked when an operation is added.</param>
    /// <param name="timeProvider">Clock, for tests. Default is <see cref="TimeProvider.System"/>.</param>
    /// <param name="isNetworkLocation">Decides whether a database file (full path) lies on a network location. Default: <see cref="DatabaseLocation.IsNetworkLocation"/>. For tests.</param>
    public ScanJobDBInserts(ILogger<ScanJobDBInserts>? logger, int batchSize = DefaultBatchSize, TimeSpan? flushInterval = null, TimeProvider? timeProvider = null, Func<string, bool>? isNetworkLocation = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _isNetworkLocation = isNetworkLocation ?? DatabaseLocation.IsNetworkLocation;
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
    /// The context itself, only for tests that need to put an entity into the change tracker on purpose.
    /// </summary>
    internal DubFinderContext? ContextForTests => _dbContext;

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

    /// <summary>The synchronous value that is really used on the connections of this object (after <see cref="SetupDB"/>): the requested value, or Full if Normal had to be ignored.</summary>
    public DatabaseSynchronous EffectiveSynchronous => _connectionSettings?.Synchronous ?? DatabaseSynchronous.Full;

    private DatabaseConnectionSettings? _connectionSettings;

    /// <summary>
    /// Opens (and creates and migrates, if needed) the database.
    /// </summary>
    /// <param name="databaseFile">The database file.</param>
    /// <param name="synchronous">
    /// The requested <c>PRAGMA synchronous</c> value (job setting <c>DatabaseSynchronous</c>). <see cref="DatabaseSynchronous.Normal"/> is only applied when the database is in WAL mode and
    /// not on a network location, otherwise it is ignored with a Warning. The value is set on every connection that EF Core opens, before any command or transaction.
    /// </param>
    public void SetupDB(string databaseFile, DatabaseSynchronous synchronous = DatabaseSynchronous.Full)
    {
        lock (dbContextLock)
        {
            databaseFile = DatabaseLocation.PrepareDatabaseFile(databaseFile);
            bool existed = File.Exists(databaseFile) && new FileInfo(databaseFile).Length > 0;
            bool network = _isNetworkLocation(databaseFile);
            if (!existed && network)
            {
                // EF Core switches a database to WAL only when it creates the file itself, so create the file first, in rollback-journal mode
                DatabaseLocation.CreateEmptyRollbackJournalFile(databaseFile);
            }
            // Full until the journal mode is known: the migration runs with the safe value
            _connectionSettings = new DatabaseConnectionSettings();
            _dbContextOptions = DubFinderContextFactory.GetDbContextOptions(databaseFile, _connectionSettings);
            _dbContext = new DubFinderContext(_dbContextOptions);
            DatabaseMigrator.Migrate(_dbContext, _logger, databaseFile);
            DatabaseModePolicy.Apply(_dbContext, _connectionSettings, synchronous, existed, network, databaseFile, _logger);
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
                // Through EF (not connection.Open()), so the connection settings are applied and the synchronous value that is read is the one the commands really use
                _dbContext.Database.OpenConnection();
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
                    _dbContext.Database.CloseConnection();
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
            if (_batchTransaction is not null)
            {
                throw new InvalidOperationException($"{callerName} called while a batch (BeginBatch) is open. The queue and the batch transaction must not be mixed.");
            }
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
        var insertedIds = new HashSet<Guid>();
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
                            insertedIds.Add(op.Item!.Id);
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
                            if (ApplyRemove(op.Id, trackedById, removedIds, insertedIds))
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
    /// Returns the entity that represents the key in the change tracker, or null if the key is not tracked at all.
    /// Looks in the entities of this batch first, then in everything the context tracks already (a row that was
    /// loaded tracked, or left over). The lookup uses the identity map of the tracker by key: no database query and no
    /// DetectChanges (which is off anyway).
    /// </summary>
    private ScanItemDto? FindTracked(Guid id, Dictionary<Guid, ScanItemDto> trackedById, out bool isDeleted)
    {
        isDeleted = false;
        if (trackedById.TryGetValue(id, out var inBatch))
        {
            return inBatch;
        }
        var entry = _dbContext!.Set<ScanItemDto>().Local.FindEntry(nameof(ScanItemDto.Id), id);
        if (entry is null)
        {
            return null;
        }
        isDeleted = entry.State == EntityState.Deleted;
        return entry.Entity;
    }

    /// <summary>
    /// Applies a touch (only LastScanDateUTC) or an update (the five update columns) from the values captured in the
    /// operation. One entity is used per primary key: if the key is already tracked (because the row was inserted earlier
    /// in the batch, or because the context tracked it before), the values are set on that entity and the modified flags
    /// are added (an entity that is Added stays Added and simply gets the values). If it is tracked as Deleted, the key
    /// counts as removed and the operation is ignored. Otherwise a stub entity that carries only the Id is attached
    /// (state Unchanged), the values are set on it and exactly those properties are flagged as modified, so EF writes
    /// only these columns and never the default values of the other properties.
    /// The change detection is off, so the flags are set explicitly and other properties of a reused entity are not written.
    /// Must be called with the lock held. Returns false if the operation was ignored because the row is removed.
    /// </summary>
    private bool ApplyModification(PendingOperation op, bool fullUpdate, Dictionary<Guid, ScanItemDto> trackedById, HashSet<Guid> removedIds)
    {
        if (removedIds.Contains(op.Id))
        {
            return false;
        }
        var target = FindTracked(op.Id, trackedById, out bool isDeleted);
        if (isDeleted)
        {
            removedIds.Add(op.Id);
            return false;
        }
        if (target is null)
        {
            if (op.Id == Guid.Empty)
            {
                throw new InvalidOperationException("A pending touch or update has no key. It must not be applied, EF would insert a blank row.");
            }
            target = new ScanItemDto { Id = op.Id };
            _dbContext!.Attach(target);
        }
        trackedById[op.Id] = target;

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
    /// Returns true if the row was inserted by this batch (so nothing is written at all).
    /// Must be called with the lock held.
    /// </summary>
    private bool ApplyRemove(Guid id, Dictionary<Guid, ScanItemDto> trackedById, HashSet<Guid> removedIds, HashSet<Guid> insertedIds)
    {
        if (!removedIds.Add(id))
        {
            return false;
        }
        var target = FindTracked(id, trackedById, out bool isDeleted);
        if (isDeleted)
        {
            // Already going to be deleted
            return false;
        }
        if (target is null)
        {
            if (id == Guid.Empty)
            {
                throw new InvalidOperationException("A pending remove has no key. It must not be applied, EF would treat the stub as a new row.");
            }
            target = new ScanItemDto { Id = id };
            _dbContext!.Attach(target);
        }
        // Removing an entity that is Added drops it again: nothing is inserted
        bool insertedInThisBatch = insertedIds.Contains(id) && _dbContext!.Entry(target).State == EntityState.Added;
        _dbContext!.Remove(target);
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

    /// <summary>
    /// The rows of the resume lookup: all rows with this FilenameAndPath that were written by this ScanExecutionComputer (the resume identity of the scan:
    /// the path and the computer that scanned it, nothing else). Not tracked, and only the columns the decision needs: the objects returned are partial
    /// (Id, OriginComputer, ScanName, FileSize, FileLastModificationUTC and LastScanDateUTC are set, everything else is empty; the identity is needed because only rows of the job's
    /// OriginComputer and ScanName may be updated or removed), so they must only be used for <see cref="DecideResume"/>
    /// and <see cref="PrimaryRowChooser"/>. The query has no condition on FileSize on purpose: SQLite uses the FilenameAndPath index (one equality search per file,
    /// see the query plan test), and a condition on FileSize would invite it to use the much less selective FileSize index.
    /// </summary>
    internal static List<ScanItemDto> GetResumeCandidates(DubFinderContext context, string filenameAndPath, string scanExecutionComputer) =>
        context.Set<ScanItemDto>().AsNoTracking()
            .Where(s => s.FilenameAndPath == filenameAndPath && s.ScanExecutionComputer == scanExecutionComputer)
            .Select(s => new ScanItemDto { Id = s.Id, OriginComputer = s.OriginComputer, ScanName = s.ScanName, FileSize = s.FileSize, FileLastModificationUTC = s.FileLastModificationUTC, LastScanDateUTC = s.LastScanDateUTC })
            .ToList();

    /// <summary>
    /// The decision of the resume filter for one file, from the candidate rows of its path (any identity, see <see cref="GetResumeCandidates"/>).
    /// <list type="bullet">
    /// <item>A candidate with the size and the last write time of the file, whatever its identity, means the file is already scanned (the rule of the resume identity: path and
    /// ScanExecutionComputer). Only the rows of the job's identity (<paramref name="originComputer"/>, <paramref name="scanName"/>) are ever updated or removed: if one of them matches, the other
    /// rows of that identity are surplus; if only a row of another identity matches, nothing else is done.</item>
    /// <item>No candidate matches and there are rows of the job's identity: the file changed, the primary row of those rows is updated and their other rows are surplus.</item>
    /// <item>No candidate matches and there is no row of the job's identity (none at all, or only rows of other scans): insert a new row, the rows of the other identities stay as they are.</item>
    /// </list>
    /// </summary>
    internal static ResumeLookup DecideResume(IReadOnlyList<ScanItemDto> rows, long fileSize, DateTime fileLastWriteTimeUtc, string originComputer, string scanName)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var own = new List<ScanItemDto>(rows.Count);
        ScanItemDto? foreignMatch = null;
        foreach (ScanItemDto row in rows)
        {
            if (PrimaryRowChooser.HasIdentity(row, originComputer, scanName))
            {
                own.Add(row);
            }
            else if (foreignMatch is null && PrimaryRowChooser.Matches(row, fileSize, fileLastWriteTimeUtc))
            {
                foreignMatch = row;
            }
        }
        if (own.Count == 0)
        {
            return foreignMatch is null ? ResumeLookup.NotInDatabase : new ResumeLookup(ResumeLookupKind.AlreadyScanned, foreignMatch.Id, Array.Empty<Guid>());
        }
        ScanItemDto primary = PrimaryRowChooser.Choose(own, fileSize, fileLastWriteTimeUtc);
        if (!PrimaryRowChooser.Matches(primary, fileSize, fileLastWriteTimeUtc) && foreignMatch is not null)
        {
            // The file is already scanned by a row of another scan; the stale rows of this job are not touched either
            return new ResumeLookup(ResumeLookupKind.AlreadyScanned, foreignMatch.Id, Array.Empty<Guid>());
        }
        var surplus = new List<Guid>(own.Count - 1);
        foreach (ScanItemDto row in own)
        {
            if (row.Id != primary.Id)
            {
                surplus.Add(row.Id);
            }
        }
        return new ResumeLookup(PrimaryRowChooser.Matches(primary, fileSize, fileLastWriteTimeUtc) ? ResumeLookupKind.AlreadyScanned : ResumeLookupKind.Changed, primary.Id, surplus);
    }

    internal static List<ScanItemDto> GetAllItemsByBasePath(DubFinderContext context, string basePath) => context.Set<ScanItemDto>().AsNoTracking().Where(s => s.PathBase == basePath).ToList();

    internal static List<ScanItemDto> GetAllItemsByScanIdentity(DubFinderContext context, string originComputer, string scanName) =>
        context.Set<ScanItemDto>().AsNoTracking().Where(s => s.OriginComputer == originComputer && s.ScanName == scanName).ToList();

    /// <summary>All rows with exactly this FilenameAndPath (an equality lookup on the FilenameAndPath index). Not tracked.</summary>
    internal static List<ScanItemDto> GetItemsByExactPath(DubFinderContext context, string filenameAndPath) => context.Set<ScanItemDto>().AsNoTracking().Where(s => s.FilenameAndPath == filenameAndPath).ToList();

    internal static ScanItemDto? GetItemByPath(DubFinderContext context, string filenameAndPath) => context.Set<ScanItemDto>().AsNoTracking().FirstOrDefault(s => s.FilenameAndPath == filenameAndPath);

    internal static List<ScanItemDto> GetItemsByRelativeSuffix(DubFinderContext context, string relativeSuffix) => context.Set<ScanItemDto>().AsNoTracking().Where(s => s.FilenameAndPath.EndsWith(relativeSuffix)).ToList();

    internal static ScanItemDto? GetItemById(DubFinderContext context, Guid id) => context.Set<ScanItemDto>().AsNoTracking().FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Number of resume lookups (<see cref="LookupForResume"/>) done so far. Only for tests: a scan into an empty database must not do any,
    /// a resumed scan does exactly one per file.
    /// </summary>
    internal int ResumeLookupCount { get; private set; }

    /// <summary>
    /// The resume filter of the scan: one indexed query for the rows of the path of the item (written by the same ScanExecutionComputer), then the decision with
    /// the size and the last write time that the item carries now (see <see cref="DecideResume"/>). Does not write anything: the caller stores the result of
    /// hashing with <see cref="UpdateItem"/> and <see cref="RemoveItem"/> (changed file) or <see cref="Enqueue(ScanItemDto)"/> (new file).
    /// </summary>
    public ResumeLookup LookupForResume(ScanItemDto si)
    {
        ArgumentNullException.ThrowIfNull(si);
        if (_dbContext is null)
        {
            throw new InvalidOperationException("LookupForResume called without SetupDB!");
        }
        lock (dbContextLock)
        {
            ResumeLookupCount++;
            var rows = GetResumeCandidates(_dbContext, si.FilenameAndPath, si.ScanExecutionComputer);
            return DecideResume(rows, si.FileSize, si.FileLastModificationUTC, si.OriginComputer, si.ScanName);
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
            try
            {
                _dbContext.Entry(existingItem).State = EntityState.Modified;
                _dbContext.SaveChanges();
            }
            finally
            {
                // Also when the save failed: a left over entity would be written (all columns) with the next flush
                _dbContext.Entry(existingItem).State = EntityState.Detached;
            }
            RecordWrite(countsAsSuccess: true);
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
            try
            {
                _dbContext.Remove(existingItem);
                _dbContext.SaveChanges();
            }
            finally
            {
                // Detached after a successful save anyway; after a failed one the entity must not stay as Deleted in the tracker
                _dbContext.Entry(existingItem).State = EntityState.Detached;
            }
            RecordWrite(countsAsSuccess: false);
            return true;
        }
    }

    /// <summary>
    /// Inserts a brand new scan item, used by applyCheck for NewOnDisk rows. Always inserts, see <see cref="InsertItemIfNew"/> for the idempotent version.
    /// </summary>
    public void InsertItem(ScanItemDto item)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("InsertItem called without SetupDB!");
        }
        lock (dbContextLock)
        {
            try
            {
                _dbContext.Add(item);
                _dbContext.SaveChanges();
            }
            finally
            {
                // Also when the save failed: a left over Added entity would be inserted again with the next flush
                _dbContext.Entry(item).State = EntityState.Detached;
            }
            RecordWrite(countsAsSuccess: true);
        }
    }

    /// <summary>
    /// The result of <see cref="InsertItemIfNew"/>.
    /// </summary>
    public enum InsertIfNewResult
    {
        /// <summary>No row had this FilenameAndPath, the row was inserted.</summary>
        Inserted,

        /// <summary>A row with this FilenameAndPath, the same hash, size and modification time exists: the item was applied before. Nothing was written.</summary>
        AlreadyApplied,

        /// <summary>A row with this FilenameAndPath exists, but its hash, size or modification time differ. Nothing was written.</summary>
        ConflictingRowExists,
    }

    /// <summary>
    /// Inserts the item unless a row with exactly its FilenameAndPath exists already (lookup through the FilenameAndPath index).
    /// A row with the same hash (ignoring case), size and last modification time means the item was applied before; a row with other
    /// values means the caller's information is out of date. Both leave the database unchanged. Used by applyCheck so that a report can be applied more than once.
    /// </summary>
    public InsertIfNewResult InsertItemIfNew(ScanItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_dbContext is null)
        {
            throw new InvalidOperationException("InsertItemIfNew called without SetupDB!");
        }
        lock (dbContextLock)
        {
            var existing = GetItemsByExactPath(_dbContext, item.FilenameAndPath);
            if (existing.Count > 0)
            {
                bool sameState = existing.Exists(e =>
                    e.FileSize == item.FileSize
                    && e.FileLastModificationUTC == item.FileLastModificationUTC
                    && string.Equals(e.FileSha512Hash, item.FileSha512Hash, StringComparison.OrdinalIgnoreCase));
                return sameState ? InsertIfNewResult.AlreadyApplied : InsertIfNewResult.ConflictingRowExists;
            }
            InsertItem(item);
            return InsertIfNewResult.Inserted;
        }
    }

    /// <summary>
    /// Opens one explicit transaction. Until <see cref="CommitBatch"/> (or <see cref="RollbackBatch"/>) the direct writes
    /// (<see cref="ApplyUpdateById"/>, <see cref="RemoveItemById"/>, <see cref="InsertItem"/>, <see cref="InsertItemIfNew"/>) are saved inside it, so a whole group of rows costs one commit.
    /// A write that fails inside the batch throws as before; EF rolls back to a savepoint it sets before every save, so the transaction stays open
    /// and the rows written before and after are still committed. Not for the queued operations (Enqueue, TouchItem, UpdateItem, RemoveItem): they
    /// have their own transaction per flush and throw while a batch is open.
    /// </summary>
    public void BeginBatch()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("BeginBatch called without SetupDB!");
        }
        lock (dbContextLock)
        {
            if (_batchTransaction is not null)
            {
                throw new InvalidOperationException("BeginBatch called while a batch is already open.");
            }
            if (_pending.Count > 0)
            {
                throw new InvalidOperationException("BeginBatch called with pending queued operations. Call WriteChanges first.");
            }
            _batchTransaction = _dbContext.Database.BeginTransaction();
            _batchSuccessCount = 0;
            _batchWriteCount = 0;
        }
    }

    /// <summary>
    /// Gets the number of direct writes (updates, inserts, removes) that were saved in the open batch and are not committed yet. 0 when no batch is open.
    /// </summary>
    public int BatchWriteCount
    {
        get
        {
            lock (dbContextLock)
            {
                return _batchWriteCount;
            }
        }
    }

    /// <summary>
    /// Commits the open batch. On success <see cref="TotalSuccessCount"/> advances by the writes of the batch.
    /// On failure the batch is rolled back, nothing of it counts and a <see cref="BatchWriteException"/> is thrown whose
    /// <see cref="BatchWriteException.PendingOperationCount"/> is the number of writes that were lost. No batch is open afterwards in both cases.
    /// </summary>
    public void CommitBatch()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("CommitBatch called without SetupDB!");
        }
        lock (dbContextLock)
        {
            var transaction = _batchTransaction ?? throw new InvalidOperationException("CommitBatch called without an open batch.");
            int writes = _batchWriteCount;
            try
            {
                transaction.Commit();
            }
#pragma warning disable CA1031 // Every failure of the commit has the same consequence
            catch (Exception ex)
#pragma warning restore CA1031
            {
                CloseBatch(transaction);
                _logger.LogError(ex, "Committing a batch of {WriteCount} writes failed. The batch was rolled back.", writes);
                throw new BatchWriteException($"Committing a batch of {writes} writes to the database failed: {ex.Message}", writes, ex);
            }
            TotalSuccessCount += _batchSuccessCount;
            CloseBatch(transaction);
        }
    }

    /// <summary>
    /// Rolls the open batch back. Nothing of it is written and nothing counts. Does nothing when no batch is open.
    /// </summary>
    public void RollbackBatch()
    {
        lock (dbContextLock)
        {
            if (_batchTransaction is not null)
            {
                CloseBatch(_batchTransaction);
            }
        }
    }

    /// <summary>
    /// Ends the batch state. Disposing a transaction that was not committed rolls it back. Must be called with the lock held.
    /// </summary>
    private void CloseBatch(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        _batchTransaction = null;
        _batchSuccessCount = 0;
        _batchWriteCount = 0;
        try
        {
            transaction.Dispose();
        }
#pragma warning disable CA1031 // The batch is over either way
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Closing the batch transaction failed.");
        }
        _dbContext?.ChangeTracker.Clear();
    }

    /// <summary>
    /// A direct write was saved. Inside a batch it is only counted at the commit. Must be called with the lock held.
    /// </summary>
    private void RecordWrite(bool countsAsSuccess)
    {
        if (_batchTransaction is not null)
        {
            _batchWriteCount++;
            if (countsAsSuccess)
            {
                _batchSuccessCount++;
            }
        }
        else if (countsAsSuccess)
        {
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
                        if (_batchTransaction is not null)
                        {
                            _logger.LogWarning("ScanJobDBInserts is disposed with an open batch ({WriteCount} writes). The batch is rolled back.", _batchWriteCount);
                            RollbackBatch();
                        }
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
