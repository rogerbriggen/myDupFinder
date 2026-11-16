// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;
using Xunit.Abstractions;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// Opt-in benchmark of the find-dups job (step 5 of the plan): two generated databases, all four modes. Run it with
/// MYDUPFINDER_BENCHMARK=1, see docs/benchmark.md. MYDUPFINDER_BENCHMARK_FILES sets the number of rows per database (default 100000).
/// </summary>
public class FindDupsBenchmark : IDisposable
{
    private const string RowCountVariable = "MYDUPFINDER_BENCHMARK_FILES";
    private const int DefaultRowCount = 100000;

    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    public FindDupsBenchmark(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "FindDupsBenchmark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // cache_size / temp_store from MYDUPFINDER_BENCHMARK_SQLITE (find-dups only reads, so "normal" changes nothing here)
        BenchmarkSqliteSettings.Apply();
    }

    public void Dispose()
    {
        BenchmarkSqliteSettings.Reset();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static int GetRowCount() =>
        int.TryParse(Environment.GetEnvironmentVariable(RowCountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : DefaultRowCount;

    private sealed record CaseResult(string Name, double? QuerySeconds, double? TotalSeconds, int Rows, double PeakMegabytes, string? Failure);

    /// <summary>Samples the managed heap size while a case runs (the peak includes garbage that is not collected yet).</summary>
    private sealed class PeakMemorySampler : IDisposable
    {
        private readonly Timer _timer;
        private long _peak;

        public PeakMemorySampler()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Baseline = GC.GetTotalMemory(true);
            _peak = Baseline;
            _timer = new Timer(_ => Sample(), null, 0, 5);
        }

        public long Baseline { get; }

        public double PeakMegabytes
        {
            get
            {
                Sample();
                return Math.Max(0, Interlocked.Read(ref _peak) - Baseline) / (1024.0 * 1024.0);
            }
        }

        private void Sample()
        {
            long now = GC.GetTotalMemory(false);
            long seen;
            while (now > (seen = Interlocked.Read(ref _peak)))
            {
                if (Interlocked.CompareExchange(ref _peak, now, seen) == seen)
                {
                    break;
                }
            }
        }

        public void Dispose() => _timer.Dispose();
    }

    private CaseResult RunCase(string name, string baseDb, string? secondDb, MyDupFinderFindDupsJobDTO.EFindDupsMode mode, Action<DubFinderDB> queryPart)
    {
        try
        {
            // 1. the database part alone: the queries of the runner on a database that is already open
            double querySeconds;
            using (var db = new DubFinderDB(null))
            {
                db.SetupDB(baseDb);
                GC.Collect();
                long start = Stopwatch.GetTimestamp();
                queryPart(db);
                querySeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            }

            // 2. the whole job: open, query, build the rows, write the report. Peak memory is measured here.
            var dto = new MyDupFinderFindDupsJobDTO
            {
                JobName = name,
                DatabaseFileBase = baseDb,
                DatabaseFile = secondDb ?? string.Empty,
                ReportPath = _tempDir + Path.DirectorySeparatorChar,
                FindDupsMode = mode,
            };
            double totalSeconds;
            double peak;
            using (var sampler = new PeakMemorySampler())
            {
                long start = Stopwatch.GetTimestamp();
                if (secondDb is null)
                {
                    using var runner = new FindDupsInSameDB(dto, null, new NullServiceProvider());
                    runner.Start(CancellationToken.None);
                }
                else
                {
                    using var runner = new FindDupsInDifferentDBs(dto, null, new NullServiceProvider());
                    runner.Start(CancellationToken.None);
                }
                totalSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                peak = sampler.PeakMegabytes;
            }

            int reportRows = File.ReadLines(Path.Combine(_tempDir, name + " dupReport.csv")).Count() - 1;
            return new CaseResult(name, querySeconds, totalSeconds, reportRows, peak, null);
        }
        catch (Exception ex)
        {
            // A failing case must not hide the other ones (the cross-database query can fail with a big second database)
            return new CaseResult(name, null, null, 0, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    [BenchmarkFact]
    public void FindDups_AllModes_ReportTimings()
    {
        int rowCount = GetRowCount();
        _output.WriteLine("Mode: " + BenchmarkSqliteSettings.Description);
        var stopwatch = Stopwatch.StartNew();
        var (baseRows, secondRows) = FindDupsTestData.Generate(rowCount);
        string baseDb = Path.Combine(_tempDir, "base.db");
        string secondDb = Path.Combine(_tempDir, "second.db");
        FindDupsTestData.WriteDatabase(baseDb, baseRows);
        FindDupsTestData.WriteDatabase(secondDb, secondRows);
        SqliteConnection.ClearAllPools();
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Databases: {0} rows each, base has {1} distinct hashes, second has {2} distinct hashes, {3} hashes in common, created in {4:F1} s (not part of the times)",
            rowCount,
            baseRows.Where(r => r.FileSha512Hash.Length > 0).Select(r => r.FileSha512Hash).Distinct().Count(),
            secondRows.Where(r => r.FileSha512Hash.Length > 0).Select(r => r.FileSha512Hash).Distinct().Count(),
            baseRows.Select(r => r.FileSha512Hash).Where(h => h.Length > 0).Intersect(secondRows.Select(r => r.FileSha512Hash)).Count(),
            stopwatch.Elapsed.TotalSeconds));
        baseRows = null!;
        secondRows = null!;

        var results = new List<CaseResult>
        {
            RunCase("same-onlydups", baseDb, null, MyDupFinderFindDupsJobDTO.EFindDupsMode.FindOnlyDups, db => db.FindDupsInSameDB()),
            RunCase("same-wholelot", baseDb, null, MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot, db => db.GetAllScanItems()),
            RunCase("diff-onlydups", baseDb, secondDb, MyDupFinderFindDupsJobDTO.EFindDupsMode.FindOnlyDups, db => db.FindDupsInDifferentDBs(secondDb)),
            RunCase("diff-wholelot", baseDb, secondDb, MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot, db =>
            {
                db.GetAllScanItems();
                db.GetAllScanItemsFromOtherDB(secondDb);
            }),
        };

        _output.WriteLine("| Mode | Query part (s) | Total with report (s) | Report rows | Peak managed memory above start (MB) |");
        _output.WriteLine("| --- | --- | --- | --- | --- |");
        foreach (var r in results)
        {
            _output.WriteLine(r.Failure is null
                ? string.Format(CultureInfo.InvariantCulture, "| {0} | {1:F2} | {2:F2} | {3} | {4:F0} |", r.Name, r.QuerySeconds, r.TotalSeconds, r.Rows, r.PeakMegabytes)
                : string.Format(CultureInfo.InvariantCulture, "| {0} | FAILED: {1} | | | |", r.Name, r.Failure));
        }

        Assert.All(results, r => Assert.True(r.Failure is null, r.Name + ": " + r.Failure));
    }

    /// <summary>
    /// The cross-database only-dups query on its own, with a growing number of distinct hashes in the second database: the version before step 5
    /// (<see cref="ReferenceFindDups"/>, all hashes of the second database passed back as a filter) against the attached query. Sizes: MYDUPFINDER_BENCHMARK_HASHES
    /// (comma separated list of distinct hashes in the second database, default 10000,30000,100000,300000).
    /// </summary>
    [BenchmarkFact]
    public void CrossDatabaseQuery_ReferenceVersusAttached_ReportsTimings()
    {
        string sizes = Environment.GetEnvironmentVariable("MYDUPFINDER_BENCHMARK_HASHES") ?? "10000,30000,100000,300000";
        _output.WriteLine("| Distinct hashes in second db | Rows in base db | Matching base rows | Before: filter list (s) | After: attached (s) |");
        _output.WriteLine("| --- | --- | --- | --- | --- |");
        foreach (int distinct in sizes.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)))
        {
            string baseDb = Path.Combine(_tempDir, "base" + distinct + ".db");
            string secondDb = Path.Combine(_tempDir, "second" + distinct + ".db");
            // second: hash of "c0", "c2", "c4" ...; base: "c0" ... "c(2n-1)": every second hash is in base, half of the base rows match
            FindDupsTestData.WriteDatabase(baseDb, Enumerable.Range(0, 2 * distinct).Select(i => FindDupsTestData.Row("/b", "f" + i, FindDupsTestData.HashOf("c" + i), 1, "b" + i)));
            FindDupsTestData.WriteDatabase(secondDb, Enumerable.Range(0, distinct).Select(i => FindDupsTestData.Row("/s", "f" + i, FindDupsTestData.HashOf("c" + (2 * i)), 1, "s" + i)));
            SqliteConnection.ClearAllPools();

            string before;
            int beforeCount = -1;
            try
            {
                var start = Stopwatch.GetTimestamp();
                using (var context = DubFinderContextFactory.CreateDubFinderContext(baseDb))
                using (var second = DubFinderContextFactory.CreateDubFinderContext(secondDb))
                {
                    beforeCount = ReferenceFindDups.FindDupsInDifferentDBs(context, second)!.Count;
                }
                before = Stopwatch.GetElapsedTime(start).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                before = "FAILED: " + ex.GetType().Name + ": " + ex.Message;
            }

            var startAfter = Stopwatch.GetTimestamp();
            int afterCount;
            using (var context = DubFinderContextFactory.CreateDubFinderContext(baseDb))
            {
                afterCount = DubFinderDB.FindDupsInDifferentDBs(context, secondDb)!.Count;
            }
            double after = Stopwatch.GetElapsedTime(startAfter).TotalSeconds;
            if (beforeCount >= 0)
            {
                Assert.Equal(beforeCount, afterCount);
            }
            _output.WriteLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3} | {4:F2} |", distinct, 2 * distinct, afterCount, before, after));
        }
    }

    /// <summary>
    /// The cross-database only-dups query with databases of very different sizes (base rows / second rows) and a high and a low share
    /// of hashes in common: the old query (hashes loaded and passed back as a filter), the <c>EXISTS</c> form, the <c>IN</c> form and the form the code
    /// chooses. Times are the query on a context for the base database including materializing the rows, best of two runs.
    /// </summary>
    [BenchmarkFact]
    public void CrossDatabaseQuery_AsymmetricPairs_ReportTimings()
    {
        var cases = new (int BaseRows, int SecondRows, double Share)[]
        {
            (300000, 10, 1.0), (300000, 10, 0.0), (300000, 1000, 1.0), (300000, 1000, 0.01),
            (10, 300000, 1.0), (10, 300000, 0.0), (1000, 300000, 1.0), (1000, 300000, 0.01),
            (300000, 300000, 0.9), (300000, 300000, 0.01),
        };
        _output.WriteLine("| Base rows | Second rows | Share of the second rows in common | Matching base rows | Before: filter list (s) | EXISTS form (s) | IN form (s) | Chosen form (s) | Chosen |");
        _output.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var (baseRows, secondRows, share) in cases)
        {
            string tag = string.Create(CultureInfo.InvariantCulture, $"{baseRows}_{secondRows}_{share}");
            string baseDb = Path.Combine(_tempDir, "b" + tag + ".db");
            string secondDb = Path.Combine(_tempDir, "s" + tag + ".db");
            int common = (int)(secondRows * share);
            FindDupsTestData.WriteDatabaseInOrder(baseDb, Enumerable.Range(0, baseRows).Select(i => FindDupsTestData.Row("/b", "f" + i, FindDupsTestData.HashOf("c" + i), 1, "b" + i)));
            FindDupsTestData.WriteDatabaseInOrder(secondDb, Enumerable.Range(0, secondRows).Select(j => FindDupsTestData.Row("/s", "f" + j, FindDupsTestData.HashOf(j < common ? "c" + ((long)j * 7 % baseRows) : "x" + j), 1, "s" + j)));
            SqliteConnection.ClearAllPools();

            static (int Count, double Seconds) Best(Func<int> run)
            {
                double best = double.MaxValue;
                int count = 0;
                for (int i = 0; i < 2; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    count = run();
                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
                }
                return (count, best);
            }

            (int Count, double Seconds) Attached(DubFinderDB.HashMatchForm? form) => Best(() =>
            {
                using var context = DubFinderContextFactory.CreateDubFinderContext(baseDb);
                return DubFinderDB.FindDupsInDifferentDBs(context, secondDb, form)!.Count;
            });

            string before;
            try
            {
                before = Best(() =>
                {
                    using var context = DubFinderContextFactory.CreateDubFinderContext(baseDb);
                    using var second = DubFinderContextFactory.CreateDubFinderContext(secondDb);
                    return ReferenceFindDups.FindDupsInDifferentDBs(context, second)!.Count;
                }).Seconds.ToString("F3", CultureInfo.InvariantCulture);
            }
            catch (SqliteException ex)
            {
                before = "FAILED: " + ex.Message;
            }
            var exists = Attached(DubFinderDB.HashMatchForm.Exists);
            var inForm = Attached(DubFinderDB.HashMatchForm.In);
            var chosen = Attached(null);
            Assert.Equal(exists.Count, inForm.Count);
            Assert.Equal(exists.Count, chosen.Count);
            string which = (long)secondRows * 3 < baseRows ? "IN" : "EXISTS";
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {baseRows} | {secondRows} | {share} | {exists.Count} | {before} | {exists.Seconds:F3} | {inForm.Seconds:F3} | {chosen.Seconds:F3} | {which} |"));
            SqliteConnection.ClearAllPools();
            File.Delete(baseDb);
            File.Delete(secondDb);
        }
    }

    /// <summary>The cost of the size estimate that chooses the form: MAX(rowid) against COUNT(*) on a table of MYDUPFINDER_BENCHMARK_FILES rows (default 300000).</summary>
    [BenchmarkFact]
    public void CrossDatabaseQuery_SizeEstimate_ReportsCost()
    {
        int rows = int.TryParse(Environment.GetEnvironmentVariable(RowCountVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : 300000;
        string db = Path.Combine(_tempDir, "estimate.db");
        FindDupsTestData.WriteDatabaseInOrder(db, Enumerable.Range(0, rows).Select(i => FindDupsTestData.Row("/b", "f" + i, FindDupsTestData.HashOf("c" + i), 1, "b" + i)));
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        foreach (var sql in new[] { "SELECT COALESCE(MAX(rowid), 0) FROM ScanItems", "SELECT COUNT(*) FROM ScanItems" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteScalar();
            long start = Stopwatch.GetTimestamp();
            object? value = command.ExecuteScalar();
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{rows} rows: {sql} = {value} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3} ms"));
        }
    }

    /// <summary>
    /// Memory of the rows of one query with and without change tracking, for the number of rows of MYDUPFINDER_BENCHMARK_FILES
    /// (default 100000). The memory is the managed heap after a full collection with the result list alive.
    /// </summary>
    [BenchmarkFact]
    public void Tracking_MemoryOfTheRows_ReportsComparison()
    {
        int rowCount = GetRowCount();
        string db = Path.Combine(_tempDir, "tracking.db");
        FindDupsTestData.WriteDatabase(db, FindDupsTestData.Generate(rowCount).BaseRows);
        SqliteConnection.ClearAllPools();

        static long Heap()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true);
        }

        // The queries run in their own methods, so the context is really unreachable when the memory of the list alone is measured
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static List<ScanItemDto> LoadTracked(string file, out long withContext)
        {
            long before = Heap();
            using var context = DubFinderContextFactory.CreateDubFinderContext(file);
            var rows = context.Set<ScanItemDto>().OrderBy(s => s.FileSha512Hash).ThenBy(s => s.FilenameAndPath).ThenBy(s => s.Id).ToList();
            withContext = Heap() - before;
            return rows;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static List<ScanItemDto> LoadUntracked(string file)
        {
            using var context = DubFinderContextFactory.CreateDubFinderContext(file);
            return DubFinderDB.GetAllScanItems(context);
        }

        long start = Heap();
        var tracked = LoadTracked(db, out long trackedWithContext);
        long trackedListOnly = Heap() - start;
        int trackedCount = tracked.Count;
        tracked = null!;

        long start2 = Heap();
        var untracked = LoadUntracked(db);
        long untrackedListOnly = Heap() - start2;
        GC.KeepAlive(untracked);

        const double Mb = 1024.0 * 1024.0;
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Rows: {0}. Tracked, context alive: {1:F1} MB. Tracked, list alive after the context is disposed: {2:F1} MB. Not tracked, list alive: {3:F1} MB.",
            trackedCount,
            trackedWithContext / Mb,
            trackedListOnly / Mb,
            untrackedListOnly / Mb));
    }
}
