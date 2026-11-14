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
}
