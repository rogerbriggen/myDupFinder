// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Deterministic generator of two databases for tests and the find-dups benchmark. The rows are written straight into the
/// databases (no files on disk). The second database is a changed copy of the first one: same relative path and same content
/// (Duplicate), same relative path and other content (Changed), same content at another path (Moved), a new file, or a copy of
/// a file of the second database itself. Both databases also contain copies of a file inside the same database (duplicate
/// hashes in one database) and a few rows without a hash.
/// </summary>
internal static class FindDupsTestData
{
    public const string BaseRoot = "/bench/base";
    public const string SecondRoot = "/bench/second";

    /// <summary>The first and, before the FilenameAndPath index was added, only migration: a database of an older version.</summary>
    public const string InitialMigration = "20201222171148_InitalCreate";

    public static string HashOf(string content) => Convert.ToHexString(SHA512.HashData(Encoding.ASCII.GetBytes(content)));

    public static Guid IdOf(string key) => new Guid(MD5.HashData(Encoding.ASCII.GetBytes(key)));

    private static string RelativePath(int i) => $"d{i % 97:D2}/s{i / 97 % 53:D2}/file{i:D7}.dat";

    /// <summary>One database row. <paramref name="idKey"/> makes the primary key (the same key always gives the same Guid).</summary>
    public static ScanItemDto Row(string root, string relPath, string hash, long size, string idKey)
    {
        var date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new ScanItemDto
        {
            Id = IdOf(idKey),
            FilenameAndPath = root + "/" + relPath,
            PathBase = root,
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

    /// <summary>Generates the rows of the base and of the second database. Both lists have <paramref name="rowCount"/> rows.</summary>
    public static (List<ScanItemDto> BaseRows, List<ScanItemDto> SecondRows) Generate(int rowCount, int seed = 4711)
    {
        var random = new Random(seed);
        var baseRows = new List<ScanItemDto>(rowCount);
        var baseContent = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            string content;
            double r = random.NextDouble();
            if (r < 0.002)
            {
                content = string.Empty;
            }
            else if (i > 0 && r < 0.15)
            {
                content = baseContent[random.Next(i)];
            }
            else
            {
                content = "base-content-" + i.ToString(CultureInfo.InvariantCulture);
            }
            baseContent[i] = content;
            baseRows.Add(Row(BaseRoot, RelativePath(i), content.Length == 0 ? string.Empty : HashOf(content), 1000 + (i % 5000), "base-" + i.ToString(CultureInfo.InvariantCulture)));
        }

        var secondRows = new List<ScanItemDto>(rowCount);
        var secondContent = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            string relPath = RelativePath(i);
            string content;
            double r = random.NextDouble();
            if (r < 0.002)
            {
                content = string.Empty;
            }
            else if (r < 0.40)
            {
                content = baseContent[i];
            }
            else if (r < 0.48)
            {
                content = "changed-content-" + i.ToString(CultureInfo.InvariantCulture);
            }
            else if (r < 0.60)
            {
                content = baseContent[random.Next(rowCount)];
                relPath = "moved/" + relPath;
            }
            else if (i > 0 && r < 0.72)
            {
                content = secondContent[random.Next(i)];
                relPath = "copies/" + relPath;
            }
            else if (r < 0.80)
            {
                content = "new-content-" + i.ToString(CultureInfo.InvariantCulture);
                relPath = "new/" + relPath;
            }
            else
            {
                content = baseContent[i];
                relPath = "other/" + relPath;
            }
            secondContent[i] = content;
            secondRows.Add(Row(SecondRoot, relPath, content.Length == 0 ? string.Empty : HashOf(content), 1000 + (i % 5000), "second-" + i.ToString(CultureInfo.InvariantCulture)));
        }
        return (baseRows, secondRows);
    }

    /// <summary>
    /// Creates (migrates) a database file and writes the rows in batches, one transaction per batch. With
    /// <paramref name="targetMigration"/> the database is only migrated up to that migration (a database of an older version).
    /// </summary>
    public static void WriteDatabase(string databaseFile, IEnumerable<ScanItemDto> rows, string? targetMigration = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile) ?? string.Empty);
        using var context = DubFinderContextFactory.CreateDubFinderContext(databaseFile);
        if (targetMigration is null)
        {
            context.Database.Migrate();
        }
        else
        {
            context.GetService<IMigrator>().Migrate(targetMigration);
        }
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        int inBatch = 0;
        foreach (var row in rows)
        {
            context.ScanItems!.Add(row);
            if (++inBatch == 5000)
            {
                context.SaveChanges();
                context.ChangeTracker.Clear();
                inBatch = 0;
            }
        }
        context.SaveChanges();
        context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Creates (migrates) a database file and inserts the rows with plain SQL, one row after the other, in exactly the given order, in one
    /// transaction. EF Core does not keep the order of the entities of a SaveChanges (it sorts the inserts by primary key), so a test that
    /// depends on the physical order of the rows (the rowid) needs this method.
    /// </summary>
    public static void WriteDatabaseInOrder(string databaseFile, IEnumerable<ScanItemDto> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile) ?? string.Empty);
        using (var context = DubFinderContextFactory.CreateDubFinderContext(databaseFile))
        {
            context.Database.Migrate();
        }
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection($"Data Source={databaseFile};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO ScanItems (Id, FilenameAndPath, Filename, PathBase, ScanExecutionComputer, OriginComputer, ScanName, FileSize, FileCreationUTC, FileLastModificationUTC, FileSha512Hash, FirstScanDateUTC, LastScanDateUTC, LastSha512ScanDateUTC) " +
            "VALUES ($id, $path, $name, $base, $scanComputer, $originComputer, $scanName, $size, $created, $modified, $hash, $first, $last, $lastHash)";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var pathBase = command.Parameters.Add("$base", SqliteType.Text);
        var scanComputer = command.Parameters.Add("$scanComputer", SqliteType.Text);
        var originComputer = command.Parameters.Add("$originComputer", SqliteType.Text);
        var scanName = command.Parameters.Add("$scanName", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var created = command.Parameters.Add("$created", SqliteType.Text);
        var modified = command.Parameters.Add("$modified", SqliteType.Text);
        var hash = command.Parameters.Add("$hash", SqliteType.Text);
        var first = command.Parameters.Add("$first", SqliteType.Text);
        var last = command.Parameters.Add("$last", SqliteType.Text);
        var lastHash = command.Parameters.Add("$lastHash", SqliteType.Text);
        static string Date(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        foreach (var row in rows)
        {
            // the same text as EF Core writes: the Guid in upper case, the dates as yyyy-MM-dd HH:mm:ss.FFFFFFF
            id.Value = row.Id.ToString("D").ToUpperInvariant();
            path.Value = row.FilenameAndPath;
            name.Value = row.Filename;
            pathBase.Value = row.PathBase;
            scanComputer.Value = row.ScanExecutionComputer;
            originComputer.Value = row.OriginComputer;
            scanName.Value = row.ScanName;
            size.Value = row.FileSize;
            created.Value = Date(row.FileCreationUTC);
            modified.Value = Date(row.FileLastModificationUTC);
            hash.Value = row.FileSha512Hash;
            first.Value = Date(row.FirstScanDateUTC);
            last.Value = Date(row.LastScanDateUTC);
            lastHash.Value = Date(row.LastSha512ScanDateUTC);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Runs PRAGMA journal_mode on the file (the mode of a database file persists) and returns the resulting mode.</summary>
    public static string SetJournalMode(string databaseFile, string mode)
    {
        using var connection = new SqliteConnection($"Data Source={databaseFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=" + mode + ";";
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>Returns the journal mode stored in the file, without changing anything.</summary>
    public static string GetJournalMode(string databaseFile)
    {
        using var connection = new SqliteConnection($"Data Source={databaseFile};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return (string)command.ExecuteScalar()!;
    }
}
