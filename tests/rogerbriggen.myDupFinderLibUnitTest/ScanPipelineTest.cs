// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The scan pipeline (HashWorkers greater than 1): the same rows as the sequential scan, every file exactly once, cancel, failures and back-pressure.
/// Every wait on another thread has a timeout (see <see cref="PipelineTestSupport"/>), and a run is started on its own thread so a deadlock fails the test.
/// </summary>
public class ScanPipelineTest : IDisposable
{
    private const int Block = 1024;

    private readonly string _tempDir;

    public ScanPipelineTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScanPipelineTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    // ---- helpers ----

    private string TreeDir(string name) => Path.Combine(_tempDir, "tree_" + name);

    private string DbPath(string name) => Path.Combine(_tempDir, "db", name + ".db");

    private MyDupFinderScanJobDTO Job(string treeDir, string dbName, int workers, bool trust = false) => new MyDupFinderScanJobDTO
    {
        JobName = "PipelineTest",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = treeDir,
        DatabaseFile = DbPath(dbName),
        ReportPath = _tempDir,
        HashWorkers = workers,
        TrustDirectoryMetadata = trust,
    };

    private static ScanRunner NewRunner(MyDupFinderScanJobDTO job, ServiceProvider serviceProvider, ScanJobDBInserts? inserts = null, IFileEnumerationSource? source = null, FileHasher? hasher = null) =>
        new ScanRunner(job, serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, inserts, source, hasher);

    /// <summary>Runs the job to its end (guarded by the timeout) and returns the runner.</summary>
    private static ScanRunner RunToEnd(MyDupFinderScanJobDTO job, ListLoggerProvider? log = null, IFileEnumerationSource? source = null, FileHasher? hasher = null, ScanJobDBInserts? inserts = null, Action<ScanRunner>? configure = null, CancellationToken token = default)
    {
        using var serviceProvider = PipelineTestSupport.BuildServices(log);
        var runner = NewRunner(job, serviceProvider, inserts, source, hasher);
        configure?.Invoke(runner);
        PipelineTestSupport.StartGuarded(runner, token);
        SqliteConnection.ClearAllPools();
        return runner;
    }

    private static List<FileEntryInfo> ListFiles(string root)
    {
        var list = new List<FileEntryInfo>();
        using var enumeration = new FileTreeEnumeration(root);
        while (enumeration.MoveNext())
        {
            list.Add(enumeration.Current);
        }
        return list;
    }

    private static FakeFileEnumerationSource SourceOf(IEnumerable<FileEntryInfo> files)
    {
        var source = new FakeFileEnumerationSource();
        source.Files.AddRange(files);
        return source;
    }

    private static string Reference(string path) => Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(path)));

    private sealed record RunSnapshot(List<ScanRowSnapshot> Rows, List<ScanErrorSnapshot> Errors, string FinishedLine, List<string> Problems, long Files, long Bytes);

    private RunSnapshot RunAndSnapshot(MyDupFinderScanJobDTO job, IFileEnumerationSource? source = null, FileHasher? hasher = null)
    {
        var log = new ListLoggerProvider();
        var runner = RunToEnd(job, log, source, hasher);
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(0, runner.RunningStageCount);
        var (rows, errors) = PipelineTestSupport.ReadSnapshot(job.DatabaseFile);
        string finished = Assert.Single(log.Entries, e => e.Message.StartsWith("Scanning finished...", StringComparison.Ordinal)).Message;
        var problems = log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => $"{e.Level}: {e.Message}").OrderBy(m => m, StringComparer.Ordinal).ToList();
        return new RunSnapshot(rows, errors, finished, problems, runner.Throughput.Files, runner.Throughput.BytesHashed);
    }

    private static void AssertSame(RunSnapshot expected, RunSnapshot actual, string label)
    {
        Assert.True(expected.Rows.SequenceEqual(actual.Rows), $"{label}: the stored rows differ from the sequential scan");
        Assert.True(expected.Errors.SequenceEqual(actual.Errors), $"{label}: the error items differ from the sequential scan");
        Assert.Equal(expected.FinishedLine, actual.FinishedLine);
        Assert.Equal(expected.Problems, actual.Problems);
        Assert.Equal(expected.Files, actual.Files);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }

    private static TestTreeOptions OptionsFor(string name) => name switch
    {
        "mixed" => new TestTreeOptions { Seed = 11, FileCount = 300, DuplicateShare = 0.2, DirectoryCount = 15 },
        "tiny" => new TestTreeOptions { Seed = 12, FileCount = 800, DuplicateShare = 0.4, DirectoryCount = 10, SizeBuckets = new[] { new SizeBucket(1.0, 64, 200) } },
        "empty" => new TestTreeOptions
        {
            Seed = 13,
            FileCount = 200,
            DuplicateShare = 0.3,
            DirectoryCount = 5,
            SizeBuckets = new[] { new SizeBucket(0.3, 0, 0), new SizeBucket(0.6, 1, 100), new SizeBucket(0.1, 2 * 1024 * 1024, 3 * 1024 * 1024) },
        },
        "dups" => new TestTreeOptions { Seed = 14, FileCount = 300, DuplicateShare = 0.8, DirectoryCount = 20, SizeBuckets = new[] { new SizeBucket(0.9, 64, 2048), new SizeBucket(0.1, 256 * 1024, 1024 * 1024) } },
        _ => throw new ArgumentException("unknown tree", nameof(name)),
    };

    // ================= equivalence with the sequential scan =================

    [Theory]
    [InlineData("mixed")]
    [InlineData("tiny")]
    [InlineData("empty")]
    [InlineData("dups")]
    public void Pipeline_StoresTheSameRowsAsTheSequentialScan(string treeName)
    {
        var tree = TestTreeGenerator.Generate(TreeDir(treeName), OptionsFor(treeName));
        RunSnapshot sequential = RunAndSnapshot(Job(tree.Root, treeName + "_1", 1));
        Assert.Equal(tree.Files.Count, sequential.Rows.Count);

        foreach (int workers in new[] { 2, 4, 8 })
        {
            RunSnapshot pipeline = RunAndSnapshot(Job(tree.Root, treeName + "_" + workers, workers));
            AssertSame(sequential, pipeline, $"{treeName}, {workers} workers");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pipeline_WithInjectedProblems_StoresTheSameRowsAndErrorsAsTheSequentialScan(bool trust)
    {
        var tree = TestTreeGenerator.Generate(TreeDir("injected"), new TestTreeOptions { Seed = 21, FileCount = 120, DuplicateShare = 0.2, DirectoryCount = 6, SizeBuckets = new[] { new SizeBucket(1.0, 64, 3000) } });
        List<FileEntryInfo> real = ListFiles(tree.Root);
        // A file whose metadata cannot be read (it does not exist): without TrustDirectoryMetadata the metadata call fails, with it the hashing fails
        FileEntryInfo ghost = FakeFileEnumerationSource.Entry(Path.Combine(tree.Root, "ghost.bin"), 10, DateTime.UtcNow);
        var source = SourceOf(real.Take(5).Append(ghost).Concat(real.Skip(5)));
        // An unreadable directory: its files are not in the list, the enumeration reports it
        source.UnreadableDirectories.Add(Path.Combine(tree.Root, "locked"));
        source.UnreadableFoundAtFileIndex = 3;
        // A file that fails to open
        string failing = real[7].FullPath;
        FileHasher NewHasher() => new FileHasher
        {
            BeforeOpen = path =>
            {
                if (string.Equals(path, failing, StringComparison.Ordinal))
                {
                    throw new IOException("injected: cannot open the file");
                }
            },
        };

        RunSnapshot sequential = RunAndSnapshot(Job(tree.Root, "inj_1", 1, trust), source, NewHasher());
        Assert.Equal(real.Count - 1, sequential.Rows.Count);
        Assert.Contains(sequential.Errors, e => e.FilenameAndPath == failing && e.ExceptionType.EndsWith("IOException", StringComparison.Ordinal));
        Assert.Equal(trust, sequential.Errors.Any(e => e.FilenameAndPath == ghost.FullPath));
        Assert.Contains(sequential.Problems, p => p.Contains("could not be read completely", StringComparison.Ordinal));

        foreach (int workers in new[] { 2, 4, 8 })
        {
            RunSnapshot pipeline = RunAndSnapshot(Job(tree.Root, "inj_" + workers, workers, trust), source, NewHasher());
            AssertSame(sequential, pipeline, $"injected problems, trust={trust}, {workers} workers");
        }
    }

    [Fact]
    public void OneWorker_HashesOnTheCallingThread_SeveralWorkersDoNot()
    {
        var tree = TestTreeGenerator.Generate(TreeDir("threads"), new TestTreeOptions { Seed = 22, FileCount = 30, DirectoryCount = 3, SizeBuckets = new[] { new SizeBucket(1.0, 64, 500) } });
        var sequentialThreads = new System.Collections.Concurrent.ConcurrentBag<int>();
        var pipelineThreads = new System.Collections.Concurrent.ConcurrentBag<int>();

        int callerThread = Environment.CurrentManagedThreadId;
        using (var serviceProvider = PipelineTestSupport.BuildServices())
        {
            var runner = NewRunner(Job(tree.Root, "t1", 1), serviceProvider, hasher: new FileHasher { BeforeOpen = p => sequentialThreads.Add(Environment.CurrentManagedThreadId) });
            runner.Start(CancellationToken.None);
        }
        RunToEnd(Job(tree.Root, "t4", 4), hasher: new FileHasher { BeforeOpen = p => pipelineThreads.Add(Environment.CurrentManagedThreadId) });

        Assert.Equal(30, sequentialThreads.Count);
        Assert.All(sequentialThreads, id => Assert.Equal(callerThread, id));
        Assert.Equal(30, pipelineThreads.Count);
        Assert.All(pipelineThreads, id => Assert.NotEqual(callerThread, id));
        Assert.InRange(pipelineThreads.Distinct().Count(), 1, 4);
    }

    // ================= exactly once =================

    [Fact]
    public void Pipeline_EightWorkers_StoresEveryFileExactlyOnce_AlsoWhenRepeated()
    {
        var tree = TestTreeGenerator.Generate(TreeDir("once"), new TestTreeOptions { Seed = 31, FileCount = 3000, DuplicateShare = 0.3, DirectoryCount = 30, SizeBuckets = new[] { new SizeBucket(1.0, 64, 120) } });
        var expected = tree.Files.Select(f => Path.Combine(tree.Root, f.RelativePath)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        for (int run = 0; run < 3; run++)
        {
            // A small batch size so that many flushes happen while the producer, the workers and the writer are all busy
            using var serviceProvider = PipelineTestSupport.BuildServices();
            var inserts = new ScanJobDBInserts(serviceProvider.GetService<ILogger<ScanJobDBInserts>>(), 37, TimeSpan.FromHours(1));
            var runner = RunToEnd(Job(tree.Root, "once" + run, 8), inserts: inserts);

            Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
            var (rows, errors) = PipelineTestSupport.ReadSnapshot(DbPath("once" + run));
            Assert.Empty(errors);
            Assert.Equal(expected.Count, rows.Count);
            Assert.Equal(expected, rows.Select(r => r.FilenameAndPath).ToList());
            Assert.Equal(rows.Count, rows.Select(r => r.FilenameAndPath).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(expected.Count, runner.Throughput.Files);
            Assert.Equal(tree.TotalBytes, runner.Throughput.BytesHashed);
            Assert.Equal(expected.Count, inserts.TotalSuccessCount);
            Assert.True(runner.MaxItemsInFlight <= runner.InFlightLimit);
        }
    }

    // ================= cancel =================

    [Fact]
    public void Cancel_WithFilesInAllThreeStages_WritesEveryCompletedResult_AndNothingForInterruptedFiles_AndRestartCompletes()
    {
        const int workers = 2;
        const int completedFiles = 9;   // 1 held by the writer + 8 (the capacity of the result queue) waiting for it
        var tree = TestTreeGenerator.Generate(TreeDir("cancel1"), new TestTreeOptions { Seed = 41, FileCount = 250, DuplicateShare = 0.1, DirectoryCount = 5, SizeBuckets = new[] { new SizeBucket(1.0, 64, 400) } });
        List<FileEntryInfo> files = ListFiles(tree.Root);
        var source = SourceOf(files);
        // Files 0..8 are hashed normally. A worker that starts any later file parks before it opens it (stage "hash")
        var parkedPaths = files.Skip(completedFiles).Select(f => f.FullPath).ToHashSet(StringComparer.Ordinal);
        using var parked = new CountdownEvent(workers);
        using var releaseWorkers = new ManualResetEventSlim(false);
        using var writerHolding = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        var hasher = new FileHasher
        {
            BeforeOpen = path =>
            {
                if (parkedPaths.Contains(path))
                {
                    parked.Signal();
                    PipelineTestSupport.Wait(releaseWorkers, "release of the parked workers");
                }
            },
        };
        int writerCalls = 0;
        var log = new ListLoggerProvider();
        using var cts = new CancellationTokenSource();
        using (var serviceProvider = PipelineTestSupport.BuildServices(log))
        {
            var runner = NewRunner(Job(tree.Root, "cancel1", workers), serviceProvider, source: source, hasher: hasher);
            // The writer holds the first result it has taken (stage "write")
            runner.WriterResultHook = item =>
            {
                if (Interlocked.Increment(ref writerCalls) == 1)
                {
                    writerHolding.Set();
                    PipelineTestSupport.Wait(releaseWriter, "release of the writer");
                }
            };
            Task run = PipelineTestSupport.StartInBackground(runner, cts.Token);

            PipelineTestSupport.Wait(parked, "all workers parked in a file");
            PipelineTestSupport.Wait(writerHolding, "the writer holding a result");
            // Everything is full: 1 in the writer, 8 results queued, 2 in the workers, 8 in the work queue, 1 held by the producer
            PipelineTestSupport.WaitUntil(() => runner.MaxItemsInFlight >= runner.InFlightLimit, "the pipeline to be full");
            Assert.Equal(runner.InFlightLimit, runner.MaxItemsInFlight);

            cts.Cancel();
            releaseWorkers.Set();
            releaseWriter.Set();
            PipelineTestSupport.WaitForEnd(run);
            SqliteConnection.ClearAllPools();

            Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
            Assert.Equal(0, runner.RunningStageCount);
            Assert.Equal(completedFiles, runner.Throughput.Files);
            Assert.Equal(completedFiles, writerCalls);
        }
        var (rows, errors) = PipelineTestSupport.ReadSnapshot(DbPath("cancel1"));
        Assert.Equal(files.Take(completedFiles).Select(f => f.FullPath).OrderBy(p => p, StringComparer.Ordinal), rows.Select(r => r.FilenameAndPath));
        Assert.Empty(errors);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Message.StartsWith("Scanning was cancelled by user", StringComparison.Ordinal));

        // Restart: the scan completes and the database equals the one of a scan that was never interrupted
        Assert.Equal(IService.EServiceState.finished, RunToEnd(Job(tree.Root, "cancel1", 4), source: source).RunnerState);
        RunToEnd(Job(tree.Root, "cancel1_whole", 1), source: source);
        AssertSameContent("cancelled and restarted", "cancel1_whole", "cancel1");
    }

    [Fact]
    public void Cancel_InsideLargeFilesWithSeveralWorkers_StoresOnlyCompletedFiles_NoErrorItems_AndRestartCompletes()
    {
        const int workers = 4;
        const int completedFiles = 3;
        var tree = TestTreeGenerator.Generate(TreeDir("cancel2"), new TestTreeOptions { Seed = 42, FileCount = 14, DuplicateShare = 0, DirectoryCount = 3, SizeBuckets = new[] { new SizeBucket(1.0, 8 * Block, 12 * Block) } });
        List<FileEntryInfo> files = ListFiles(tree.Root);
        var parkedPaths = files.Skip(completedFiles).Select(f => f.FullPath).ToHashSet(StringComparer.Ordinal);
        using var parked = new CountdownEvent(workers);
        using var releaseWorkers = new ManualResetEventSlim(false);
        // The hasher reads blocks of 1 KB; every worker parks after the second block of a file (inside the file)
        var hasher = new FileHasher(Block)
        {
            AfterBufferRead = (path, block) =>
            {
                if (block == 2 && parkedPaths.Contains(path))
                {
                    parked.Signal();
                    PipelineTestSupport.Wait(releaseWorkers, "release of the parked workers");
                }
            },
        };
        var log = new ListLoggerProvider();
        using var cts = new CancellationTokenSource();
        using (var serviceProvider = PipelineTestSupport.BuildServices(log))
        {
            var runner = NewRunner(Job(tree.Root, "cancel2", workers), serviceProvider, hasher: hasher);
            Task run = PipelineTestSupport.StartInBackground(runner, cts.Token);

            PipelineTestSupport.Wait(parked, "all workers inside a file");
            cts.Cancel();
            releaseWorkers.Set();
            PipelineTestSupport.WaitForEnd(run);
            SqliteConnection.ClearAllPools();

            Assert.Equal(IService.EServiceState.cancelled, runner.RunnerState);
            Assert.Equal(0, runner.RunningStageCount);
            Assert.Equal(completedFiles, runner.Throughput.Files);
        }
        var (rows, errors) = PipelineTestSupport.ReadSnapshot(DbPath("cancel2"));
        Assert.Equal(files.Take(completedFiles).Select(f => f.FullPath).OrderBy(p => p, StringComparer.Ordinal), rows.Select(r => r.FilenameAndPath));
        Assert.Empty(errors);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);

        Assert.Equal(IService.EServiceState.finished, RunToEnd(Job(tree.Root, "cancel2", 4)).RunnerState);
        RunToEnd(Job(tree.Root, "cancel2_whole", 1));
        AssertSameContent("cancelled inside files and restarted", "cancel2_whole", "cancel2");
        foreach (var row in PipelineTestSupport.ReadSnapshot(DbPath("cancel2")).Rows)
        {
            Assert.Equal(Reference(row.FilenameAndPath), row.Hash);
        }
    }

    private void AssertSameContent(string label, string expectedDb, string actualDb)
    {
        var expected = PipelineTestSupport.ReadSnapshot(DbPath(expectedDb));
        var actual = PipelineTestSupport.ReadSnapshot(DbPath(actualDb));
        Assert.True(expected.Rows.SequenceEqual(actual.Rows), $"{label}: the rows differ from an uninterrupted scan");
        Assert.True(expected.Errors.SequenceEqual(actual.Errors), $"{label}: the error items differ from an uninterrupted scan");
    }

    // ================= failures =================

    [Fact]
    public void FatalWriteFailure_WithFourWorkers_EndsTheRun_StopsHashing_AndIsNotRecordedAsFileError()
    {
        var tree = TestTreeGenerator.Generate(TreeDir("fatal"), new TestTreeOptions { Seed = 51, FileCount = 200, DuplicateShare = 0, DirectoryCount = 5, SizeBuckets = new[] { new SizeBucket(1.0, 64, 300) } });
        // Create the database and let SQLite refuse the insert of one file name (a real SQLite error at commit time)
        using (var prepare = new ScanJobDBInserts(null))
        {
            prepare.SetupDB(DbPath("fatal"));
        }
        string poisoned = Path.GetFileName(tree.Files[20].RelativePath);
        using (var connection = new SqliteConnection($"Data Source={DbPath("fatal")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER poison BEFORE INSERT ON ScanItems WHEN NEW.Filename = '{poisoned}' BEGIN SELECT RAISE(ABORT, 'poisoned'); END;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var log = new ListLoggerProvider();
        int hashStarts = 0;
        var inserts = new ScanJobDBInserts(null, 10, TimeSpan.FromHours(1));

        var runner = RunToEnd(Job(tree.Root, "fatal", 4), log, hasher: new FileHasher { BeforeOpen = p => Interlocked.Increment(ref hashStarts) }, inserts: inserts);

        var (rows, errors) = PipelineTestSupport.ReadSnapshot(DbPath("fatal"));
        Assert.True(rows.Count < tree.Files.Count);
        Assert.DoesNotContain(rows, r => r.Filename == poisoned);
        Assert.Equal(0, rows.Count % 10);
        Assert.Empty(errors);
        Assert.Equal(IService.EServiceState.failed, runner.RunnerState);
        Assert.Equal(0, runner.RunningStageCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("database write failure", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Hashing of", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Scanning finished", StringComparison.Ordinal));
        // The producer and the workers were stopped: only the files in flight were started after the failing batch, not all 200
        Assert.True(hashStarts < 150, $"{hashStarts} files were started, hashing went on after the failure");
        Assert.True(runner.MaxItemsInFlight <= runner.InFlightLimit);
    }

    [Fact]
    public void ProducerFailure_AfterTheFirstBatch_EndsLikeErrorInScan_AfterTheHashedResultsWereWritten()
    {
        var tree = TestTreeGenerator.Generate(TreeDir("producer"), new TestTreeOptions { Seed = 52, FileCount = 250, DuplicateShare = 0, DirectoryCount = 5, SizeBuckets = new[] { new SizeBucket(1.0, 64, 300) } });
        List<FileEntryInfo> files = ListFiles(tree.Root);
        var source = SourceOf(files);
        // The enumeration reads 100 files per batch: the first batch is complete and handed over when the listing fails in the second one
        source.BeforeFile = index =>
        {
            if (index == 150)
            {
                throw new IOException("injected: the listing failed");
            }
        };
        var log = new ListLoggerProvider();

        var runner = RunToEnd(Job(tree.Root, "producer", 4), log, source);

        var (rows, errors) = PipelineTestSupport.ReadSnapshot(DbPath("producer"));
        Assert.Equal(files.Take(100).Select(f => f.FullPath).OrderBy(p => p, StringComparer.Ordinal), rows.Select(r => r.FilenameAndPath));
        Assert.Empty(errors);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message == "Error in Scan!");
        Assert.Equal(IService.EServiceState.failed, runner.RunnerState);
        Assert.Equal(0, runner.RunningStageCount);
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Scanning finished", StringComparison.Ordinal));
    }

    [Fact]
    public void UnexpectedWorkerException_StopsThePipeline_IsLogged_AndKeepsTheResultsAlreadyWritten()
    {
        var tree = TestTreeGenerator.Generate(TreeDir("worker"), new TestTreeOptions { Seed = 53, FileCount = 200, DuplicateShare = 0, DirectoryCount = 5, SizeBuckets = new[] { new SizeBucket(1.0, 64, 300) } });
        List<FileEntryInfo> files = ListFiles(tree.Root);
        string target = files[60].FullPath;
        using var tenWritten = new ManualResetEventSlim(false);
        int written = 0;
        var log = new ListLoggerProvider();

        var runner = RunToEnd(Job(tree.Root, "worker", 4), log, configure: r =>
        {
            r.WriterResultHook = item =>
            {
                if (Interlocked.Increment(ref written) == 10)
                {
                    tenWritten.Set();
                }
            };
            r.WorkerItemHook = item =>
            {
                if (string.Equals(item.FilenameAndPath, target, StringComparison.Ordinal))
                {
                    // Not about the file: fails after the writer has recorded ten results
                    PipelineTestSupport.Wait(tenWritten, "ten results written");
                    throw new InvalidOperationException("injected worker failure");
                }
            };
        });

        var (rows, errors) = PipelineTestSupport.ReadSnapshot(DbPath("worker"));
        Assert.True(rows.Count >= 10, $"{rows.Count} rows");
        Assert.True(rows.Count < files.Count);
        Assert.DoesNotContain(rows, r => r.FilenameAndPath == target);
        Assert.Equal(rows.Count, rows.Select(r => r.FilenameAndPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, r => Assert.Equal(Reference(r.FilenameAndPath), r.Hash));
        Assert.Empty(errors);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("hash worker failed unexpectedly", StringComparison.Ordinal));
        Assert.Equal(IService.EServiceState.failed, runner.RunnerState);
        Assert.Equal(0, runner.RunningStageCount);
    }

    // ================= back-pressure =================

    [Fact]
    public void BackPressure_BlockedWorkers_KeepTheFilesInFlightBounded_ForATreeFarLargerThanTheQueues()
    {
        const int workers = 2;
        var tree = TestTreeGenerator.Generate(TreeDir("bp1"), new TestTreeOptions { Seed = 61, FileCount = 400, DuplicateShare = 0.1, DirectoryCount = 5, SizeBuckets = new[] { new SizeBucket(1.0, 64, 200) } });
        using var parked = new CountdownEvent(workers);
        using var release = new ManualResetEventSlim(false);
        int hashStarts = 0;
        var hasher = new FileHasher
        {
            BeforeOpen = path =>
            {
                if (Interlocked.Increment(ref hashStarts) <= workers)
                {
                    parked.Signal();
                    PipelineTestSupport.Wait(release, "release of the workers");
                }
            },
        };
        using var serviceProvider = PipelineTestSupport.BuildServices();
        var runner = NewRunner(Job(tree.Root, "bp1", workers), serviceProvider, hasher: hasher);
        Task run = PipelineTestSupport.StartInBackground(runner, CancellationToken.None);

        PipelineTestSupport.Wait(parked, "all workers parked");
        // The producer fills the work queue (8) and then waits holding one more; the two files of the workers are in flight as well
        int expectedFull = (workers * ScanRunner.QueueCapacityPerWorker) + workers + 1;
        PipelineTestSupport.WaitUntil(() => runner.MaxItemsInFlight >= expectedFull, "the work queue to be full");
        Assert.Equal(expectedFull, runner.MaxItemsInFlight);
        // ... and it does not grow while the workers are blocked
        Assert.Equal(workers, Volatile.Read(ref hashStarts));
        release.Set();
        PipelineTestSupport.WaitForEnd(run);
        SqliteConnection.ClearAllPools();

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.True(runner.MaxItemsInFlight <= runner.InFlightLimit, $"{runner.MaxItemsInFlight} in flight, limit {runner.InFlightLimit}");
        Assert.Equal(400, PipelineTestSupport.ReadSnapshot(DbPath("bp1")).Rows.Count);
    }

    [Fact]
    public void BackPressure_SlowWriter_KeepsTheFilesInFlightAtTheLimit_ForATreeFarLargerThanTheQueues()
    {
        const int workers = 2;
        var tree = TestTreeGenerator.Generate(TreeDir("bp2"), new TestTreeOptions { Seed = 62, FileCount = 400, DuplicateShare = 0.1, DirectoryCount = 5, SizeBuckets = new[] { new SizeBucket(1.0, 64, 200) } });
        using var writerHolding = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        int writerCalls = 0;
        using var serviceProvider = PipelineTestSupport.BuildServices();
        var runner = NewRunner(Job(tree.Root, "bp2", workers), serviceProvider);
        runner.WriterResultHook = item =>
        {
            if (Interlocked.Increment(ref writerCalls) == 1)
            {
                writerHolding.Set();
                PipelineTestSupport.Wait(releaseWriter, "release of the writer");
            }
        };
        Task run = PipelineTestSupport.StartInBackground(runner, CancellationToken.None);

        PipelineTestSupport.Wait(writerHolding, "the writer holding a result");
        // Writer 1 + result queue 8 + workers 2 + work queue 8 + producer 1: full, and never more
        PipelineTestSupport.WaitUntil(() => runner.MaxItemsInFlight >= runner.InFlightLimit, "the pipeline to be full");
        Assert.Equal(runner.InFlightLimit, runner.MaxItemsInFlight);
        releaseWriter.Set();
        PipelineTestSupport.WaitForEnd(run);
        SqliteConnection.ClearAllPools();

        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        Assert.Equal(runner.InFlightLimit, runner.MaxItemsInFlight);
        Assert.Equal(400, PipelineTestSupport.ReadSnapshot(DbPath("bp2")).Rows.Count);
    }
}
