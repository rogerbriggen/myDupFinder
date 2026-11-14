// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using Microsoft.Data.Sqlite;
using RogerBriggen.MyDupFinderDB;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class DatabaseDiagnosticsTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public DatabaseDiagnosticsTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DatabaseDiagnosticsTest_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void GetDatabaseDiagnostics_ReturnsJournalModeSynchronousAndMigrationIndexes()
    {
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);

        var diagnostics = dbInserts.GetDatabaseDiagnostics();

        Assert.False(string.IsNullOrWhiteSpace(diagnostics.JournalMode));
        Assert.InRange(diagnostics.Synchronous, 0, 3);
        Assert.Contains("IX_ScanItems_FileSha512Hash", diagnostics.ScanItemsIndexes);
        Assert.Contains("IX_ScanItems_FileSize", diagnostics.ScanItemsIndexes);
        Assert.Contains("IX_ScanItems_Filename", diagnostics.ScanItemsIndexes);
        Assert.Contains("IX_ScanItems_FilenameAndPath", diagnostics.ScanItemsIndexes);
    }

    [Fact]
    public void GetDatabaseDiagnostics_ChangesNothing()
    {
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(_dbPath);

        var first = dbInserts.GetDatabaseDiagnostics();
        var second = dbInserts.GetDatabaseDiagnostics();

        Assert.Equal(first.JournalMode, second.JournalMode);
        Assert.Equal(first.Synchronous, second.Synchronous);
        Assert.Equal(first.ScanItemsIndexes, second.ScanItemsIndexes);
    }

    [Fact]
    public void ToString_ListsAllValues()
    {
        var diagnostics = new DatabaseDiagnostics("wal", 2, new[] { "IX_A", "IX_B" });

        Assert.Equal("journal_mode=wal, synchronous=2 (FULL), ScanItems indexes=[IX_A, IX_B]", diagnostics.ToString());
    }

    [Fact]
    public void GetDatabaseDiagnostics_WithoutSetupDB_Throws()
    {
        using var dbInserts = new ScanJobDBInserts(null);

        Assert.Throws<InvalidOperationException>(() => dbInserts.GetDatabaseDiagnostics());
    }
}
