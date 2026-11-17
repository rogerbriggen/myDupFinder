// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// How a find-duplicates run ends: finished only after the report was written, cancelled by the token (between the phases, in the write loop, and a running query is
/// interrupted), failed otherwise. A partial report (created by the run, then failed or cancelled) is kept as <c>.failed</c> so that it does not block the next run;
/// a report that existed before the run is never touched.
/// </summary>
public class FindDupsOutcomeTest : IDisposable
{
    private const string ExistingContent = "do not touch\r\n";

    private readonly string _tempDir;
    private readonly string _baseDbPath;
    private readonly string _secondDbPath;

    public FindDupsOutcomeTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FindDupsOutcomeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _baseDbPath = Path.Combine(_tempDir, "base.db");
        _secondDbPath = Path.Combine(_tempDir, "second.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string Report => Path.Combine(_tempDir, "job dupReport.csv");

    private static ScanItemDto Item(string fullPath, string hash) => new ScanItemDto
    {
        Id = Guid.NewGuid(),
        FilenameAndPath = fullPath,
        PathBase = Path.GetDirectoryName(fullPath) ?? string.Empty,
        ScanExecutionComputer = "TestPC",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        FileSize = 10,
        FileSha512Hash = hash,
        FileCreationUTC = DateTime.UtcNow,
        FileLastModificationUTC = DateTime.UtcNow,
        FirstScanDateUTC = DateTime.UtcNow,
        LastScanDateUTC = DateTime.UtcNow,
        LastSha512ScanDateUTC = DateTime.UtcNow,
    };

    private static void PopulateDatabase(string dbPath, params ScanItemDto[] items)
    {
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(dbPath);
        foreach (var item in items)
        {
            dbInserts.Enqueue(item);
        }
        dbInserts.WriteChanges();
    }

    private MyDupFinderFindDupsJobDTO CreateJob(bool crossDb)
    {
        PopulateDatabase(_baseDbPath, Item("/base/a.txt", "HASH_A"), Item("/base/b.txt", "HASH_A"));
        if (crossDb)
        {
            PopulateDatabase(_secondDbPath, Item("/second/a.txt", "HASH_A"));
        }
        return new MyDupFinderFindDupsJobDTO
        {
            JobName = "job",
            DatabaseFileBase = _baseDbPath,
            DatabaseFile = crossDb ? _secondDbPath : string.Empty,
            ReportPath = _tempDir,
        };
    }

    private static (IRunner Runner, ListLoggerProvider Log) CreateRunner(MyDupFinderFindDupsJobDTO dto, bool crossDb)
    {
        var provider = new ListLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        IRunner runner = crossDb
            ? new FindDupsInDifferentDBs(dto, factory.CreateLogger<FindDupsInDifferentDBs>(), new NullServiceProvider())
            : new FindDupsInSameDB(dto, factory.CreateLogger<FindDupsInSameDB>(), new NullServiceProvider());
        return (runner, provider);
    }

    private static IService.EServiceState Run(MyDupFinderFindDupsJobDTO dto, bool crossDb, CancellationToken token, out ListLoggerProvider log)
    {
        var (runner, provider) = CreateRunner(dto, crossDb);
        log = provider;
        try
        {
            runner.Start(token);
            return runner.RunnerState;
        }
        finally
        {
            runner.Dispose();
        }
    }

    private string[] FilesInTempDir() => Directory.GetFiles(_tempDir).Select(Path.GetFileName).OfType<string>().Where(n => !n.EndsWith(".db", StringComparison.Ordinal) && !n.EndsWith("-wal", StringComparison.Ordinal) && !n.EndsWith("-shm", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ----- finished -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_Successful_EndsFinished_AfterTheReportWasWritten(bool crossDb)
    {
        var dto = CreateJob(crossDb);

        var state = Run(dto, crossDb, CancellationToken.None, out var log);

        Assert.Equal(IService.EServiceState.finished, state);
        Assert.True(File.Exists(Report));
        Assert.Equal(DupReportWriter.HeaderLine, File.ReadLines(Report).First());
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Duplicate report successfully created", StringComparison.Ordinal));
        Assert.Equal(new[] { "job dupReport.csv" }, FilesInTempDir());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WholeLotMode_EndsFinished(bool crossDb)
    {
        var dto = CreateJob(crossDb);
        dto.FindDupsMode = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot;

        var state = Run(dto, crossDb, CancellationToken.None, out _);

        Assert.Equal(IService.EServiceState.finished, state);
        Assert.True(File.Exists(Report));
    }

    // ----- cancelled -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_TokenCancelledBeforeStart_EndsCancelled_AndLeavesNoReport_SoTheNextRunIsNotBlocked(bool crossDb)
    {
        var dto = CreateJob(crossDb);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var state = Run(dto, crossDb, cts.Token, out var log);

        Assert.Equal(IService.EServiceState.cancelled, state);
        Assert.Empty(FilesInTempDir());
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        // the next run finds nothing in its way
        Assert.Equal(IService.EServiceState.finished, Run(dto, crossDb, CancellationToken.None, out _));
        Assert.True(File.Exists(Report));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_TokenCancelledBeforeStart_WithAnExistingReport_LeavesItUntouched(bool crossDb)
    {
        var dto = CreateJob(crossDb);
        File.WriteAllText(Report, ExistingContent);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var state = Run(dto, crossDb, cts.Token, out _);

        Assert.Equal(IService.EServiceState.cancelled, state);
        Assert.Equal(ExistingContent, File.ReadAllText(Report));
        Assert.False(File.Exists(Report + ".failed"));
    }

    // ----- failed -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_ReportAlreadyExists_EndsFailed_AndTheExistingReportIsNeitherChangedNorRenamed(bool crossDb)
    {
        var dto = CreateJob(crossDb);
        File.WriteAllText(Report, ExistingContent);

        var state = Run(dto, crossDb, CancellationToken.None, out var log);

        Assert.Equal(IService.EServiceState.failed, state);
        Assert.Equal(ExistingContent, File.ReadAllText(Report));
        Assert.False(File.Exists(Report + ".failed"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("already exists and is not overwritten", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_ReportCannotBeCreated_EndsFailed_AndLeavesNothingBehind(bool crossDb)
    {
        var dto = CreateJob(crossDb);
        Directory.CreateDirectory(Report); // a directory with the name of the report: the file cannot be created

        var state = Run(dto, crossDb, CancellationToken.None, out var log);

        Assert.Equal(IService.EServiceState.failed, state);
        Assert.False(File.Exists(Report + ".failed"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_DatabaseCannotBeOpened_EndsFailed(bool crossDb)
    {
        var dto = CreateJob(crossDb);
        // The pooled connections of the seeding still hold the file open: clear the pools before the file is replaced
        SqliteConnection.ClearAllPools();
        File.Delete(_baseDbPath);
        File.WriteAllText(_baseDbPath, new string('x', 5000)); // no database

        var state = Run(dto, crossDb, CancellationToken.None, out var log);

        Assert.Equal(IService.EServiceState.failed, state);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
        Assert.False(File.Exists(Report));
    }

    [Fact]
    public void Run_SecondDatabaseMissing_EndsFailed()
    {
        var dto = CreateJob(crossDb: true);
        SqliteConnection.ClearAllPools();
        File.Delete(_secondDbPath);

        var state = Run(dto, crossDb: true, CancellationToken.None, out _);

        Assert.Equal(IService.EServiceState.failed, state);
        Assert.False(File.Exists(Report));
    }

    // ----- the writer: partial report is kept as .failed -----

    private static IEnumerable<DupReportRow> RowsThatFailWhileEnumerated(DupReportRow first)
    {
        yield return first;
        throw new IOException("simulated write failure");
    }

    private static List<DupReportRow> ManyRows(int count) =>
        Enumerable.Range(0, count).Select(i => new DupReportRow(Item("/r/f" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".txt", "H" + i), DupReportCategory.Duplicate, DupReportSource.Base, i)).ToList();

    [Fact]
    public void Writer_FailureAfterCreation_KeepsThePartialFileAsFailed_AndOverwritesAnOlderFailedFile()
    {
        File.WriteAllText(Report + ".failed", "older failed run");
        var first = new DupReportRow(Item("/r/a.txt", "H"), DupReportCategory.Duplicate, DupReportSource.Base, 1);

        var ex = Assert.Throws<ReportWriteFailedException>(() => DupReportWriter.Write(Report, RowsThatFailWhileEnumerated(first)));

        Assert.False(File.Exists(Report));
        Assert.Equal(Report + ".failed", ex.FailedFile);
        Assert.Contains(DupReportWriter.HeaderLine, File.ReadAllText(Report + ".failed"));
        Assert.DoesNotContain("older failed run", File.ReadAllText(Report + ".failed"));
        // not blocked any more: a new run can create the report
        DupReportWriter.Write(Report, new List<DupReportRow> { first });
        Assert.True(File.Exists(Report));
    }

    [Fact]
    public void Writer_CancelledInTheWriteLoop_ThrowsCancelled_AndKeepsThePartialFileAsFailed()
    {
        var rows = ManyRows(DupReportWriter.CancelCheckInterval * 3);
        using var cts = new CancellationTokenSource();
        // The report is created before the rows are sorted, i.e. before the first row is read from this sequence: cancel while it is read
        IEnumerable<DupReportRow> cancelling = rows.Select((r, i) =>
        {
            if (i == 10)
            {
                cts.Cancel();
            }
            return r;
        });

        var ex = Assert.Throws<ReportWriteCancelledException>(() => DupReportWriter.Write(Report, cancelling, cts.Token));

        Assert.False(File.Exists(Report));
        Assert.Equal(Report + ".failed", ex.FailedFile);
        Assert.True(File.Exists(Report + ".failed"));
        // it stopped at a check, not at the end
        Assert.True(File.ReadAllLines(Report + ".failed").Length <= DupReportWriter.CancelCheckInterval + 1);
    }

    [Fact]
    public void Writer_CancelledDuringTheSort_WithFewRows_IsSeenAfterTheLastRow_AndKeepsTheFileAsFailed()
    {
        var rows = ManyRows(5);
        using var cts = new CancellationTokenSource();
        // Read after the file was created (the sort enumerates it); far fewer rows than the periodic check interval
        IEnumerable<DupReportRow> cancelling = rows.Select((r, i) =>
        {
            if (i == 2)
            {
                cts.Cancel();
            }
            return r;
        });

        var ex = Assert.Throws<ReportWriteCancelledException>(() => DupReportWriter.Write(Report, cancelling, cts.Token));

        Assert.False(File.Exists(Report));
        Assert.Equal(Report + ".failed", ex.FailedFile);
        Assert.True(File.Exists(Report + ".failed"));
    }

    [Fact]
    public void Writer_CancelledBeforeTheFileIsCreated_CreatesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DupReportWriter.Write(Report, ManyRows(5), cts.Token));

        Assert.False(File.Exists(Report));
        Assert.False(File.Exists(Report + ".failed"));
    }

    [Fact]
    public void Writer_ExistingReport_IsNotRenamed_WhateverTheToken()
    {
        File.WriteAllText(Report, ExistingContent);

        Assert.Throws<ReportAlreadyExistsException>(() => DupReportWriter.Write(Report, ManyRows(5), CancellationToken.None));

        Assert.Equal(ExistingContent, File.ReadAllText(Report));
        Assert.False(File.Exists(Report + ".failed"));
    }

    [Fact]
    public void Writer_NormalRun_LeavesNoFailedFile()
    {
        DupReportWriter.Write(Report, ManyRows(DupReportWriter.CancelCheckInterval + 5), new CancellationTokenSource().Token);

        Assert.True(File.Exists(Report));
        Assert.False(File.Exists(Report + ".failed"));
        Assert.Equal(DupReportWriter.CancelCheckInterval + 6, File.ReadAllLines(Report).Length);
    }

    // ----- a running query is interrupted -----

    // A query that runs for a long time (a few seconds when it is not interrupted): counts up to 150 million in a recursive CTE
    private const string SlowQuery = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 150000000) SELECT COUNT(*) AS Value FROM c";

    private DubFinderContext NewContext()
    {
        PopulateDatabase(_baseDbPath, Item("/base/a.txt", "HASH_A"));
        return DubFinderContextFactory.CreateDubFinderContext(_baseDbPath);
    }

    [Fact]
    public void RunInterruptible_CancelledWhileTheQueryRuns_InterruptsItAndThrowsOperationCanceled()
    {
        using var context = NewContext();
        using var cts = new CancellationTokenSource();
        using var started = new ManualResetEventSlim(false);
        // The cancel is given only after the native query has really started: a user function that the query calls signals its first call
        var canceller = new Thread(() =>
        {
            if (started.Wait(TimeSpan.FromSeconds(30)))
            {
                cts.Cancel();
            }
        });
        canceller.Start();
        const string signalingQuery = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 150000000) SELECT COUNT(*) AS Value FROM c WHERE signal(x) = 1";
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.ThrowsAny<OperationCanceledException>(() =>
            DubFinderDB.RunInterruptible(context, cts.Token, () =>
            {
                var connection = (SqliteConnection)context.Database.GetDbConnection();
                connection.CreateFunction("signal", (long x) =>
                {
                    if (!started.IsSet)
                    {
                        started.Set();
                    }
                    return 1L;
                });
                return context.Database.SqlQueryRaw<long>(signalingQuery).AsEnumerable().Single();
            }));
        canceller.Join();

        Assert.True(started.IsSet, "The query never started");
        // It came from the interrupt path (a SQLITE_INTERRUPT of the running statement), not from a token check before the query
        var inner = Assert.IsType<SqliteException>(ex.InnerException);
        Assert.Equal(SQLitePCL.raw.SQLITE_INTERRUPT, inner.SqliteErrorCode);
        // Hang guard only: it would need many seconds to run to its end
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"The query took {stopwatch.Elapsed}");
        // The connection is closed again and usable
        Assert.Equal(ConnectionStateClosed, context.Database.GetDbConnection().State);
        Assert.Equal(1, context.ScanItems!.Count());
    }

    private const System.Data.ConnectionState ConnectionStateClosed = System.Data.ConnectionState.Closed;

    [Fact]
    public void RunInterruptible_TokenAlreadyCancelled_ThrowsWithoutRunningTheQuery()
    {
        using var context = NewContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool ran = false;

        Assert.ThrowsAny<OperationCanceledException>(() => DubFinderDB.RunInterruptible(context, cts.Token, () =>
        {
            ran = true;
            return 1;
        }));

        Assert.False(ran);
    }

    [Fact]
    public void RunInterruptible_TokenNeverCancelled_ReturnsTheResult_AndLeavesTheConnectionAsItWas()
    {
        using var context = NewContext();
        using var cts = new CancellationTokenSource();

        long result = DubFinderDB.RunInterruptible(context, cts.Token, () => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM ScanItems").AsEnumerable().Single());

        Assert.Equal(1, result);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        // another error is not turned into a cancel
        Assert.Throws<SqliteException>(() => DubFinderDB.RunInterruptible(context, cts.Token, () => context.Database.SqlQueryRaw<long>("SELECT * FROM NoSuchTable").AsEnumerable().ToList()));
    }

    [Fact]
    public void DubFinderDB_CancelledToken_StopsTheQueries()
    {
        PopulateDatabase(_baseDbPath, Item("/base/a.txt", "HASH_A"), Item("/base/b.txt", "HASH_A"));
        PopulateDatabase(_secondDbPath, Item("/second/a.txt", "HASH_A"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var db = new DubFinderDB(null);
        db.SetupDB(_baseDbPath);

        Assert.ThrowsAny<OperationCanceledException>(() => db.GetAllScanItems(cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => db.FindDupsInSameDB(cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => db.FindDupsInDifferentDBs(_secondDbPath, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => db.GetAllScanItemsFromOtherDB(_secondDbPath, cts.Token));
        // and without a cancel they work
        Assert.Equal(2, db.FindDupsInSameDB(CancellationToken.None)!.Count);
        Assert.Equal(2, db.FindDupsInDifferentDBs(_secondDbPath, new CancellationTokenSource().Token)!.Count);
    }
}
