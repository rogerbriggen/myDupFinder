// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Helpers of the applyCheck tests: seed rows, look at the whole table, write reports by hand and make the database fail on purpose.
/// </summary>
internal static class ApplyTestSupport
{
    public static readonly DateTime Mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public const string OldHash = "AAAA";

    public static ScanItemDto NewItem(string path, string pathBase, string hash = OldHash, long size = 10, DateTime? mtime = null)
    {
        var time = mtime ?? Mtime;
        return new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = path,
            PathBase = pathBase,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = size,
            FileSha512Hash = hash,
            FileCreationUTC = time,
            FileLastModificationUTC = time,
            FirstScanDateUTC = time,
            LastScanDateUTC = time,
            LastSha512ScanDateUTC = time,
        };
    }

    public static ScanItemDto Seed(string dbPath, string path, string pathBase, string hash = OldHash, long size = 10, DateTime? mtime = null)
    {
        var item = NewItem(path, pathBase, hash, size, mtime);
        using var db = new ScanJobDBInserts(null);
        db.SetupDB(dbPath);
        db.Enqueue(item);
        db.WriteChanges();
        return item;
    }

    /// <summary>Creates and migrates an empty database.</summary>
    public static void EnsureDb(string dbPath) => SeedMany(dbPath, Array.Empty<ScanItemDto>());

    public static void SeedMany(string dbPath, IEnumerable<ScanItemDto> items)
    {
        using var db = new ScanJobDBInserts(null);
        db.SetupDB(dbPath);
        foreach (var item in items)
        {
            db.Enqueue(item);
        }
        db.WriteChanges();
    }

    public static List<ScanItemDto> ReadRows(string dbPath)
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(dbPath);
        return context.ScanItems!.AsNoTracking().OrderBy(i => i.FilenameAndPath).ThenBy(i => i.Id).ToList();
    }

    /// <summary>
    /// Every column of every row as text, sorted. With <paramref name="withoutScanDates"/> the two columns that applyCheck sets to "now"
    /// on every update (LastScanDateUTC, LastSha512ScanDateUTC) are left out: applying an update again writes the same values, only these dates move.
    /// </summary>
    public static List<string> Snapshot(string dbPath, bool withoutScanDates = false) =>
        ReadRows(dbPath).Select(i => Describe(i, withoutScanDates)).OrderBy(s => s, StringComparer.Ordinal).ToList();

    public static string Describe(ScanItemDto i, bool withoutScanDates = false)
    {
        var text = string.Join(
            "|",
            i.Id.ToString("D", CultureInfo.InvariantCulture),
            i.FilenameAndPath,
            i.Filename,
            i.PathBase,
            i.ScanExecutionComputer,
            i.OriginComputer,
            i.ScanName,
            i.FileSize.ToString(CultureInfo.InvariantCulture),
            i.FileCreationUTC.Ticks.ToString(CultureInfo.InvariantCulture),
            i.FileLastModificationUTC.Ticks.ToString(CultureInfo.InvariantCulture),
            i.FileSha512Hash,
            i.FirstScanDateUTC.Ticks.ToString(CultureInfo.InvariantCulture));
        if (!withoutScanDates)
        {
            text += "|" + i.LastScanDateUTC.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + i.LastSha512ScanDateUTC.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        return text;
    }

    public static string WriteReport(string reportPath, string dbPath, string basePath, IEnumerable<CheckReportRow> rows, bool skipHashCheck = false)
    {
        using var writer = new StreamWriter(reportPath, append: false);
        CheckReportCsv.WriteHeader(writer, new CheckReportHeader
        {
            DatabaseFile = dbPath,
            BasePath = basePath,
            JobName = "CheckJob",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            SkipHashCheck = skipHashCheck,
        });
        foreach (var row in rows)
        {
            CheckReportCsv.WriteRow(writer, row);
        }
        return reportPath;
    }

    public static CheckReportRow NewOnDiskRow(string path, string pathBase, string? hash, long size = 10, DateTime? mtime = null) =>
        new()
        {
            Category = CheckCategory.NewOnDisk,
            FilenameAndPathDisk = path,
            PathBaseDisk = pathBase,
            FileSizeDisk = size,
            MTimeDisk = mtime ?? Mtime,
            HashDisk = hash,
            CreationTimeDisk = mtime ?? Mtime,
        };

    /// <summary>A Modified row for the seeded item, as a check writes it: the DB columns of the item and the given disk state.</summary>
    public static CheckReportRow ModifiedRow(ScanItemDto item, long sizeDisk, DateTime mtimeDisk, string? hashDisk) =>
        new()
        {
            Category = CheckCategory.Modified,
            ScanItemId = item.Id,
            FilenameAndPathDB = item.FilenameAndPath,
            FilenameAndPathDisk = item.FilenameAndPath,
            PathBaseDB = item.PathBase,
            PathBaseDisk = item.PathBase,
            FileSizeDB = item.FileSize,
            FileSizeDisk = sizeDisk,
            MTimeDB = item.FileLastModificationUTC,
            MTimeDisk = mtimeDisk,
            HashDB = item.FileSha512Hash,
            HashDisk = hashDisk,
        };

    private static void Execute(string dbPath, string sql)
    {
        // Creates and migrates the database when it does not exist yet (the triggers need the table)
        using (var db = new ScanJobDBInserts(null))
        {
            db.SetupDB(dbPath);
        }
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Inserting a row whose path contains "poison" fails at the save (the statement is aborted).</summary>
    public static void InstallFailingInsertTrigger(string dbPath) =>
        Execute(dbPath, "CREATE TRIGGER poison_insert BEFORE INSERT ON ScanItems WHEN NEW.FilenameAndPath LIKE '%poison%' BEGIN SELECT RAISE(ABORT, 'poison insert'); END;");

    /// <summary>Updating a row whose path contains "poison" fails at the save.</summary>
    public static void InstallFailingUpdateTrigger(string dbPath) =>
        Execute(dbPath, "CREATE TRIGGER poison_update BEFORE UPDATE ON ScanItems WHEN OLD.FilenameAndPath LIKE '%poison%' BEGIN SELECT RAISE(ABORT, 'poison update'); END;");

    /// <summary>
    /// Inserting a row whose path contains "poisoncommit" saves fine but the COMMIT of the transaction fails: the trigger writes a child row whose
    /// deferred foreign key has no parent (checked at the commit).
    /// </summary>
    public static void InstallFailingCommitTrigger(string dbPath) =>
        Execute(
            dbPath,
            "CREATE TABLE FkParent (Id INTEGER PRIMARY KEY);"
            + "CREATE TABLE FkChild (ParentId INTEGER REFERENCES FkParent(Id) DEFERRABLE INITIALLY DEFERRED);"
            + "CREATE TRIGGER poison_commit AFTER INSERT ON ScanItems WHEN NEW.FilenameAndPath LIKE '%poisoncommit%' BEGIN INSERT INTO FkChild(ParentId) VALUES (1); END;");

    public static void DropTrigger(string dbPath, string name) => Execute(dbPath, "DROP TRIGGER " + name + ";");
}
