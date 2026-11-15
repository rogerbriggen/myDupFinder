// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Tests that change the current directory of the process. The directory is process-wide, so these tests must not run
/// in parallel with any other test: DisableParallelization makes the collection run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "ProcessWideCurrentDirectory";
}

[Collection(CurrentDirectoryCollection.Name)]
public class DatabaseLocationTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _savedCurrentDirectory;

    public DatabaseLocationTest()
    {
        _savedCurrentDirectory = Environment.CurrentDirectory;
        _tempDir = Path.Combine(Path.GetTempPath(), "DatabaseLocationTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _savedCurrentDirectory;
        SqliteConnection.ClearAllPools();
        Directory.Delete(_tempDir, true);
    }

    /// <summary>Runs the action with the temporary directory as current directory and always restores the old one.</summary>
    private void InTempDirectory(Action action)
    {
        string old = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempDir;
        try
        {
            action();
        }
        finally
        {
            Environment.CurrentDirectory = old;
        }
    }

    private static ScanItemDto Item(string path) => new ScanItemDto
    {
        Id = Guid.NewGuid(),
        FilenameAndPath = path,
        PathBase = "/b",
        ScanExecutionComputer = "pc",
        OriginComputer = "pc",
        ScanName = "scan",
        FileSize = 1,
        FileSha512Hash = "H",
        FileCreationUTC = DateTime.UtcNow,
        FileLastModificationUTC = DateTime.UtcNow,
        FirstScanDateUTC = DateTime.UtcNow,
        LastScanDateUTC = DateTime.UtcNow,
        LastSha512ScanDateUTC = DateTime.UtcNow,
    };

    private static void SetupWithInserts(string databaseFile)
    {
        using var db = new ScanJobDBInserts(null);
        db.SetupDB(databaseFile);
        db.Enqueue(Item("/b/one.txt"));
        db.WriteChanges();
    }

    private static int CountRows(string fullPath)
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(fullPath);
        return context.ScanItems!.Count();
    }

    // ----- bare file name -----

    [Fact]
    public void ScanJobDBInserts_SetupDB_BareFileName_CreatesTheFileInTheCurrentDirectory()
    {
        InTempDirectory(() => SetupWithInserts("scan.db"));

        Assert.True(File.Exists(Path.Combine(_tempDir, "scan.db")));
        Assert.Equal(1, CountRows(Path.Combine(_tempDir, "scan.db")));
    }

    [Fact]
    public void DubFinderDB_SetupDB_BareFileName_CreatesTheFileInTheCurrentDirectory()
    {
        InTempDirectory(() =>
        {
            using var db = new DubFinderDB(null);
            db.SetupDB("dups.db");
            Assert.NotNull(db.GetAllScanItems());
        });

        Assert.True(File.Exists(Path.Combine(_tempDir, "dups.db")));
    }

    [Fact]
    public void SetupDB_BareFileName_KeepsTheResolvedFullPath_WhenTheCurrentDirectoryChangesAfterwards()
    {
        // the file SQLite opens is the full path that was resolved in SetupDB, also if the current directory changes afterwards
        using var db = new ScanJobDBInserts(null);
        InTempDirectory(() => db.SetupDB("later.db"));

        db.Enqueue(Item("/b/one.txt"));
        db.WriteChanges();

        Assert.Equal(1, CountRows(Path.Combine(_tempDir, "later.db")));
        Assert.False(File.Exists(Path.Combine(_savedCurrentDirectory, "later.db")));
    }

    // ----- relative path with a directory that does not exist yet -----

    [Fact]
    public void ScanJobDBInserts_SetupDB_RelativePathWithNewDirectories_CreatesTheDirectoriesBelowTheCurrentDirectory()
    {
        InTempDirectory(() => SetupWithInserts(Path.Combine("sub", "deeper", "scan.db")));

        Assert.True(File.Exists(Path.Combine(_tempDir, "sub", "deeper", "scan.db")));
    }

    [Fact]
    public void DubFinderDB_SetupDB_RelativePathWithNewDirectories_CreatesTheDirectoriesBelowTheCurrentDirectory()
    {
        InTempDirectory(() =>
        {
            using var db = new DubFinderDB(null);
            db.SetupDB(Path.Combine("sub", "deeper", "dups.db"));
        });

        Assert.True(File.Exists(Path.Combine(_tempDir, "sub", "deeper", "dups.db")));
    }

    [Fact]
    public void SetupDB_RelativePathWithParentDirectory_ResolvesAgainstTheCurrentDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "cwd"));
        string old = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Path.Combine(_tempDir, "cwd");
        try
        {
            SetupWithInserts(Path.Combine("..", "up", "scan.db"));
        }
        finally
        {
            Environment.CurrentDirectory = old;
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, "up", "scan.db")));
    }

    // ----- absolute path: as before -----

    [Fact]
    public void SetupDB_AbsolutePath_CreatesMissingDirectories_AndDoesNotDependOnTheCurrentDirectory()
    {
        string scanDb = Path.Combine(_tempDir, "new1", "new2", "scan.db");
        string dupsDb = Path.Combine(_tempDir, "new3", "dups.db");

        SetupWithInserts(scanDb);
        using (var db = new DubFinderDB(null))
        {
            db.SetupDB(dupsDb);
        }

        Assert.True(File.Exists(scanDb));
        Assert.True(File.Exists(dupsDb));
        Assert.Equal(1, CountRows(scanDb));
    }

    [Fact]
    public void SetupDB_AbsolutePathInExistingDirectory_Works_AndAnExistingDatabaseIsOpenedNotReplaced()
    {
        string path = Path.Combine(_tempDir, "scan.db");
        SetupWithInserts(path);
        SetupWithInserts(path);

        Assert.Equal(2, CountRows(path));
    }

    // ----- special characters in the path (connection string) -----

    [Theory]
    [InlineData("with space")]
    [InlineData("semi;colon")]
    [InlineData("equals=sign")]
    [InlineData("single'quote")]
    [InlineData("double\"quote")]
    [InlineData("a;b=c'd e")]
    [InlineData("café über 日本")]
    public void SetupDB_PathWithSpecialCharacters_OpensTheFileAtThatPath(string directoryName)
    {
        if (directoryName.Contains('"') && OperatingSystem.IsWindows())
        {
            return; // '"' is not allowed in a Windows file name
        }
        string dir = Path.Combine(_tempDir, directoryName);
        string scanDb = Path.Combine(dir, "scan " + directoryName + ".db");
        string dupsDb = Path.Combine(dir, "dups " + directoryName + ".db");

        SetupWithInserts(scanDb);
        using (var db = new DubFinderDB(null))
        {
            db.SetupDB(dupsDb);
            Assert.NotNull(db.GetAllScanItems());
        }

        Assert.True(File.Exists(scanDb));
        Assert.True(File.Exists(dupsDb));
        Assert.Equal(1, CountRows(scanDb));
        // nothing was created at a wrongly cut path (SQLite side files like -wal or -journal start with the name of their database)
        Assert.All(Directory.GetFiles(dir), f => Assert.True(Path.GetFileName(f).StartsWith("scan ", StringComparison.Ordinal) || Path.GetFileName(f).StartsWith("dups ", StringComparison.Ordinal), f));
        Assert.Empty(Directory.GetDirectories(dir));
    }

    [Fact]
    public void SetupDB_BareFileNameWithSpecialCharacters_Works()
    {
        InTempDirectory(() => SetupWithInserts("it's; a=b.db"));

        Assert.True(File.Exists(Path.Combine(_tempDir, "it's; a=b.db")));
    }

    [Fact]
    public void GetDbContextOptions_QuotesThePath()
    {
        var options = DubFinderContextFactory.GetDbContextOptions("x;y=z'w.db");
        using var context = new DubFinderContext(options);
        var connectionString = context.Database.GetConnectionString();
        var builder = new SqliteConnectionStringBuilder(connectionString);

        Assert.Equal("x;y=z'w.db", builder.DataSource);
    }
}
