// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using Xunit;
using static RogerBriggen.MyDupFinderLibUnitTest.ApplyTestSupport;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The batch transaction of ScanJobDBInserts (BeginBatch / CommitBatch) that applyCheck uses, and the idempotent insert.
/// Key question: what does a failed SaveChanges do inside an open SQLite transaction?
/// </summary>
public class ApplyBatchTransactionTest : IDisposable
{
    private const string Base = "/b/";
    private readonly string _tempDir;
    private readonly string _dbPath;

    public ApplyBatchTransactionTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ApplyBatchTransactionTest_" + Guid.NewGuid().ToString("N"));
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

    private ScanJobDBInserts CreateDb()
    {
        var db = new ScanJobDBInserts(null);
        db.SetupDB(_dbPath);
        return db;
    }

    [Fact]
    public void AFailedSave_InsideTheOpenTransaction_DoesNotEndIt_AndTheRowsBeforeAndAfterAreCommitted()
    {
        using var db = CreateDb();
        var a = NewItem(Base + "a", Base);
        var b = NewItem(Base + "b", Base);
        var duplicateOfA = NewItem(Base + "dup", Base);
        duplicateOfA.Id = a.Id;

        db.BeginBatch();
        db.InsertItem(a);
        Assert.ThrowsAny<Exception>(() => db.InsertItem(duplicateOfA)); // primary key violation at SaveChanges
        // The savepoint of the failed save was rolled back, the earlier write is still there (same connection) and the transaction is open
        Assert.NotNull(db.GetItemById(a.Id));
        db.InsertItem(b);
        Assert.Equal(2, db.BatchWriteCount);
        Assert.Equal(0, db.TrackedEntityCount);
        db.CommitBatch();

        Assert.Equal(new[] { Base + "a", Base + "b" }, ReadRows(_dbPath).Select(r => r.FilenameAndPath));
        Assert.Equal(2, db.TotalSuccessCount);
        Assert.Equal(0, db.TrackedEntityCount);
        Assert.Equal(0, db.BatchWriteCount);
    }

    [Fact]
    public void ABatch_IsOneTransaction_NothingIsVisibleToOthersBeforeTheCommit()
    {
        using var db = CreateDb();
        db.BeginBatch();
        for (int i = 0; i < 5; i++)
        {
            db.InsertItem(NewItem(Base + i, Base));
        }

        Assert.Empty(ReadRows(_dbPath));
        Assert.Equal(0, db.TotalSuccessCount);

        db.CommitBatch();

        Assert.Equal(5, ReadRows(_dbPath).Count);
        Assert.Equal(5, db.TotalSuccessCount);
    }

    [Fact]
    public void UpdatesAndRemovesInABatch_AreCommittedTogether_AndRemovesAreNotSuccessCounts()
    {
        var item = NewItem(Base + "u", Base);
        var gone = NewItem(Base + "gone", Base);
        SeedMany(_dbPath, new[] { item, gone });
        using var db = CreateDb();

        db.BeginBatch();
        db.ApplyUpdateById(item.Id, 99, null, "NEWHASH", DateTime.UtcNow, true, null, null);
        Assert.True(db.RemoveItemById(gone.Id));
        Assert.False(db.RemoveItemById(gone.Id));
        db.CommitBatch();

        var row = Assert.Single(ReadRows(_dbPath));
        Assert.Equal("NEWHASH", row.FileSha512Hash);
        Assert.Equal(99, row.FileSize);
        Assert.Equal(1, db.TotalSuccessCount);
    }

    [Fact]
    public void RollbackBatch_WritesNothing_AndCountsNothing()
    {
        using var db = CreateDb();
        db.BeginBatch();
        db.InsertItem(NewItem(Base + "a", Base));

        db.RollbackBatch();

        Assert.Empty(ReadRows(_dbPath));
        Assert.Equal(0, db.TotalSuccessCount);
        db.RollbackBatch(); // nothing open: no effect
        db.BeginBatch(); // a new batch can be opened
        db.CommitBatch();
    }

    [Fact]
    public void AFailedCommit_RollsTheBatchBack_ThrowsWithTheNumberOfWrites_CountsNothing_AndLeavesNoBatchOpen()
    {
        InstallFailingCommitTrigger(_dbPath);
        using var db = CreateDb();
        db.BeginBatch();
        db.InsertItem(NewItem(Base + "a", Base));
        db.InsertItem(NewItem(Base + "poisoncommit", Base));
        db.InsertItem(NewItem(Base + "c", Base));

        var ex = Assert.Throws<BatchWriteException>(() => db.CommitBatch());

        Assert.Equal(3, ex.PendingOperationCount);
        Assert.Empty(ReadRows(_dbPath));
        Assert.Equal(0, db.TotalSuccessCount);
        Assert.Equal(0, db.TrackedEntityCount);
        Assert.Throws<InvalidOperationException>(() => db.CommitBatch()); // no batch open any more
        db.BeginBatch();
        db.InsertItem(NewItem(Base + "d", Base));
        db.CommitBatch();
        Assert.Single(ReadRows(_dbPath));
        Assert.Equal(1, db.TotalSuccessCount);
    }

    [Fact]
    public void TheQueuedOperations_AndTheBatch_CannotBeMixed()
    {
        using var db = CreateDb();
        db.Enqueue(NewItem(Base + "queued", Base));
        Assert.Throws<InvalidOperationException>(() => db.BeginBatch());
        db.WriteChanges();

        db.BeginBatch();
        Assert.Throws<InvalidOperationException>(() => db.Enqueue(NewItem(Base + "x", Base)));
        Assert.Throws<InvalidOperationException>(() => db.BeginBatch());
        db.RollbackBatch();

        db.Enqueue(NewItem(Base + "queued2", Base));
        db.WriteChanges();
        Assert.Equal(2, ReadRows(_dbPath).Count);
    }

    [Fact]
    public void DisposeWithAnOpenBatch_RollsItBack()
    {
        using (var db = CreateDb())
        {
            db.BeginBatch();
            db.InsertItem(NewItem(Base + "a", Base));
        }
        SqliteConnection.ClearAllPools();

        Assert.Empty(ReadRows(_dbPath));
    }

    [Fact]
    public void InsertItemIfNew_InsertsOnlyWhenNoRowHasThePath()
    {
        using var db = CreateDb();
        var item = NewItem(Base + "a", Base, "AB", 5);

        Assert.Equal(ScanJobDBInserts.InsertIfNewResult.Inserted, db.InsertItemIfNew(item));
        Assert.Equal(ScanJobDBInserts.InsertIfNewResult.AlreadyApplied, db.InsertItemIfNew(NewItem(Base + "a", Base, "ab", 5)));
        Assert.Equal(ScanJobDBInserts.InsertIfNewResult.ConflictingRowExists, db.InsertItemIfNew(NewItem(Base + "a", Base, "AB", 6)));
        Assert.Equal(ScanJobDBInserts.InsertIfNewResult.ConflictingRowExists, db.InsertItemIfNew(NewItem(Base + "a", Base, "CD", 5)));

        Assert.Single(ReadRows(_dbPath));
        Assert.Equal(1, db.TotalSuccessCount);
    }

    [Fact]
    public void InsertItemIfNew_WhenSeveralRowsHaveThePath_AnyRowWithTheSameStateCountsAsApplied()
    {
        // Rows that an older version stored twice
        SeedMany(_dbPath, new[] { NewItem(Base + "a", Base, "AB", 5), NewItem(Base + "a", Base, "CD", 5) });
        using var db = CreateDb();

        Assert.Equal(ScanJobDBInserts.InsertIfNewResult.AlreadyApplied, db.InsertItemIfNew(NewItem(Base + "a", Base, "CD", 5)));
        Assert.Equal(ScanJobDBInserts.InsertIfNewResult.ConflictingRowExists, db.InsertItemIfNew(NewItem(Base + "a", Base, "EF", 5)));
        Assert.Equal(2, ReadRows(_dbPath).Count);
    }
}
