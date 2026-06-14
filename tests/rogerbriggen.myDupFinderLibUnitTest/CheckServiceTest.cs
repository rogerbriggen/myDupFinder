// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class CheckServiceTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _basePath;
    private readonly string _dbPath;
    private readonly string _reportDir;
    private readonly IServiceProvider _serviceProvider;

    public CheckServiceTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CheckServiceTest_" + Guid.NewGuid().ToString("N"));
        _basePath = Path.Combine(_tempDir, "data");
        _reportDir = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(_reportDir);
        _dbPath = Path.Combine(_tempDir, "test.db");

        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddTransient<CheckService>();
        services.AddTransient<CheckReportApplier>();
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private static string Sha512Hex(byte[] content)
    {
        using var sha = SHA512.Create();
        return BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "", StringComparison.Ordinal);
    }

    private string CreateFile(string relative, byte[] content, DateTime? mtime = null)
    {
        var fullPath = Path.Combine(_basePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        if (mtime.HasValue)
        {
            File.SetLastWriteTimeUtc(fullPath, mtime.Value);
        }
        return fullPath;
    }

    private void SeedDb(string fullPath, string pathBase, byte[] content, DateTime mtime)
    {
        using var db = new ScanJobDBInserts(null);
        db.SetupDB(_dbPath);
        db.Enqueue(new ScanItemDto
        {
            Id = Guid.NewGuid(),
            FilenameAndPath = fullPath,
            PathBase = pathBase,
            ScanExecutionComputer = "TestPC",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            FileSize = content.Length,
            FileSha512Hash = Sha512Hex(content),
            FileCreationUTC = mtime,
            FileLastModificationUTC = mtime,
            FirstScanDateUTC = mtime,
            LastScanDateUTC = mtime,
            LastSha512ScanDateUTC = mtime,
        });
        db.WriteChanges();
    }

    private string EnsureBasePathTrailingSep(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private MyDupFinderCheckJobDTO BuildJob(bool skipHashCheck = false, bool ignoreBasePath = false, string? overrideBasePath = null) =>
        new()
        {
            ScanJobDTO = new MyDupFinderScanJobDTO
            {
                JobName = "CheckJob",
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = EnsureBasePathTrailingSep(overrideBasePath ?? _basePath),
                DatabaseFile = _dbPath,
                ReportPath = EnsureBasePathTrailingSep(_reportDir),
            },
            SkipHashCheck = skipHashCheck,
            IgnoreBasePath = ignoreBasePath,
        };

    private CheckReportRow[] RunCheckAndRead(MyDupFinderCheckJobDTO job)
    {
        using var service = _serviceProvider.GetRequiredService<CheckService>();
        service.StartCheck(job);

        var reports = Directory.GetFiles(_reportDir, "check-*.csv");
        Assert.Single(reports);
        var (_, rows) = CheckReportCsv.Read(reports[0]);
        return rows.ToArray();
    }

    [Fact]
    public void DetectsBitRotSuspect_WhenSizeAndMtimeMatchButHashDiffers()
    {
        var basePathSep = EnsureBasePathTrailingSep(_basePath);
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var filePath = CreateFile("rot.dat", content, mtime);
        SeedDb(filePath, basePathSep, content, mtime);

        // Mutate the file in-place but preserve size + mtime to simulate bit rot.
        var rotted = new byte[] { 9, 9, 9, 9, 9 };
        File.WriteAllBytes(filePath, rotted);
        File.SetLastWriteTimeUtc(filePath, mtime);

        var rows = RunCheckAndRead(BuildJob());
        Assert.Single(rows);
        var r = rows[0];
        Assert.Equal(CheckCategory.BitRotSuspect, r.Category);
        Assert.Equal(filePath, r.FilenameAndPathDisk);
        Assert.Equal(Sha512Hex(content), r.HashDB);
        Assert.Equal(Sha512Hex(rotted), r.HashDisk);
        Assert.Equal(content.Length, r.FileSizeDB);
        Assert.Equal(rotted.Length, r.FileSizeDisk);
        Assert.False(r.PathMoved);
    }

    [Fact]
    public void DetectsModified_WhenSizeAndHashDiffer()
    {
        var basePathSep = EnsureBasePathTrailingSep(_basePath);
        var content = new byte[] { 1, 2, 3 };
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var filePath = CreateFile("edit.dat", content, mtime);
        SeedDb(filePath, basePathSep, content, mtime);

        var bigger = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        File.WriteAllBytes(filePath, bigger);
        File.SetLastWriteTimeUtc(filePath, mtime.AddDays(1));

        var rows = RunCheckAndRead(BuildJob());
        Assert.Single(rows);
        Assert.Equal(CheckCategory.Modified, rows[0].Category);
        Assert.Equal(Sha512Hex(bigger), rows[0].HashDisk);
    }

    [Fact]
    public void DetectsModifiedNoHashChange_WhenOnlyMtimeChanged()
    {
        var basePathSep = EnsureBasePathTrailingSep(_basePath);
        var content = new byte[] { 1, 2, 3 };
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var filePath = CreateFile("touch.dat", content, mtime);
        SeedDb(filePath, basePathSep, content, mtime);

        // Same content, different mtime - file was touched but content unchanged.
        File.SetLastWriteTimeUtc(filePath, mtime.AddDays(1));

        var rows = RunCheckAndRead(BuildJob());
        Assert.Single(rows);
        Assert.Equal(CheckCategory.ModifiedNoHashChange, rows[0].Category);
        Assert.Equal(rows[0].HashDB, rows[0].HashDisk);
    }

    [Fact]
    public void DetectsMissingOnDisk_AndNewOnDisk()
    {
        var basePathSep = EnsureBasePathTrailingSep(_basePath);
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Seed the DB with a row whose file we never create.
        var ghostPath = Path.Combine(_basePath, "ghost.dat");
        SeedDb(ghostPath, basePathSep, new byte[] { 1 }, mtime);

        // Add a file on disk that has no DB row.
        var freshContent = new byte[] { 9, 9, 9 };
        var freshPath = CreateFile("fresh.dat", freshContent, mtime);

        var rows = RunCheckAndRead(BuildJob());
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, r => r.Category == CheckCategory.MissingOnDisk && r.FilenameAndPathDB == ghostPath);
        var newRow = rows.FirstOrDefault(r => r.Category == CheckCategory.NewOnDisk);
        Assert.NotNull(newRow);
        Assert.Equal(freshPath, newRow!.FilenameAndPathDisk);
        Assert.Equal(Sha512Hex(freshContent), newRow.HashDisk);
    }

    [Fact]
    public void OkFilesAreNotReported()
    {
        var basePathSep = EnsureBasePathTrailingSep(_basePath);
        var content = new byte[] { 1, 2, 3 };
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var filePath = CreateFile("ok.dat", content, mtime);
        SeedDb(filePath, basePathSep, content, mtime);

        var rows = RunCheckAndRead(BuildJob());
        Assert.Empty(rows);
    }

    [Fact]
    public void SkipHashCheck_StillDetectsSizeChange_ButCannotSpotBitRot()
    {
        var basePathSep = EnsureBasePathTrailingSep(_basePath);
        var rotContent = new byte[] { 1, 2, 3, 4, 5 };
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rotPath = CreateFile("rot.dat", rotContent, mtime);
        SeedDb(rotPath, basePathSep, rotContent, mtime);

        // Bit-flip with preserved size + mtime; SkipHashCheck cannot detect this.
        File.WriteAllBytes(rotPath, new byte[] { 9, 9, 9, 9, 9 });
        File.SetLastWriteTimeUtc(rotPath, mtime);

        // A genuine size change should still surface.
        var editContent = new byte[] { 1, 2 };
        var editPath = CreateFile("edit.dat", editContent, mtime);
        SeedDb(editPath, basePathSep, editContent, mtime);
        File.WriteAllBytes(editPath, new byte[] { 1, 2, 3, 4, 5, 6 });
        File.SetLastWriteTimeUtc(editPath, mtime.AddDays(1));

        var rows = RunCheckAndRead(BuildJob(skipHashCheck: true));
        Assert.Single(rows);
        var r = rows[0];
        Assert.Equal(CheckCategory.Modified, r.Category);
        Assert.Equal(editPath, r.FilenameAndPathDisk);
        Assert.Null(r.HashDisk);
    }

    [Fact]
    public void IgnoreBasePath_MatchesMovedFilesByRelativeSuffix()
    {
        // Seed a row pretending it came from a different (old) base path.
        var oldBase = EnsureBasePathTrailingSep(Path.Combine(_tempDir, "OLD"));
        var content = new byte[] { 7, 7, 7 };
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var fakeOldFile = Path.Combine(oldBase, "sub", "moved.dat");
        SeedDb(fakeOldFile, oldBase, content, mtime);

        // Same relative location under the *current* base.
        var newFile = CreateFile(Path.Combine("sub", "moved.dat"), content, mtime);

        var rows = RunCheckAndRead(BuildJob(ignoreBasePath: true));
        Assert.Single(rows);
        var r = rows[0];
        Assert.True(r.PathMoved);
        Assert.Equal(CheckCategory.ModifiedNoHashChange, r.Category);
        Assert.Equal(newFile, r.FilenameAndPathDisk);
        Assert.Equal(fakeOldFile, r.FilenameAndPathDB);
        Assert.Equal(EnsureBasePathTrailingSep(_basePath), r.PathBaseDisk);
        Assert.Equal(oldBase, r.PathBaseDB);
    }

    [Fact]
    public void IgnoreBasePath_ReportsMissingRowsFromOldBasePath()
    {
        var oldBase = EnsureBasePathTrailingSep(Path.Combine(_tempDir, "OLD"));
        var content = new byte[] { 7, 7, 7 };
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var movedOldFile = Path.Combine(oldBase, "sub", "moved.dat");
        SeedDb(movedOldFile, oldBase, content, mtime);
        var missingOldFile = Path.Combine(oldBase, "sub", "missing.dat");
        SeedDb(missingOldFile, oldBase, new byte[] { 8, 8, 8 }, mtime);

        var movedNewFile = CreateFile(Path.Combine("sub", "moved.dat"), content, mtime);

        var rows = RunCheckAndRead(BuildJob(ignoreBasePath: true));

        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, r =>
            r.Category == CheckCategory.ModifiedNoHashChange &&
            r.PathMoved &&
            r.FilenameAndPathDB == movedOldFile &&
            r.FilenameAndPathDisk == movedNewFile);
        Assert.Contains(rows, r =>
            r.Category == CheckCategory.MissingOnDisk &&
            r.FilenameAndPathDB == missingOldFile &&
            r.PathBaseDB == oldBase);
    }

    [Fact]
    public void EndToEnd_CheckThenApplyThenReCheck_LeavesDbInSync()
    {
        var basePathSep = EnsureBasePathTrailingSep(_basePath);
        var mtime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // bitrot + a modified + a new + a missing
        var rotOrig = new byte[] { 1, 2, 3 };
        var rotPath = CreateFile("rot.dat", rotOrig, mtime);
        SeedDb(rotPath, basePathSep, rotOrig, mtime);
        var rotted = new byte[] { 9, 9, 9 };
        File.WriteAllBytes(rotPath, rotted);
        File.SetLastWriteTimeUtc(rotPath, mtime);

        var editOrig = new byte[] { 1, 2 };
        var editPath = CreateFile("edit.dat", editOrig, mtime);
        SeedDb(editPath, basePathSep, editOrig, mtime);
        var edited = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(editPath, edited);
        File.SetLastWriteTimeUtc(editPath, mtime.AddDays(1));

        var freshContent = new byte[] { 5, 5 };
        var freshPath = CreateFile("fresh.dat", freshContent, mtime);

        var ghostPath = Path.Combine(_basePath, "ghost.dat");
        SeedDb(ghostPath, basePathSep, new byte[] { 8 }, mtime);

        var firstRows = RunCheckAndRead(BuildJob());
        Assert.Equal(4, firstRows.Length);

        var reportPath = Directory.GetFiles(_reportDir, "check-*.csv").Single();
        var applier = _serviceProvider.GetRequiredService<CheckReportApplier>();
        applier.Apply(reportPath);

        Assert.Equal(2, applier.UpdatedCount);   // rot + edit
        Assert.Equal(1, applier.InsertedCount);  // fresh
        Assert.Equal(1, applier.RemovedCount);   // ghost
        Assert.Equal(0, applier.ErrorCount);

        // Move the produced report out of the way so the second check writes its own.
        File.Delete(reportPath);

        var secondRows = RunCheckAndRead(BuildJob());
        Assert.Empty(secondRows);

        // Verify DB state directly.
        using var db = new ScanJobDBInserts(null);
        db.SetupDB(_dbPath);
        var items = db.GetAllItemsByBasePath(basePathSep);
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.FilenameAndPath == rotPath && i.FileSha512Hash == Sha512Hex(rotted));
        Assert.Contains(items, i => i.FilenameAndPath == editPath && i.FileSha512Hash == Sha512Hex(edited));
        Assert.Contains(items, i => i.FilenameAndPath == freshPath && i.FileSha512Hash == Sha512Hex(freshContent));
        Assert.DoesNotContain(items, i => i.FilenameAndPath == ghostPath);
    }
}
