// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Helpers for the tests of the "one row per path" rules of scan (resume), refresh and check: rows are written straight into the database,
/// so a state that older versions produced (several rows for one path) can be created on purpose.
/// </summary>
internal static class RowsPerPathSupport
{
    public static string Sha512Hex(byte[] content) => Convert.ToHexString(SHA512.HashData(content));

    public static ScanItemDto Row(string fullPath, string pathBase, string hash, long size, DateTime mtimeUtc, DateTime lastScanUtc, string computer = "TestPC", Guid? id = null, string origin = "TestPC", string scanName = "TestScan") => new ScanItemDto
    {
        Id = id ?? Guid.NewGuid(),
        FilenameAndPath = fullPath,
        PathBase = pathBase,
        ScanExecutionComputer = computer,
        OriginComputer = origin,
        ScanName = scanName,
        FileSize = size,
        FileSha512Hash = hash,
        FileCreationUTC = mtimeUtc,
        FileLastModificationUTC = mtimeUtc,
        FirstScanDateUTC = lastScanUtc,
        LastScanDateUTC = lastScanUtc,
        LastSha512ScanDateUTC = lastScanUtc,
    };

    /// <summary>Writes the rows into the database (created if needed). The rows are inserted as they are, nothing looks for an existing row of the path.</summary>
    public static void Insert(string databaseFile, params ScanItemDto[] rows)
    {
        using (var db = new ScanJobDBInserts(null))
        {
            db.SetupDB(databaseFile);
            foreach (var row in rows)
            {
                db.Enqueue(row);
            }
            db.WriteChanges();
        }
        SqliteConnection.ClearAllPools();
    }

    public static List<ScanItemDto> ReadRows(string databaseFile)
    {
        List<ScanItemDto> rows;
        using (var context = DubFinderContextFactory.CreateDubFinderContext(databaseFile))
        {
            rows = context.ScanItems!.AsNoTracking().ToList();
        }
        SqliteConnection.ClearAllPools();
        return rows;
    }

    /// <summary>The identity of the rows of "another scan" in the tests of the identity rule (the job's own identity is TestPC / TestScan).</summary>
    public const string OtherOrigin = "OtherPC";

    public const string OtherScanName = "OtherScan";

    /// <summary>A row of another identity (another OriginComputer and ScanName) for the same path.</summary>
    public static ScanItemDto ForeignRow(string fullPath, string pathBase, string hash, long size, DateTime mtimeUtc, DateTime lastScanUtc, string computer = "TestPC") =>
        Row(fullPath, pathBase, hash, size, mtimeUtc, lastScanUtc, computer, null, OtherOrigin, OtherScanName);

    /// <summary>Every column of the row as text (dates as ticks), to compare rows byte for byte.</summary>
    public static string Fingerprint(ScanItemDto r) => string.Join(
        "|",
        r.Id, r.FilenameAndPath, r.Filename, r.PathBase, r.ScanExecutionComputer, r.OriginComputer, r.ScanName, r.FileSize, r.FileCreationUTC.Ticks, r.FileLastModificationUTC.Ticks,
        r.FileSha512Hash, r.FirstScanDateUTC.Ticks, r.LastScanDateUTC.Ticks, r.LastSha512ScanDateUTC.Ticks);

    public static List<string> Fingerprints(IEnumerable<ScanItemDto> rows) => rows.Select(Fingerprint).OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>The fingerprints of all rows of the other identity currently in the database.</summary>
    public static List<string> ForeignFingerprints(string databaseFile) => Fingerprints(ReadRows(databaseFile).Where(r => r.OriginComputer == OtherOrigin && r.ScanName == OtherScanName));

    public static List<ScanItemDto> RowsOfPath(string databaseFile, string fullPath) =>
        ReadRows(databaseFile).Where(r => string.Equals(r.FilenameAndPath, fullPath, StringComparison.Ordinal)).ToList();
}
