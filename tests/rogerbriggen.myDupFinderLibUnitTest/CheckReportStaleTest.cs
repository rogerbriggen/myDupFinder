// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using static RogerBriggen.MyDupFinderLibUnitTest.ApplyTestSupport;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// applyCheck with report schema version 2: a report only changes or removes a row that is still in the state the check saw (the "_DB" columns),
/// a row that already has the state of the report counts as applied, every other row is skipped as stale. Version 1 reports are rejected.
/// </summary>
public class CheckReportStaleTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _basePath;
    private readonly string _dbPath;
    private readonly string _reportDir;
    private readonly List<IDisposable> _disposables = new List<IDisposable>();

    public CheckReportStaleTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CheckReportStaleTest_" + Guid.NewGuid().ToString("N"));
        _basePath = Path.Combine(_tempDir, "data") + Path.DirectorySeparatorChar;
        _reportDir = Path.Combine(_tempDir, "reports");
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(_reportDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
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

    private string P(string name) => Path.Combine(_basePath, name);

    private static string Sha512Hex(byte[] content) => Convert.ToHexString(SHA512.HashData(content));

    private (CheckReportApplier Applier, ListLoggerProvider Log) CreateApplier(ListLoggerProvider? log = null)
    {
        log ??= new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(log);
        });
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        return (new CheckReportApplier(provider.GetService<ILogger<CheckReportApplier>>(), provider), log);
    }

    private CheckReportApplier Apply(string reportPath, out ListLoggerProvider log)
    {
        var (applier, logProvider) = CreateApplier();
        applier.Apply(reportPath);
        log = logProvider;
        return applier;
    }

    private string Report(string name, IEnumerable<CheckReportRow> rows, bool skipHash = false) => WriteReport(Path.Combine(_reportDir, name), _dbPath, _basePath, rows, skipHash);

    private static void AssertStaleWarning(ListLoggerProvider log, string path)
    {
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("NOT applied", StringComparison.Ordinal));
        Assert.Contains(path, warning.Message, StringComparison.Ordinal);
        Assert.Contains("changed", warning.Message, StringComparison.Ordinal);
        Assert.Contains("after the report was written", warning.Message, StringComparison.Ordinal);
        Assert.Contains("run check again", warning.Message, StringComparison.Ordinal);
    }

    // ---------- update rows ----------

    [Fact]
    public void UpdateRow_WhoseRowWasChangedAfterTheCheck_IsStale_AndTheRowIsNotTouched()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var report = Report("r.csv", new[] { ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC") });
        // A refresh (or another applyCheck, or a resumed scan) changed the row after the report was written
        ChangeRow(_dbPath, item.Id, 55, Mtime.AddDays(9), "REFRESHED");
        var before = Snapshot(_dbPath);

        var applier = Apply(report, out var log);

        Assert.Equal(0, applier.UpdatedCount);
        Assert.Equal(0, applier.AlreadyAppliedCount);
        Assert.Equal(1, applier.SkippedCount);
        Assert.Equal(1, applier.SkippedStaleCount);
        Assert.Equal(0, applier.ErrorCount);
        Assert.Equal(before, Snapshot(_dbPath));
        AssertStaleWarning(log, P("a.dat"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("stale=1", StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateRow_WhoseRowWasRemovedAfterTheCheck_IsStale_NotAnError()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var other = Seed(_dbPath, P("b.dat"), _basePath);
        var report = Report("r.csv", new[] { ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC") });
        using (var db = new ScanJobDBInserts(null))
        {
            db.SetupDB(_dbPath);
            Assert.True(db.RemoveItemById(item.Id));
        }
        var before = Snapshot(_dbPath);

        var applier = Apply(report, out var log);

        Assert.Equal(0, applier.ErrorCount);
        Assert.Equal(1, applier.SkippedStaleCount);
        Assert.Equal(1, applier.SkippedCount);
        Assert.Equal(before, Snapshot(_dbPath));
        Assert.Equal(other.Id, Assert.Single(ReadRows(_dbPath)).Id);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("removed", StringComparison.Ordinal) && e.Message.Contains(P("a.dat"), StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateRow_AppliedTwice_TheSecondTimeIsAlreadyApplied_AndNothingIsWritten()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var report = Report("r.csv", new[] { ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC") });

        var first = Apply(report, out _);
        Assert.Equal(1, first.UpdatedCount);
        var afterFirst = Snapshot(_dbPath); // including LastScanDateUTC and LastSha512ScanDateUTC
        Thread.Sleep(20);

        var second = Apply(report, out _);

        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.AlreadyAppliedCount);
        Assert.Equal(0, second.SkippedCount);
        Assert.Equal(0, second.SkippedStaleCount);
        Assert.Equal(afterFirst, Snapshot(_dbPath)); // LastScanDateUTC was not bumped
    }

    [Fact]
    public void UpdateRow_HashInTheReportDifferentInCaseOnly_IsStillTheSameRow()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath, hash: "ABCDEF");
        var row = ModifiedRow(item, 77, Mtime.AddDays(1), "ccdd");
        row.HashDB = "abcdef";

        var applier = Apply(Report("r.csv", new[] { row }), out _);

        Assert.Equal(1, applier.UpdatedCount);
        Assert.Equal("ccdd", Assert.Single(ReadRows(_dbPath)).FileSha512Hash);
    }

    [Fact]
    public void BitRotSuspectRow_WhoseHashWasChangedMeanwhile_IsStale()
    {
        var item = Seed(_dbPath, P("rot.dat"), _basePath);
        var rot = ModifiedRow(item, item.FileSize, item.FileLastModificationUTC, "BBBB");
        rot.Category = CheckCategory.BitRotSuspect;
        var report = Report("r.csv", new[] { rot });
        ChangeRow(_dbPath, item.Id, item.FileSize, item.FileLastModificationUTC, "SOMEONEELSE");
        var before = Snapshot(_dbPath);

        var applier = Apply(report, out var log);

        Assert.Equal(0, applier.UpdatedCount);
        Assert.Equal(1, applier.SkippedStaleCount);
        Assert.Equal(before, Snapshot(_dbPath));
        Assert.Equal("SOMEONEELSE", Assert.Single(ReadRows(_dbPath)).FileSha512Hash);
        AssertStaleWarning(log, P("rot.dat"));
    }

    [Fact]
    public void BitRotSuspectRow_AppliedTwice_TheSecondTimeIsAlreadyApplied()
    {
        var item = Seed(_dbPath, P("rot.dat"), _basePath);
        var rot = ModifiedRow(item, item.FileSize, item.FileLastModificationUTC, "BBBB");
        rot.Category = CheckCategory.BitRotSuspect;
        var report = Report("r.csv", new[] { rot });
        Assert.Equal(1, Apply(report, out _).UpdatedCount);
        var afterFirst = Snapshot(_dbPath);

        var second = Apply(report, out _);

        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.AlreadyAppliedCount);
        Assert.Equal(afterFirst, Snapshot(_dbPath));
    }

    [Fact]
    public void UpdateRowWithPathMove_AppliedTwice_AndStaleWhenThePathWasChangedAgain()
    {
        string oldBase = Path.Combine(_tempDir, "OLD") + Path.DirectorySeparatorChar;
        var item = Seed(_dbPath, Path.Combine(oldBase, "m.dat"), oldBase);
        var row = ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC");
        row.PathMoved = true;
        row.FilenameAndPathDisk = P("m.dat");
        row.PathBaseDisk = _basePath;
        var report = Report("r.csv", new[] { row });
        Assert.Equal(1, Apply(report, out _).UpdatedCount);
        var afterFirst = Snapshot(_dbPath);
        Assert.Equal(P("m.dat"), Assert.Single(ReadRows(_dbPath)).FilenameAndPath);

        var second = Apply(report, out _);
        Assert.Equal(1, second.AlreadyAppliedCount);
        Assert.Equal(afterFirst, Snapshot(_dbPath));

        // The same size, time and hash but another path: not the state of the report
        ChangeRow(_dbPath, item.Id, 77, Mtime.AddDays(1), "CCCC", P("elsewhere.dat"), _basePath);
        var before = Snapshot(_dbPath);
        var third = Apply(report, out _);
        Assert.Equal(1, third.SkippedStaleCount);
        Assert.Equal(before, Snapshot(_dbPath));
    }

    // ---------- pure path moves (no hash) ----------

    [Fact]
    public void PathMoveOnly_Fresh_IsApplied_AlreadyApplied_AndStale()
    {
        string oldBase = Path.Combine(_tempDir, "OLD") + Path.DirectorySeparatorChar;
        var item = Seed(_dbPath, Path.Combine(oldBase, "m.dat"), oldBase);
        var report = Report("move.csv", new[] { PathMoveRow(item, P("m.dat"), _basePath) }, skipHash: true);

        // fresh
        var first = Apply(report, out _);
        Assert.Equal(1, first.UpdatedCount);
        var stored = Assert.Single(ReadRows(_dbPath));
        Assert.Equal(P("m.dat"), stored.FilenameAndPath);
        Assert.Equal(_basePath, stored.PathBase);
        Assert.Equal(item.FileSha512Hash, stored.FileSha512Hash);
        Assert.Equal(item.LastSha512ScanDateUTC, stored.LastSha512ScanDateUTC);
        var afterFirst = Snapshot(_dbPath);

        // already applied
        Thread.Sleep(20);
        var second = Apply(report, out _);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.AlreadyAppliedCount);
        Assert.Equal(0, second.SkippedCount);
        Assert.Equal(afterFirst, Snapshot(_dbPath));

        // stale: a second database where the row was changed (size, time and hash) after the check
        string otherDb = Path.Combine(_tempDir, "other.db");
        var item2 = Seed(otherDb, Path.Combine(oldBase, "m.dat"), oldBase);
        var report2 = WriteReport(Path.Combine(_reportDir, "move2.csv"), otherDb, _basePath, new[] { PathMoveRow(item2, P("m.dat"), _basePath) }, skipHashCheck: true);
        ChangeRow(otherDb, item2.Id, 99, Mtime.AddDays(4), "REFRESHED");
        var before = Snapshot(otherDb);
        var third = Apply(report2, out var log);
        Assert.Equal(0, third.UpdatedCount);
        Assert.Equal(1, third.SkippedStaleCount);
        Assert.Equal(1, third.SkippedCount);
        Assert.Equal(0, third.SkippedNoHashCount);
        Assert.Equal(before, Snapshot(otherDb));
        AssertStaleWarning(log, Path.Combine(oldBase, "m.dat"));
    }

    // ---------- MissingOnDisk ----------

    [Fact]
    public void MissingOnDiskRow_WhoseRowWasChangedAfterTheCheck_IsNotRemoved_AndIsStale()
    {
        var item = Seed(_dbPath, P("gone.dat"), _basePath);
        var report = Report("r.csv", new[] { MissingRow(item) });
        ChangeRow(_dbPath, item.Id, 55, Mtime.AddDays(9), "REFRESHED");
        var before = Snapshot(_dbPath);

        var applier = Apply(report, out var log);

        Assert.Equal(0, applier.RemovedCount);
        Assert.Equal(1, applier.SkippedStaleCount);
        Assert.Equal(1, applier.SkippedCount);
        Assert.Equal(before, Snapshot(_dbPath));
        AssertStaleWarning(log, P("gone.dat"));
    }

    [Fact]
    public void MissingOnDiskRow_AppliedTwice_TheSecondTimeIsSkippedAsBefore()
    {
        var item = Seed(_dbPath, P("gone.dat"), _basePath);
        var keep = Seed(_dbPath, P("keep.dat"), _basePath);
        var report = Report("r.csv", new[] { MissingRow(item) });

        var first = Apply(report, out _);
        Assert.Equal(1, first.RemovedCount);
        Assert.Equal(keep.Id, Assert.Single(ReadRows(_dbPath)).Id);

        var second = Apply(report, out var log);

        Assert.Equal(0, second.RemovedCount);
        Assert.Equal(1, second.SkippedCount);
        Assert.Equal(0, second.SkippedStaleCount);
        Assert.Equal(0, second.ErrorCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not found in DB", StringComparison.Ordinal));
    }

    [Fact]
    public void SurplusRow_ReportedAsMissing_IsNotRemovedWhenItWasUpdatedMeanwhile()
    {
        // One path with two rows: A (old) is reported as surplus, B is the row that is kept
        var a = Seed(_dbPath, P("dup.dat"), _basePath, hash: "AAAA", size: 10);
        var b = Seed(_dbPath, P("dup.dat"), _basePath, hash: "BBBB", size: 20, mtime: Mtime.AddDays(1));
        var report = Report("r.csv", new[] { MissingRow(a) });
        // Meanwhile a refresh updated row A (it is the row that matches the file now)
        ChangeRow(_dbPath, a.Id, 30, Mtime.AddDays(2), "CCCC");
        var before = Snapshot(_dbPath);

        var applier = Apply(report, out var log);

        Assert.Equal(0, applier.RemovedCount);
        Assert.Equal(1, applier.SkippedStaleCount);
        Assert.Equal(before, Snapshot(_dbPath));
        Assert.Equal(2, ReadRows(_dbPath).Count);
        Assert.Contains(ReadRows(_dbPath), r => r.Id == a.Id && r.FileSha512Hash == "CCCC");
        Assert.Contains(ReadRows(_dbPath), r => r.Id == b.Id);
        AssertStaleWarning(log, P("dup.dat"));
    }

    [Fact]
    public void SurplusRow_ReportedAsMissing_IsRemovedWhenItIsUnchanged_ExactlyThatRow()
    {
        var a = Seed(_dbPath, P("dup.dat"), _basePath, hash: "AAAA", size: 10);
        var b = Seed(_dbPath, P("dup.dat"), _basePath, hash: "BBBB", size: 20, mtime: Mtime.AddDays(1));

        var applier = Apply(Report("r.csv", new[] { MissingRow(a) }), out _);

        Assert.Equal(1, applier.RemovedCount);
        Assert.Equal(b.Id, Assert.Single(ReadRows(_dbPath)).Id);
    }

    // ---------- schema version ----------

    private string WriteVersion1Report(string name, IEnumerable<CheckReportRow> rows)
    {
        var path = Report(name, rows);
        var text = File.ReadAllText(path)
            .Replace("# myDupFinder check report v2", "# myDupFinder check report v1", StringComparison.Ordinal)
            .Replace("# SchemaVersion=2", "# SchemaVersion=1", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Version1Report_IsRejectedWithTheNewMessage_AndNothingIsApplied()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var gone = Seed(_dbPath, P("gone.dat"), _basePath);
        var report = WriteVersion1Report("v1.csv", new[] { ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC"), MissingRow(gone), NewOnDiskRow(P("new.dat"), _basePath, "NEWH") });
        Assert.Contains("SchemaVersion=1", File.ReadAllText(report), StringComparison.Ordinal);
        var before = Snapshot(_dbPath);
        var (applier, _) = CreateApplier();

        var ex = Assert.Throws<InvalidOperationException>(() => applier.Apply(report));

        Assert.Contains("older version", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Run check again", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, applier.UpdatedCount + applier.InsertedCount + applier.RemovedCount);
        Assert.Equal(before, Snapshot(_dbPath));
    }

    [Fact]
    public void UnknownNewerVersionReport_IsRejected()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var report = Report("v9.csv", new[] { ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC") });
        File.WriteAllText(report, File.ReadAllText(report).Replace("# SchemaVersion=2", "# SchemaVersion=9", StringComparison.Ordinal));
        var (applier, _) = CreateApplier();

        var ex = Assert.Throws<InvalidOperationException>(() => applier.Apply(report));

        Assert.Contains("not supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriter_WritesSchemaVersion2()
    {
        Assert.Equal(2, CheckReportHeader.CurrentSchemaVersion);
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var text = File.ReadAllText(Report("r.csv", new[] { ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC") }));
        Assert.Contains("# myDupFinder check report v2", text, StringComparison.Ordinal);
        Assert.Contains("# SchemaVersion=2", text, StringComparison.Ordinal);
    }

    // ---------- all or nothing ----------

    [Fact]
    public void AMalformedRowAnywhere_RejectsTheWholeReport_BeforeAnythingIsApplied()
    {
        var a = Seed(_dbPath, P("a.dat"), _basePath);
        var b = Seed(_dbPath, P("b.dat"), _basePath);
        var report = Report("r.csv", new[] { ModifiedRow(a, 77, Mtime.AddDays(1), "CCCC"), ModifiedRow(b, 78, Mtime.AddDays(1), "DDDD") });
        File.AppendAllText(report, "Modified,only,three fields" + Environment.NewLine);
        var before = Snapshot(_dbPath);
        var (applier, _) = CreateApplier();

        Assert.Throws<InvalidDataException>(() => applier.Apply(report));

        Assert.Equal(0, applier.UpdatedCount);
        Assert.Equal(before, Snapshot(_dbPath));
    }

    [Fact]
    public void AReportThatBecomesMalformedBetweenTheTwoPasses_RollsBackTheOpenTransaction_LogsAndThrows()
    {
        var a = Seed(_dbPath, P("a.dat"), _basePath);
        var b = Seed(_dbPath, P("b.dat"), _basePath);
        var c = Seed(_dbPath, P("c.dat"), _basePath);
        var report = Report("r.csv", new[] { ModifiedRow(a, 77, Mtime.AddDays(1), "CCCC"), ModifiedRow(b, 78, Mtime.AddDays(1), "DDDD"), ModifiedRow(c, 79, Mtime.AddDays(1), "EEEE") });
        var before = Snapshot(_dbPath);
        var log = new ListLoggerProvider();
        // The validation pass is over when the apply pass announces itself: break the last row now
        log.OnMessage = message =>
        {
            if (message.Contains("applyCheck: applying", StringComparison.Ordinal))
            {
                var lines = File.ReadAllLines(report);
                lines[^1] = "Modified,broken";
                File.WriteAllLines(report, lines);
            }
        };
        var (applier, _) = CreateApplier(log);

        Assert.Throws<InvalidDataException>(() => applier.Apply(report));

        // The first two rows were applied inside the open transaction, which was rolled back
        Assert.Equal(before, Snapshot(_dbPath));
        Assert.Equal(0, applier.UpdatedCount);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("rolled back", StringComparison.Ordinal));
        Assert.Contains("committed before stay in the database", error.Message, StringComparison.Ordinal);
    }

    // ---------- end to end ----------

    private string RunCheck(bool skipHash = false)
    {
        var scanJob = new MyDupFinderScanJobDTO { JobName = "E2E", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _basePath, DatabaseFile = _dbPath, ReportPath = _reportDir + Path.DirectorySeparatorChar };
        var job = new MyDupFinderCheckJobDTO { ScanJobDTO = scanJob, SkipHashCheck = skipHash };
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var check = new CheckRunner(job, provider.GetService<ILogger<CheckRunner>>(), provider);
        check.Start(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        return check.ReportFilePath!;
    }

    private string WriteDisk(string name, byte[] content, DateTime mtime)
    {
        var path = P(name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, mtime);
        return path;
    }

    [Fact]
    public void EndToEnd_Scan_ModifyFiles_Check_Apply_DatabaseMatchesDisk_AndApplyingAgainChangesNothing()
    {
        var same = WriteDisk("same.dat", new byte[] { 1, 1, 1 }, Mtime);
        var edited = WriteDisk("edited.dat", new byte[] { 2, 2 }, Mtime);
        var touched = WriteDisk("touched.dat", new byte[] { 3, 3, 3, 3 }, Mtime);
        var rotted = WriteDisk("rotted.dat", new byte[] { 4, 4, 4 }, Mtime);
        var removed = WriteDisk("removed.dat", new byte[] { 5 }, Mtime);
        var scanJob = new MyDupFinderScanJobDTO { JobName = "E2E", OriginComputer = "TestPC", ScanName = "TestScan", BasePath = _basePath, DatabaseFile = _dbPath, ReportPath = _reportDir + Path.DirectorySeparatorChar };
        Assert.Equal(IService.EServiceState.finished, ScanTestRunner.Run(scanJob).RunnerState);
        Assert.Equal(5, ReadRows(_dbPath).Count);

        // Modify the files: new content and time, only the time, same size and time with other content (bit rot), deleted, and a new one
        WriteDisk("edited.dat", new byte[] { 2, 2, 2, 2, 2 }, Mtime.AddDays(1));
        File.SetLastWriteTimeUtc(touched, Mtime.AddDays(2));
        WriteDisk("rotted.dat", new byte[] { 9, 9, 9 }, Mtime);
        File.Delete(removed);
        var fresh = WriteDisk("fresh.dat", new byte[] { 6, 6 }, Mtime.AddDays(3));

        var report = RunCheck();
        var (_, rowsEnum) = CheckReportCsv.Read(report);
        var reportRows = rowsEnum.ToList();
        Assert.Equal(5, reportRows.Count);

        var first = Apply(report, out _);

        Assert.Equal(3, first.UpdatedCount);
        Assert.Equal(1, first.InsertedCount);
        Assert.Equal(1, first.RemovedCount);
        Assert.Equal(0, first.SkippedCount);
        Assert.Equal(0, first.ErrorCount);
        var rows = ReadRows(_dbPath);
        var disk = new DirectoryInfo(_basePath).GetFiles().OrderBy(f => f.FullName, StringComparer.Ordinal).ToList();
        Assert.Equal(disk.Select(f => f.FullName), rows.Select(r => r.FilenameAndPath).OrderBy(p => p, StringComparer.Ordinal));
        foreach (var file in disk)
        {
            var row = Assert.Single(rows, r => r.FilenameAndPath == file.FullName);
            Assert.Equal(file.Length, row.FileSize);
            Assert.Equal(file.LastWriteTimeUtc.Ticks, row.FileLastModificationUTC.Ticks);
            Assert.Equal(Sha512Hex(File.ReadAllBytes(file.FullName)), row.FileSha512Hash, ignoreCase: true);
        }
        Assert.DoesNotContain(rows, r => r.FilenameAndPath == removed);
        Assert.Contains(rows, r => r.FilenameAndPath == fresh);
        Assert.Contains(rows, r => r.FilenameAndPath == same);
        var afterFirst = Snapshot(_dbPath);

        // The same report again changes nothing (not even a scan date)
        Thread.Sleep(20);
        var second = Apply(report, out var log);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(0, second.InsertedCount);
        Assert.Equal(0, second.RemovedCount);
        Assert.Equal(0, second.ErrorCount);
        Assert.Equal(0, second.SkippedStaleCount);
        Assert.Equal(4, second.AlreadyAppliedCount); // three updates and the insert
        Assert.Equal(1, second.SkippedCount); // the removed row is gone
        Assert.Equal(afterFirst, Snapshot(_dbPath));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);

        // And a new check finds nothing to report
        File.Delete(report);
        var (_, againEnum) = CheckReportCsv.Read(RunCheck());
        Assert.Empty(againEnum);
    }

    // ---------- review fixes ----------

    [Fact]
    public void AReportThatIsReplacedByAnotherOneBetweenThePasses_IsNotAppliedToTheOtherDatabase()
    {
        var a = Seed(_dbPath, P("a.dat"), _basePath);
        string otherDb = Path.Combine(_tempDir, "other.db");
        var x = Seed(otherDb, P("x.dat"), _basePath);
        var report = Report("r.csv", new[] { ModifiedRow(a, 77, Mtime.AddDays(1), "CCCC") });
        var replacement = WriteReport(Path.Combine(_reportDir, "b.csv"), otherDb, _basePath, new[] { ModifiedRow(x, 88, Mtime.AddDays(1), "DDDD") });
        var replacementText = File.ReadAllText(replacement);
        var beforeFirst = Snapshot(_dbPath);
        var beforeOther = Snapshot(otherDb);
        var log = new ListLoggerProvider();
        log.OnMessage = message =>
        {
            if (message.Contains("applyCheck: applying", StringComparison.Ordinal))
            {
                File.WriteAllText(report, replacementText);
            }
        };
        var (applier, _) = CreateApplier(log);

        var ex = Assert.Throws<InvalidDataException>(() => applier.Apply(report));

        Assert.Contains("changed while it was read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, applier.UpdatedCount);
        Assert.Equal(beforeFirst, Snapshot(_dbPath));
        Assert.Equal(beforeOther, Snapshot(otherDb));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("rolled back", StringComparison.Ordinal));
    }

    [Fact]
    public void PathMove_ReappliedAfterTheContentChangedAtTheDestination_IsStale()
    {
        string oldBase = Path.Combine(_tempDir, "OLD") + Path.DirectorySeparatorChar;
        var item = Seed(_dbPath, Path.Combine(oldBase, "m.dat"), oldBase);
        var report = Report("move.csv", new[] { PathMoveRow(item, P("m.dat"), _basePath) }, skipHash: true);
        Assert.Equal(1, Apply(report, out _).UpdatedCount);
        // A refresh changed size, time and hash at the new path
        ChangeRow(_dbPath, item.Id, 99, Mtime.AddDays(4), "REFRESHED");
        var before = Snapshot(_dbPath);

        var again = Apply(report, out _);

        Assert.Equal(0, again.AlreadyAppliedCount);
        Assert.Equal(1, again.SkippedStaleCount);
        Assert.Equal(before, Snapshot(_dbPath));
    }

    [Fact]
    public void ContentUpdateWithoutPathMove_ReappliedAfterThePathChanged_IsStale()
    {
        var item = Seed(_dbPath, P("a.dat"), _basePath);
        var report = Report("r.csv", new[] { ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC") });
        Assert.Equal(1, Apply(report, out _).UpdatedCount);
        ChangeRow(_dbPath, item.Id, 77, Mtime.AddDays(1), "CCCC", P("elsewhere.dat"), _basePath);
        var before = Snapshot(_dbPath);

        var again = Apply(report, out _);

        Assert.Equal(0, again.AlreadyAppliedCount);
        Assert.Equal(1, again.SkippedStaleCount);
        Assert.Equal(before, Snapshot(_dbPath));
    }

    [Fact]
    public void AFailedCommit_CountsAnAlreadyAppliedRowAsAnError_SoEveryRowIsInExactlyOneCounter()
    {
        var item = Seed(_dbPath, P("done.dat"), _basePath);
        var updateRow = ModifiedRow(item, 77, Mtime.AddDays(1), "CCCC");
        ChangeRow(_dbPath, item.Id, 77, Mtime.AddDays(1), "CCCC"); // the update was applied before
        InstallFailingCommitTrigger(_dbPath);
        var report = Report("r.csv", new[] { updateRow, NewOnDiskRow(P("poisoncommit.dat"), _basePath, "NEWH") });

        var applier = Apply(report, out var log);

        Assert.Equal(1, applier.FailedCommitCount);
        Assert.Equal(2, applier.ErrorCount);
        Assert.Equal(0, applier.AlreadyAppliedCount);
        Assert.Equal(0, applier.InsertedCount);
        Assert.Equal(0, applier.UpdatedCount);
        Assert.Equal(0, applier.SkippedCount);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("committing", StringComparison.Ordinal));
        Assert.Contains("1 rows that were found already applied", error.Message, StringComparison.Ordinal);
    }
}
