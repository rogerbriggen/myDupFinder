// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Tests the deferred, batched writing of <see cref="ScanJobDBInserts"/>. The database is always looked at through a
/// second context, because only committed rows are visible there.
/// </summary>
public class ScanJobDBInsertsBatchTest : IDisposable
{
    private const string BasePath = "/base/";
    private readonly string _tempDir;
    private readonly string _dbPath;

    public ScanJobDBInsertsBatchTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScanJobDBInsertsBatchTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static ScanItemDto CreateItem(string name, string hash = "HASH", long size = 100)
    {
        var date = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = BasePath + name,
            PathBase = BasePath,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = size,
            FileSha512Hash = hash,
            FileCreationUTC = date,
            FileLastModificationUTC = date,
            FirstScanDateUTC = date,
            LastScanDateUTC = date,
            LastSha512ScanDateUTC = date,
        };
    }

    private static ScanErrorItemDto CreateErrorItem(string name) => new ScanErrorItemDto(CreateItem(name), new InvalidOperationException("test error"), DateTime.UtcNow);

    private ScanJobDBInserts CreateDb(int batchSize = 500, TimeSpan? flushInterval = null, TimeProvider? timeProvider = null, ILogger<ScanJobDBInserts>? logger = null)
    {
        var db = new ScanJobDBInserts(logger, batchSize, flushInterval, timeProvider);
        db.SetupDB(_dbPath);
        return db;
    }

    /// <summary>Opens a second context on the file: sees only what is committed.</summary>
    private DubFinderContext OpenContext() => DubFinderContextFactory.CreateDubFinderContext(_dbPath);

    private int CommittedItemCount()
    {
        using var context = OpenContext();
        return context.ScanItems!.Count();
    }

    private int CommittedErrorCount()
    {
        using var context = OpenContext();
        return context.ScanErrorItems!.Count();
    }

    [Fact]
    public void Enqueue_BelowBatchSize_IsNotWritten_ReachingBatchSizeWritesAllAndAdvancesCounters()
    {
        using var db = CreateDb(batchSize: 5);

        for (int i = 0; i < 4; i++)
        {
            db.Enqueue(CreateItem($"file{i}"));
        }

        Assert.Equal(0, CommittedItemCount());
        Assert.Equal(4, db.PendingCount);
        Assert.Equal(0, db.TotalSuccessCount);

        db.Enqueue(CreateItem("file4"));

        Assert.Equal(5, CommittedItemCount());
        Assert.Equal(0, db.PendingCount);
        Assert.Equal(5, db.TotalSuccessCount);
        Assert.Equal(0, db.TotalErrorCount);
    }

    [Fact]
    public void Enqueue_ErrorItemsCountAsErrors()
    {
        using var db = CreateDb(batchSize: 3);

        db.Enqueue(CreateItem("a"));
        db.Enqueue(CreateErrorItem("b"));
        db.Enqueue(CreateErrorItem("c"));

        Assert.Equal(1, CommittedItemCount());
        Assert.Equal(2, CommittedErrorCount());
        Assert.Equal(1, db.TotalSuccessCount);
        Assert.Equal(2, db.TotalErrorCount);
    }

    [Fact]
    public void Enqueue_AfterFlushInterval_WritesOnTheNextAddedOperation()
    {
        var clock = new FakeTimeProvider();
        using var db = CreateDb(batchSize: 1000, flushInterval: TimeSpan.FromSeconds(5), timeProvider: clock);

        db.Enqueue(CreateItem("a"));
        clock.Advance(TimeSpan.FromSeconds(4));
        db.Enqueue(CreateItem("b"));
        Assert.Equal(0, CommittedItemCount());

        clock.Advance(TimeSpan.FromSeconds(1));
        db.Enqueue(CreateItem("c"));

        Assert.Equal(3, CommittedItemCount());
        Assert.Equal(0, db.PendingCount);
        Assert.Equal(3, db.TotalSuccessCount);

        // The interval starts again after a flush
        clock.Advance(TimeSpan.FromSeconds(4));
        db.Enqueue(CreateItem("d"));
        Assert.Equal(3, CommittedItemCount());
        Assert.Equal(1, db.PendingCount);
    }

    [Fact]
    public void WriteChanges_FlushesPendingOperations()
    {
        using var db = CreateDb();
        db.Enqueue(CreateItem("a"));
        db.Enqueue(CreateErrorItem("b"));
        Assert.Equal(0, CommittedItemCount());

        db.WriteChanges();

        Assert.Equal(1, CommittedItemCount());
        Assert.Equal(1, CommittedErrorCount());
        Assert.Equal(0, db.PendingCount);
        Assert.Equal(1, db.TotalSuccessCount);
        Assert.Equal(1, db.TotalErrorCount);
    }

    [Fact]
    public void WriteChanges_WithNothingPending_DoesNothing()
    {
        using var db = CreateDb();
        db.WriteChanges();
        Assert.Equal(0, db.TotalSuccessCount);
    }

    [Fact]
    public void Dispose_FlushesPendingOperationsAndLogsWarning()
    {
        var log = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        var db = CreateDb(logger: factory.CreateLogger<ScanJobDBInserts>());
        db.Enqueue(CreateItem("a"));
        db.Enqueue(CreateItem("b"));

        db.Dispose();

        Assert.Equal(2, CommittedItemCount());
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("2 pending operations", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispose_WithNothingPending_LogsNoWarning()
    {
        var log = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        var db = CreateDb(logger: factory.CreateLogger<ScanJobDBInserts>());
        db.Enqueue(CreateItem("a"));
        db.WriteChanges();

        db.Dispose();

        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void MixedBatch_InsertsErrorsTouchUpdateAndRemoveEndUpCorrect()
    {
        using var db = CreateDb();
        var touched = CreateItem("touched", "HASH_T", 10);
        var updated = CreateItem("updated", "HASH_U", 20);
        var removed = CreateItem("removed", "HASH_R", 30);
        var untouched = CreateItem("untouched", "HASH_N", 40);
        db.Enqueue(touched);
        db.Enqueue(updated);
        db.Enqueue(removed);
        db.Enqueue(untouched);
        db.WriteChanges();
        Assert.Equal(4, db.TotalSuccessCount);

        // The way refresh gets them
        var loaded = db.GetAllItemsByBasePath(BasePath);
        Assert.Equal(0, db.TrackedEntityCount);
        var loadedTouched = loaded.Single(i => i.Id == touched.Id);
        var loadedUpdated = loaded.Single(i => i.Id == updated.Id);
        var loadedRemoved = loaded.Single(i => i.Id == removed.Id);

        // Somebody else changes columns directly in the database after we have loaded the rows.
        // A targeted update must not write them back.
        using (var other = OpenContext())
        {
            other.ScanItems!.Where(i => i.Id == touched.Id).ExecuteUpdate(s => s.SetProperty(i => i.FileSize, 999L).SetProperty(i => i.FileSha512Hash, "CHANGED_BY_OTHER"));
            other.ScanItems!.Where(i => i.Id == updated.Id).ExecuteUpdate(s => s.SetProperty(i => i.ScanName, "CHANGED_BY_OTHER").SetProperty(i => i.PathBase, "/other/"));
        }

        var touchDate = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var updateDate = new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        var newModification = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.TouchItem(loadedTouched, touchDate);
        db.UpdateItem(loadedUpdated, 222, newModification, "NEW_HASH", updateDate);
        db.RemoveItem(loadedRemoved);
        db.Enqueue(CreateItem("added", "HASH_A", 50));
        db.Enqueue(CreateErrorItem("failed"));
        Assert.Equal(5, db.PendingCount);

        // Nothing is written before the flush
        using (var before = OpenContext())
        {
            Assert.Equal(4, before.ScanItems!.Count());
            Assert.Equal(0, before.ScanErrorItems!.Count());
        }

        db.WriteChanges();

        Assert.Equal(0, db.PendingCount);
        Assert.Equal(4 + 2, db.TotalSuccessCount); // 4 first inserts + update + added insert
        Assert.Equal(1, db.TotalErrorCount);
        Assert.Equal(0, db.TrackedEntityCount);
        using var check = OpenContext();
        var rows = check.ScanItems!.AsNoTracking().ToList();
        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.Id == removed.Id);
        Assert.Equal(1, check.ScanErrorItems!.Count());

        // Touch: only LastScanDateUTC
        var rowTouched = rows.Single(r => r.Id == touched.Id);
        Assert.Equal(touchDate, rowTouched.LastScanDateUTC);
        Assert.Equal(999, rowTouched.FileSize);
        Assert.Equal("CHANGED_BY_OTHER", rowTouched.FileSha512Hash);

        // Update: only its columns
        var rowUpdated = rows.Single(r => r.Id == updated.Id);
        Assert.Equal(222, rowUpdated.FileSize);
        Assert.Equal("NEW_HASH", rowUpdated.FileSha512Hash);
        Assert.Equal(newModification, rowUpdated.FileLastModificationUTC);
        Assert.Equal(updateDate, rowUpdated.LastScanDateUTC);
        Assert.Equal(updateDate, rowUpdated.LastSha512ScanDateUTC);
        Assert.Equal("CHANGED_BY_OTHER", rowUpdated.ScanName);
        Assert.Equal("/other/", rowUpdated.PathBase);

        // The rest is as it was
        var rowUntouched = rows.Single(r => r.Id == untouched.Id);
        Assert.Equal("HASH_N", rowUntouched.FileSha512Hash);
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), rowUntouched.LastScanDateUTC);
        Assert.Contains(rows, r => r.FilenameAndPath == BasePath + "added" && r.FileSha512Hash == "HASH_A");
    }

    [Fact]
    public void TouchAndUpdate_KeepTheTimestampsTheCallerPassed()
    {
        using var db = CreateDb();
        db.Enqueue(CreateItem("a"));
        db.Enqueue(CreateItem("b"));
        db.WriteChanges();
        var loaded = db.GetAllItemsByBasePath(BasePath).OrderBy(i => i.FilenameAndPath).ToList();
        var dateA = new DateTime(2025, 1, 1, 1, 1, 1, DateTimeKind.Utc);
        var dateB = new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc);

        db.TouchItem(loaded[0], dateA);
        db.TouchItem(loaded[1], dateB);
        db.WriteChanges();

        using var check = OpenContext();
        Assert.Equal(dateA, check.ScanItems!.Single(i => i.Id == loaded[0].Id).LastScanDateUTC);
        Assert.Equal(dateB, check.ScanItems!.Single(i => i.Id == loaded[1].Id).LastScanDateUTC);
    }

    [Fact]
    public void FailingCommit_RollsBackEverything_KeepsOperationsPending_AndThrows()
    {
        var log = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        using var db = CreateDb(batchSize: 4, logger: factory.CreateLogger<ScanJobDBInserts>());
        var existing = CreateItem("existing");
        db.Enqueue(existing);
        db.WriteChanges();
        Assert.Equal(1, db.TotalSuccessCount);

        // The last operation has a primary key that exists already: SQLite rejects it
        var duplicateKey = CreateItem("duplicate");
        duplicateKey.Id = existing.Id;
        db.Enqueue(CreateItem("valid1"));
        db.Enqueue(CreateErrorItem("validError"));
        db.Enqueue(CreateItem("valid2"));
        var ex = Assert.Throws<BatchWriteException>(() => db.Enqueue(duplicateKey));

        Assert.NotNull(ex.InnerException);
        Assert.Equal(4, ex.PendingOperationCount);
        // Everything of the batch was rolled back, also the valid operations
        Assert.Equal(1, CommittedItemCount());
        Assert.Equal(0, CommittedErrorCount());
        Assert.Equal(1, db.TotalSuccessCount);
        Assert.Equal(0, db.TotalErrorCount);
        Assert.Equal(4, db.PendingCount);
        Assert.Equal(0, db.TrackedEntityCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);

        // A retry fails the same way (not with an "already tracked" error) and still writes nothing
        Assert.Throws<BatchWriteException>(() => db.WriteChanges());
        Assert.Equal(1, CommittedItemCount());
        Assert.Equal(4, db.PendingCount);
        Assert.Equal(0, db.TrackedEntityCount);
    }

    [Fact]
    public void FailingCommit_DisposeDoesNotThrow()
    {
        var db = CreateDb();
        var existing = CreateItem("existing");
        db.Enqueue(existing);
        db.WriteChanges();
        var duplicateKey = CreateItem("duplicate");
        duplicateKey.Id = existing.Id;
        db.Enqueue(duplicateKey);

        var exception = Record.Exception(() => db.Dispose());

        Assert.Null(exception);
        Assert.Equal(1, CommittedItemCount());
    }

    [Fact]
    public void AbandonedInstance_LeavesOnlyCommittedRows_AndANewInstanceOpensCleanly()
    {
        var abandoned = CreateDb();
        try
        {
            abandoned.Enqueue(CreateItem("committed1"));
            abandoned.Enqueue(CreateItem("committed2"));
            abandoned.WriteChanges();
            abandoned.Enqueue(CreateItem("pending1"));
            abandoned.Enqueue(CreateItem("pending2"));
            abandoned.Enqueue(CreateErrorItem("pendingError"));
            // No flush, no dispose: like a killed process, the instance is simply not used any more

            using var next = new ScanJobDBInserts(null);
            next.SetupDB(_dbPath);

            var rows = next.GetAllItemsByBasePath(BasePath);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.StartsWith(BasePath + "committed", r.FilenameAndPath, StringComparison.Ordinal));
            Assert.Equal(0, CommittedErrorCount());

            // and it can write
            next.Enqueue(CreateItem("fromNext"));
            next.WriteChanges();
            Assert.Equal(3, CommittedItemCount());
        }
        finally
        {
            abandoned.Dispose();
        }
    }

    [Fact]
    public void ChangeTracker_StaysEmptyOverManyBatches()
    {
        using var db = CreateDb(batchSize: 10);

        for (int i = 0; i < 1000; i++)
        {
            db.Enqueue(CreateItem($"file{i:D4}", $"HASH{i}"));
            Assert.Equal(0, db.TrackedEntityCount);
        }

        Assert.Equal(1000, CommittedItemCount());
        Assert.Equal(1000, db.TotalSuccessCount);
        Assert.Equal(0, db.PendingCount);
    }

    [Fact]
    public void ConstructorRejectsBatchSizeBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanJobDBInserts(null, 0));
    }

    [Fact]
    public void GetAllItemsByScanIdentity_ReturnsDetachedRows()
    {
        using var db = CreateDb();
        db.Enqueue(CreateItem("a"));
        db.Enqueue(CreateItem("b"));
        db.WriteChanges();

        var rows = db.GetAllItemsByScanIdentity("TestPC", "TestScan");

        Assert.Equal(2, rows.Count);
        Assert.Equal(0, db.TrackedEntityCount);
    }

    // ---- Several operations on the same entity in one batch ----

    private static readonly DateTime Date1 = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Date2 = new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Modification = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Writes two rows, loads them detached (the way refresh does) and then changes columns directly in the database,
    /// so a targeted update that falls back to "whole entity modified" would write the stale values back.
    /// </summary>
    private (ScanJobDBInserts Db, ScanItemDto A, ScanItemDto B) SeedAndLoadTwo()
    {
        var db = CreateDb();
        db.Enqueue(CreateItem("a", "HASH_A", 10));
        db.Enqueue(CreateItem("b", "HASH_B", 20));
        db.WriteChanges();
        var loaded = db.GetAllItemsByBasePath(BasePath).OrderBy(i => i.FilenameAndPath).ToList();
        using (var other = OpenContext())
        {
            other.ScanItems!.ExecuteUpdate(s => s.SetProperty(i => i.FileSize, 999L).SetProperty(i => i.FileSha512Hash, "CHANGED_BY_OTHER").SetProperty(i => i.ScanName, "CHANGED_BY_OTHER").SetProperty(i => i.PathBase, "/other/"));
        }
        return (db, loaded[0], loaded[1]);
    }

    private ScanItemDto ReadRow(Guid id)
    {
        using var context = OpenContext();
        return context.ScanItems!.AsNoTracking().Single(i => i.Id == id);
    }

    private static void AssertColumnsOutsideOperationsUntouched(ScanItemDto row)
    {
        Assert.Equal("CHANGED_BY_OTHER", row.ScanName);
        Assert.Equal("/other/", row.PathBase);
    }

    private static void AssertTouchOnly(ScanItemDto row, DateTime lastScan)
    {
        Assert.Equal(lastScan, row.LastScanDateUTC);
        Assert.Equal(999, row.FileSize);
        Assert.Equal("CHANGED_BY_OTHER", row.FileSha512Hash);
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), row.LastSha512ScanDateUTC);
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), row.FileLastModificationUTC);
        AssertColumnsOutsideOperationsUntouched(row);
    }

    private static void AssertUpdated(ScanItemDto row, long size, string hash, DateTime lastScan, DateTime hashScan)
    {
        Assert.Equal(size, row.FileSize);
        Assert.Equal(hash, row.FileSha512Hash);
        Assert.Equal(Modification, row.FileLastModificationUTC);
        Assert.Equal(lastScan, row.LastScanDateUTC);
        Assert.Equal(hashScan, row.LastSha512ScanDateUTC);
        AssertColumnsOutsideOperationsUntouched(row);
    }

    [Fact]
    public void SameInstance_UpdateThenTouch_WritesAllUpdateColumnsAndTheLaterLastScanDate()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;

        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date1);
        db.TouchItem(a, Date2);
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 222, "NEW_HASH", Date2, Date1);
        // Update counts, touch does not
        Assert.Equal(2 + 1, db.TotalSuccessCount);
        Assert.Equal(0, db.TrackedEntityCount);
    }

    [Fact]
    public void SameInstance_TouchThenUpdate_WritesAllUpdateColumns()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;

        db.TouchItem(a, Date1);
        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date2);
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 222, "NEW_HASH", Date2, Date2);
    }

    [Fact]
    public void SameInstance_TouchThenTouch_WritesTheLatest()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;

        db.TouchItem(a, Date1);
        db.TouchItem(a, Date2);
        db.WriteChanges();

        AssertTouchOnly(ReadRow(a.Id), Date2);
    }

    [Fact]
    public void SameInstance_UpdateThenUpdate_WritesTheLatest()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;

        db.UpdateItem(a, 222, Modification, "FIRST", Date1);
        db.UpdateItem(a, 333, Modification, "SECOND", Date2);
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 333, "SECOND", Date2, Date2);
        Assert.Equal(2 + 2, db.TotalSuccessCount);
    }

    [Fact]
    public void SameInstance_TouchOrUpdateThenRemove_DeletesTheRow()
    {
        var (db, a, b) = SeedAndLoadTwo();
        using var _ = db;

        db.TouchItem(a, Date1);
        db.RemoveItem(a);
        db.UpdateItem(b, 222, Modification, "NEW_HASH", Date1);
        db.RemoveItem(b);
        db.WriteChanges();

        Assert.Equal(0, CommittedItemCount());
        Assert.Equal(0, db.PendingCount);
    }

    [Fact]
    public void SameInstance_RemoveThenTouchOrUpdate_RowStaysDeleted_LaterOperationIsIgnored()
    {
        var (db, a, b) = SeedAndLoadTwo();
        using var _ = db;

        db.RemoveItem(a);
        db.TouchItem(a, Date1);
        db.RemoveItem(b);
        db.UpdateItem(b, 222, Modification, "NEW_HASH", Date1);
        db.WriteChanges();

        Assert.Equal(0, CommittedItemCount());
        // The ignored update did not count
        Assert.Equal(2, db.TotalSuccessCount);
    }

    [Fact]
    public void SameInstance_InsertThenTouchOrUpdate_InsertsTheFinalValues()
    {
        using var db = CreateDb();
        var touched = CreateItem("touched", "HASH_T", 10);
        var updated = CreateItem("updated", "HASH_U", 20);
        db.Enqueue(touched);
        db.Enqueue(updated);

        db.TouchItem(touched, Date1);
        db.UpdateItem(updated, 222, Modification, "NEW_HASH", Date2);
        db.WriteChanges();

        var rowTouched = ReadRow(touched.Id);
        Assert.Equal(Date1, rowTouched.LastScanDateUTC);
        Assert.Equal("HASH_T", rowTouched.FileSha512Hash);
        var rowUpdated = ReadRow(updated.Id);
        Assert.Equal(222, rowUpdated.FileSize);
        Assert.Equal("NEW_HASH", rowUpdated.FileSha512Hash);
        Assert.Equal(Modification, rowUpdated.FileLastModificationUTC);
        Assert.Equal(Date2, rowUpdated.LastScanDateUTC);
        Assert.Equal(Date2, rowUpdated.LastSha512ScanDateUTC);
        Assert.Equal("TestScan", rowUpdated.ScanName);
        // Two inserts count, the touch does not; the update after its insert counts again
        Assert.Equal(2 + 1, db.TotalSuccessCount);
        Assert.Equal(0, db.TrackedEntityCount);
    }

    [Fact]
    public void DifferentInstancesSameKey_TouchThenUpdate_LaterWinsPerColumn()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var copy = ReadRow(a.Id);

        db.TouchItem(a, Date1);
        db.UpdateItem(copy, 222, Modification, "NEW_HASH", Date2);
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 222, "NEW_HASH", Date2, Date2);
    }

    [Fact]
    public void DifferentInstancesSameKey_UpdateThenTouch_EarlierColumnsKept()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var copy = ReadRow(a.Id);

        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date1);
        db.TouchItem(copy, Date2);
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 222, "NEW_HASH", Date2, Date1);
    }

    [Fact]
    public void DifferentInstancesSameKey_TouchThenTouch_LatestWins()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var copy = ReadRow(a.Id);

        db.TouchItem(a, Date1);
        db.TouchItem(copy, Date2);
        db.WriteChanges();

        AssertTouchOnly(ReadRow(a.Id), Date2);
    }

    [Fact]
    public void DifferentInstancesSameKey_RemoveWithOtherInstance_DeletesTheRow()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var copy = ReadRow(a.Id);

        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date1);
        db.RemoveItem(copy);
        db.TouchItem(a, Date2);
        db.WriteChanges();

        Assert.Equal(1, CommittedItemCount());
        using var check = OpenContext();
        Assert.DoesNotContain(check.ScanItems!.ToList(), i => i.Id == a.Id);
    }

    [Fact]
    public void DifferentInstancesSameKey_InsertedThenTouchedWithOtherInstance_InsertsRowWithTouchDate()
    {
        using var db = CreateDb();
        var inserted = CreateItem("x", "HASH_X", 10);
        var other = CreateItem("x", "HASH_OTHER", 77);
        other.Id = inserted.Id;
        db.Enqueue(inserted);

        db.TouchItem(other, Date1);
        db.WriteChanges();

        var row = ReadRow(inserted.Id);
        Assert.Equal(Date1, row.LastScanDateUTC);
        Assert.Equal("HASH_X", row.FileSha512Hash);
        Assert.Equal(10, row.FileSize);
    }

    // ---- Values are captured when the operation is queued ----

    private static readonly DateTime Date3 = new DateTime(2024, 4, 4, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Snapshot_UpdateWithTwoInstancesOfTheSameKey_LastUpdateWins()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var b = ReadRow(a.Id);

        db.UpdateItem(a, 111, Modification, "H1", Date1);
        db.UpdateItem(b, 222, Modification, "H2", Date2);
        db.UpdateItem(a, 333, Modification, "H3", Date3);
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 333, "H3", Date3, Date3);
        Assert.Equal(2 + 3, db.TotalSuccessCount);
    }

    [Fact]
    public void Snapshot_TouchWithTwoInstancesOfTheSameKey_LastTouchWins()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var b = ReadRow(a.Id);

        db.TouchItem(a, Date1);
        db.TouchItem(b, Date2);
        db.TouchItem(a, Date3);
        db.WriteChanges();

        AssertTouchOnly(ReadRow(a.Id), Date3);
    }

    [Fact]
    public void Snapshot_UpdateThenDirectChangeOfTheObject_WritesTheValuesOfTheCall()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;

        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date1);
        a.FileSize = 5;
        a.FileSha512Hash = "DIRECT";
        a.FileLastModificationUTC = Date3;
        a.LastScanDateUTC = Date3;
        a.LastSha512ScanDateUTC = Date3;
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 222, "NEW_HASH", Date1, Date1);
    }

    [Fact]
    public void Snapshot_TouchThenDirectChangeOfTheObject_WritesTheDateOfTheCall()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;

        db.TouchItem(a, Date1);
        a.LastScanDateUTC = Date3;
        db.WriteChanges();

        AssertTouchOnly(ReadRow(a.Id), Date1);
    }

    [Fact]
    public void Snapshot_Update_DoesNotBlankOtherColumns()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var creation = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date1);
        db.WriteChanges();

        var row = ReadRow(a.Id);
        Assert.Equal(BasePath + "a", row.FilenameAndPath);
        Assert.Equal("a", row.Filename);
        Assert.Equal("/other/", row.PathBase);
        Assert.Equal("CHANGED_BY_OTHER", row.ScanName);
        Assert.Equal("TestPC", row.OriginComputer);
        Assert.Equal("TestPC", row.ScanExecutionComputer);
        Assert.Equal(creation, row.FileCreationUTC);
        Assert.Equal(creation, row.FirstScanDateUTC);
    }

    [Fact]
    public void Snapshot_FailedBatchThenRetry_WritesTheUpdateExactlyOnce()
    {
        var (db, a, b) = SeedAndLoadTwo();
        using var _ = db;
        // An insert with the key of an existing row makes the batch fail
        var duplicateKey = CreateItem("duplicate");
        duplicateKey.Id = b.Id;

        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date1);
        db.Enqueue(duplicateKey);
        var ex = Assert.Throws<BatchWriteException>(() => db.WriteChanges());

        Assert.Equal(2, ex.PendingOperationCount);
        Assert.Equal(2, db.PendingCount);
        Assert.Equal(2, db.TotalSuccessCount);
        Assert.Equal(0, db.TrackedEntityCount);
        Assert.Equal(999, ReadRow(a.Id).FileSize);

        // The caller changes the object after the failure: the queued snapshot must not change
        a.FileSize = 5;
        // Remove the cause: the row that blocks the insert is deleted directly
        using (var other = OpenContext())
        {
            other.ScanItems!.Where(i => i.Id == b.Id).ExecuteDelete();
        }
        db.WriteChanges();

        AssertUpdated(ReadRow(a.Id), 222, "NEW_HASH", Date1, Date1);
        Assert.Equal(BasePath + "duplicate", ReadRow(b.Id).FilenameAndPath);
        Assert.Equal(0, db.PendingCount);
        // The update and the insert count once each
        Assert.Equal(2 + 2, db.TotalSuccessCount);
        Assert.Equal(0, db.TrackedEntityCount);
    }

    [Fact]
    public void Snapshot_RemoveOfLoadedRow_DeletesByKey()
    {
        var (db, a, b) = SeedAndLoadTwo();
        using var _ = db;

        db.RemoveItem(a);
        db.WriteChanges();

        Assert.Equal(1, CommittedItemCount());
        using var check = OpenContext();
        var remaining = check.ScanItems!.AsNoTracking().ToList();
        Assert.DoesNotContain(remaining, i => i.Id == a.Id);
        Assert.Contains(remaining, i => i.Id == b.Id);
        Assert.Equal(0, db.TrackedEntityCount);
    }

    // ---- Rows that are loaded through the lookups or that are tracked in the context already ----

    public enum Lookup
    {
        ById,
        ByPath,
        BySuffix,
    }

    public enum Operation
    {
        Touch,
        Update,
        Remove,
    }

    private static ScanItemDto LookupA(ScanJobDBInserts db, Lookup lookup, Guid idOfA) => lookup switch
    {
        Lookup.ById => db.GetItemById(idOfA)!,
        Lookup.ByPath => db.GetItemByPath(BasePath + "a")!,
        _ => db.GetItemsByRelativeSuffix("a").Single(),
    };

    private void QueueAndAssert(ScanJobDBInserts db, Operation operation, ScanItemDto a, ScanItemDto b)
    {
        switch (operation)
        {
            case Operation.Touch:
                db.TouchItem(a, Date1);
                break;
            case Operation.Update:
                db.UpdateItem(a, 222, Modification, "NEW_HASH", Date1);
                break;
            default:
                db.RemoveItem(a);
                break;
        }
        Assert.Equal(1, db.PendingCount);

        db.WriteChanges();

        Assert.Equal(0, db.PendingCount);
        Assert.Equal(0, db.TrackedEntityCount);
        switch (operation)
        {
            case Operation.Touch:
                AssertTouchOnly(ReadRow(a.Id), Date1);
                Assert.Equal(2, CommittedItemCount());
                break;
            case Operation.Update:
                AssertUpdated(ReadRow(a.Id), 222, "NEW_HASH", Date1, Date1);
                Assert.Equal(2, CommittedItemCount());
                break;
            default:
                Assert.Equal(1, CommittedItemCount());
                using (var check = OpenContext())
                {
                    Assert.DoesNotContain(check.ScanItems!.AsNoTracking().ToList(), i => i.Id == a.Id);
                }
                break;
        }
        // The other row was not touched
        Assert.Equal("CHANGED_BY_OTHER", ReadRow(b.Id).FileSha512Hash);
    }

    [Theory]
    [InlineData(Lookup.ById, Operation.Touch)]
    [InlineData(Lookup.ById, Operation.Update)]
    [InlineData(Lookup.ById, Operation.Remove)]
    [InlineData(Lookup.ByPath, Operation.Touch)]
    [InlineData(Lookup.ByPath, Operation.Update)]
    [InlineData(Lookup.ByPath, Operation.Remove)]
    [InlineData(Lookup.BySuffix, Operation.Touch)]
    [InlineData(Lookup.BySuffix, Operation.Update)]
    [InlineData(Lookup.BySuffix, Operation.Remove)]
    public void LoadedWithLookup_ThenQueuedOperation_IsWrittenAndNothingStaysTracked(Lookup lookup, Operation operation)
    {
        var (db, seededA, b) = SeedAndLoadTwo();
        using var _ = db;
        var a = LookupA(db, lookup, seededA.Id);
        Assert.Equal(0, db.TrackedEntityCount);

        QueueAndAssert(db, operation, a, b);
    }

    [Theory]
    [InlineData(Operation.Touch)]
    [InlineData(Operation.Update)]
    [InlineData(Operation.Remove)]
    public void AlreadyTrackedEntity_ThenQueuedOperation_IsWrittenAndOtherColumnsAreNotFlagged(Operation operation)
    {
        var (db, seededA, b) = SeedAndLoadTwo();
        using var _ = db;
        var context = db.ContextForTests!;
        var tracked = context.ScanItems!.First(i => i.Id == seededA.Id);
        Assert.Equal(EntityState.Unchanged, context.Entry(tracked).State);
        // Changed without telling EF (change detection is off): must not be written
        tracked.ScanName = "MUTATED_IN_TRACKER";
        tracked.PathBase = "/mutated/";

        QueueAndAssert(db, operation, ReadRow(seededA.Id), b);

        if (operation != Operation.Remove)
        {
            var row = ReadRow(seededA.Id);
            Assert.Equal("CHANGED_BY_OTHER", row.ScanName);
            Assert.Equal("/other/", row.PathBase);
        }
    }

    [Fact]
    public void AlreadyTrackedAsModified_ThenTouch_KeepsEarlierFlagsAndWritesTheTouch()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var context = db.ContextForTests!;
        var tracked = context.ScanItems!.First(i => i.Id == a.Id);
        context.Entry(tracked).Property(i => i.FileSize).CurrentValue = 4711;
        context.Entry(tracked).Property(i => i.FileSize).IsModified = true;

        db.TouchItem(a, Date1);
        db.WriteChanges();

        var row = ReadRow(a.Id);
        Assert.Equal(Date1, row.LastScanDateUTC);
        Assert.Equal(4711, row.FileSize);
        Assert.Equal("CHANGED_BY_OTHER", row.FileSha512Hash);
        AssertColumnsOutsideOperationsUntouched(row);
        Assert.Equal(0, db.TrackedEntityCount);
    }

    [Fact]
    public void AlreadyTrackedAsDeleted_ThenTouchOrUpdate_RowStaysDeleted_AndUpdateDoesNotCount()
    {
        var (db, a, b) = SeedAndLoadTwo();
        using var _ = db;
        var context = db.ContextForTests!;
        context.Remove(context.ScanItems!.First(i => i.Id == a.Id));

        db.TouchItem(a, Date1);
        db.UpdateItem(a, 222, Modification, "NEW_HASH", Date2);
        db.RemoveItem(a);
        db.WriteChanges();

        Assert.Equal(1, CommittedItemCount());
        Assert.Equal("CHANGED_BY_OTHER", ReadRow(b.Id).FileSha512Hash);
        Assert.Equal(2, db.TotalSuccessCount);
        Assert.Equal(0, db.TrackedEntityCount);
        Assert.Equal(0, db.PendingCount);
    }

    [Fact]
    public void InsertItem_WhenItFails_LeavesNothingTracked()
    {
        var (db, a, _) = SeedAndLoadTwo();
        using var _ = db;
        var duplicateKey = CreateItem("duplicate");
        duplicateKey.Id = a.Id;

        Assert.ThrowsAny<Exception>(() => db.InsertItem(duplicateKey));

        Assert.Equal(0, db.TrackedEntityCount);
        db.TouchItem(a, Date1);
        db.WriteChanges();
        AssertTouchOnly(ReadRow(a.Id), Date1);
    }

    // ---- Items without an Id (the way the runners create them) ----

    private static ScanItemDto CreateItemWithoutId(string name, string hash = "HASH", long size = 100)
    {
        var item = CreateItem(name, hash, size);
        item.Id = Guid.Empty;
        return item;
    }

    [Fact]
    public void NoId_Enqueue_AssignsAnIdImmediately_AndTheRowIsStoredUnderIt()
    {
        using var db = CreateDb();
        var item = CreateItemWithoutId("a");

        db.Enqueue(item);

        Assert.NotEqual(Guid.Empty, item.Id);
        var assigned = item.Id;
        db.WriteChanges();
        Assert.Equal(assigned, item.Id);
        Assert.Equal(BasePath + "a", ReadRow(assigned).FilenameAndPath);
        Assert.Equal(1, CommittedItemCount());
    }

    [Fact]
    public void NoId_EnqueueThenTouchAndUpdate_StoresOneRowWithTheFinalValues()
    {
        using var db = CreateDb();
        var touched = CreateItemWithoutId("touched", "HASH_T", 10);
        var updated = CreateItemWithoutId("updated", "HASH_U", 20);
        db.Enqueue(touched);
        db.Enqueue(updated);

        db.TouchItem(touched, Date1);
        db.UpdateItem(updated, 222, Modification, "NEW_HASH", Date2);
        db.WriteChanges();

        Assert.Equal(2, CommittedItemCount());
        var rowTouched = ReadRow(touched.Id);
        Assert.Equal(Date1, rowTouched.LastScanDateUTC);
        Assert.Equal("HASH_T", rowTouched.FileSha512Hash);
        Assert.Equal(BasePath + "touched", rowTouched.FilenameAndPath);
        var rowUpdated = ReadRow(updated.Id);
        Assert.Equal(222, rowUpdated.FileSize);
        Assert.Equal("NEW_HASH", rowUpdated.FileSha512Hash);
        Assert.Equal(Date2, rowUpdated.LastScanDateUTC);
        Assert.Equal(BasePath + "updated", rowUpdated.FilenameAndPath);
    }

    [Fact]
    public void NoId_EnqueueThenRemove_StoresNothing_AndCountsNothing()
    {
        using var db = CreateDb();
        var item = CreateItemWithoutId("a");
        db.Enqueue(item);

        db.RemoveItem(item);
        db.WriteChanges();

        Assert.Equal(0, CommittedItemCount());
        Assert.Equal(0, db.TotalSuccessCount);
        Assert.Equal(0, db.PendingCount);
    }

    [Fact]
    public void InsertThenRemoveOfTheSameKey_InsertsNothing_AndDoesNotCount()
    {
        using var db = CreateDb();
        var item = CreateItem("a");
        db.Enqueue(item);
        db.Enqueue(CreateItem("b"));

        db.RemoveItem(item);
        db.WriteChanges();

        Assert.Equal(1, CommittedItemCount());
        Assert.Equal(1, db.TotalSuccessCount);
    }

    [Fact]
    public void EmptyId_TouchUpdateRemove_ThrowArgumentException_AndQueueNothing()
    {
        using var db = CreateDb();
        var item = CreateItemWithoutId("a");

        Assert.Throws<ArgumentException>(() => db.TouchItem(item, Date1));
        Assert.Throws<ArgumentException>(() => db.UpdateItem(item, 1, Modification, "H", Date1));
        Assert.Throws<ArgumentException>(() => db.RemoveItem(item));

        Assert.Equal(0, db.PendingCount);
        db.WriteChanges();
        Assert.Equal(0, CommittedItemCount());
    }
}
