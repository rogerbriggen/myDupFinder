# Benchmark

A scan benchmark that creates its own test files, so a change in the scan can be measured before and after (see step 0 in [speed-up-plan.md](speed-up-plan.md)).

## How to run

The benchmark is a test that is skipped unless the environment variable `MYDUPFINDER_BENCHMARK` is `1`. A normal `dotnet test` does not run it.

PowerShell:

```powershell
$env:MYDUPFINDER_BENCHMARK = "1"
dotnet test tests/rogerbriggen.myDupFinderLibUnitTest --configuration Release --filter "FullyQualifiedName~ScanBenchmark" --logger "console;verbosity=detailed"
Remove-Item Env:MYDUPFINDER_BENCHMARK
```

bash:

```bash
MYDUPFINDER_BENCHMARK=1 dotnet test tests/rogerbriggen.myDupFinderLibUnitTest --configuration Release --filter "FullyQualifiedName~ScanBenchmark" --logger "console;verbosity=detailed"
```

Use the Release configuration, Debug hashes much slower. The detailed console logger is needed to see the test output.

Optional: `MYDUPFINDER_BENCHMARK_FILES` sets the number of files (default 5000, about 700 MB at the default size mix).

Optional: `MYDUPFINDER_BENCHMARK_TRUST=1` runs the jobs of the benchmarks with the job setting `TrustDirectoryMetadata` (size and timestamps from the directory listing, no call per file). Without it the default is measured: the metadata of every file is read from the file itself (one call per file), which is the safe mode. The benchmark prints a `Mode:` line first.

Optional: `MYDUPFINDER_BENCHMARK_WORKERS=n` (1 to 64, default 1) runs the scan jobs of the benchmarks with the job setting `HashWorkers = n` (step 7): 1 is the sequential scan, more is the pipeline (one producer, n hash workers, one database writer). The `Mode:` line shows the worker count and the number of logical processors of the machine. With several workers the `hash` stage time in the summary is the **sum over all workers**: it can be larger than the elapsed time (for example 4 workers that are each busy for 5 s give hash=20 s). Compare the total time and the MB/s, not the stage times, between worker counts. `Scan_ManyTinyFiles_ReportsTimings` (20000 files of 64 to 512 bytes, no duplicates; run it alone with `--filter "FullyQualifiedName~Scan_ManyTinyFiles"`) is the database-bound case: it shows where more workers stop helping. Note that the filter `FullyQualifiedName~Scan_GeneratedTree` also matches `Rescan_GeneratedTree_ReportsResumeTimings` (the name contains it); use `FullyQualifiedName~ScanBenchmark.Scan_GeneratedTree` for the main benchmark alone.

Optional: `MYDUPFINDER_BENCHMARK_SQLITE` selects SQLite settings for the benchmarks (step 8): a comma separated list of `normal` (the job setting `DatabaseSynchronous=Normal` for the scan jobs; `ApplyCheckBenchmark` has no job setting, applyCheck always uses Full, so there `normal` is applied as a plain `PRAGMA synchronous=NORMAL` for the measurement only; find-dups only reads, so it changes nothing there), `cache=n` (`PRAGMA cache_size` of n MB, for example `cache=64`) and `temp=memory` (`PRAGMA temp_store=MEMORY`). Example: `MYDUPFINDER_BENCHMARK_SQLITE=normal,cache=64,temp=memory`. Not set: the defaults, which is what a normal run uses. The `Mode:` line shows the settings. The pragmas are applied on every connection that EF Core opens, through an internal hook (`MeasurementPragmas`) that is empty in every normal run and is reset by the benchmark classes; it is process wide, so run these benchmarks alone (a filter on the benchmark class), never together with the normal tests. Use `MYDUPFINDER_BENCHMARK_FILES=20000` for the scan benchmarks (`Scan_ManyTinyFiles`, `Rescan_GeneratedTree`; default 5000) to make the database part big enough to see.

## What it does

1. Generates a deterministic tree in a temporary directory: nested directories, a mix of file sizes (70 % between 64 B and 4 KB, 25 % up to 256 KB, 5 % up to 4 MB) and 20 % of the files as copies of another file. The same options always give the same tree (seeded `Random`, no external data).
2. Runs a scan job on it from start to end against a new temporary database, in the test thread, without throttling.
3. Writes two lines to the test output and deletes the temporary directory (tree and database) afterwards. The benchmark fails if any file failed or is missing in the database (error rows, row count and hashed bytes are checked).

The tree generator is `TestTreeGenerator` in the test project and can be used by other tests.

## The numbers

```
Tree: 5000 files, 719.7 MB, 4000 unique contents, 1000 duplicate files, generated in 2.5 s (not part of the scan time)
Scan: 5000 files, 719.7 MB hashed in 8.3 s. Average 601.0 files/s, 86.5 MB/s. Stage time: enumerate=0.2 s, hash=1.5 s, database=6.3 s
```

(Example from one run on the development machine; your numbers will differ. Note how the database stage dominates: one transaction per file, see step 2 of the plan.)

- **files**: files handled. Includes files that were skipped (already in the database, unchanged) and files that failed. Hashed bytes are only counted for files that were read and hashed successfully, so a skipped file adds a file but no bytes.
- **MB**: 1024 * 1024 bytes.
- **elapsed**: wall clock time of the run from after the database was opened. It includes everything, also sleeping in the throttle.
- **Average files/s, MB/s**: totals divided by elapsed.
- **Stage time**: time the runner spent in
  - `enumerate`: walking the directories, including the metadata (size, creation and last write time) that comes with the directory listing. The same in scan, refresh and check (since step 4 all three use the same enumerator): the directory listing, and, unless `TrustDirectoryMetadata` is set, the one call per file that reads size and timestamps from the file itself,
  - `hash`: opening, reading, hashing (SHA-512) and closing the files,
  - `database`: everything done with the database, i.e. resume lookups, inserts, updates, removals and commits (check: the initial load of the rows).

  The stages do not add up to elapsed exactly: code that no stage measures (and the throttle) is missing.

The same progress line (every 10 seconds) and summary line are written by the scan, refresh and check jobs to the normal log, see the README.

## Resume benchmark

A second benchmark, `Rescan_GeneratedTree_ReportsResumeTimings`, measures the resume filter (step 3 of the plan). It uses the same `MYDUPFINDER_BENCHMARK=1` gate and the same `MYDUPFINDER_BENCHMARK_FILES` setting. Run only this one with `--filter "FullyQualifiedName~Rescan_Generated"`.

1. Generates a tree of small files (90 % between 64 B and 4 KB, 10 % up to 64 KB; the files are not hashed in the second run, so their size does not matter) and scans it into a new temporary database (`First scan:` line).
2. Scans the same tree again with the same job. Every file is already in the database, so the resume filter (`LookupForResume`, one query per file) skips all of them.
3. Writes the `Scan:` line of the second run. The benchmark fails unless the second run handled all files, hashed 0 bytes, and the database still has the same number of rows and no error rows.

Use at least 20000 files (`MYDUPFINDER_BENCHMARK_FILES=20000`); with fewer files the lookups are too fast to see a difference. The line to look at is the second one, mainly `database=` in the stage times:

```
Scan: 20000 files, 0.0 MB hashed in 1.8 s. Average 11379.1 files/s, 0.0 MB/s. Stage time: enumerate=0.8 s, hash=0.0 s, database=0.9 s
```

Numbers before and after the `FilenameAndPath` index are in the Result section of step 3 in [speed-up-plan.md](speed-up-plan.md).

## Check benchmark (IgnoreBasePath)

A third benchmark, `Check_IgnoreBasePath_MovedTree_ReportsTimings`, measures the lookup of rows by relative path in a check with `IgnoreBasePath` (step 4 of the plan). Same `MYDUPFINDER_BENCHMARK=1` gate; `MYDUPFINDER_BENCHMARK_FILES` sets the number of files (default for this test: 20000). Run only this one with `--filter "FullyQualifiedName~Check_IgnoreBasePath"`.

1. Generates a tree of small files and scans it into a new temporary database.
2. Renames the tree directory, so no stored path matches a path on disk.
3. Checks the renamed tree with `IgnoreBasePath` and `SkipHashCheck` (the files are not read, so the time is the matching of rows, not hashing). Every file has to be found by its relative path.
4. Writes one line with the total time and the stage times. The benchmark fails unless all files are reported as moved (`ModifiedNoHashChange`), none as new or missing and no error occurred.

Numbers before and after the dictionary lookup are in the Result section of step 4 in [speed-up-plan.md](speed-up-plan.md).

## Find-dups benchmark

`FindDupsBenchmark` (step 5 of the plan) measures the find-duplicates job in all four modes on two generated databases. Same `MYDUPFINDER_BENCHMARK=1` gate. Run it with `--filter "FullyQualifiedName~FindDupsBenchmark"`, or only the main one with `--filter "FullyQualifiedName~FindDups_AllModes"`.

`MYDUPFINDER_BENCHMARK_FILES` is the number of rows **per database** (default 100000). No files are created on disk: the generator (`FindDupsTestData` in the test project, seeded `Random`, so the same options always give the same databases) writes the rows straight into two new databases in batches of 5000 (about 15 s for 2 x 100000 rows; this is not part of the times).

The databases: the base database has paths under `/bench/base`, the second one under `/bench/second`. 15 % of the rows of each database are copies of another row of the same database (duplicate hashes inside one database), 0.2 % have no hash. The second database is a changed copy of the base one: about 40 % of its rows have the same relative path and the same hash as the base row (`Duplicate`), 8 % the same path and another hash (`Changed`), 12 % the hash of another base file at another path (`Moved`), 12 % copies of a file of the second database, 8 % new files, and 20 % the same content at a different path. For 100000 rows that makes 84966 distinct hashes in the base database, 73445 in the second one and 57330 in common.

`FindDups_AllModes_ReportTimings` runs the four modes (same database with `FindOnlyDups` and `FindDupsTheWholeLot`, two databases with each) and prints one table row per mode:

- **Query part**: the time of the database queries of the mode on a database that is already open (same-db only dups: `FindDupsInSameDB`; same-db whole lot: `GetAllScanItems`; two databases, only dups: `FindDupsInDifferentDBs`; two databases, whole lot: `GetAllScanItems` and `GetAllScanItemsFromOtherDB`). Includes materializing the rows.
- **Total with report**: the complete job from a new runner to the finished report file: open the database, queries, build the rows, summary, write the CSV report. The report writer is not streaming: all rows are in memory.
- **Report rows**: lines in the report without the header.
- **Peak managed memory above start**: the highest managed heap size (sampled every 5 ms, includes garbage that is not collected yet) during the total run minus the heap size after a full collection before it.

A mode that fails (before step 5 the cross-database only-dups query fails with `too many SQL variables` above about 32500 distinct hashes in the second database) is printed as `FAILED: ...` and does not stop the other modes; the benchmark fails at the end.

Two more benchmarks in the same class (same gate):

- `CrossDatabaseQuery_ReferenceVersusAttached_ReportsTimings` times the cross-database only-dups query alone, with a growing number of distinct hashes in the second database (`MYDUPFINDER_BENCHMARK_HASHES`, comma separated, default `10000,30000,100000,300000`), for the implementation from before step 5 (`ReferenceFindDups` in the test project) and for the attached query.
- `CrossDatabaseQuery_AsymmetricPairs_ReportTimings` times the same query for databases of very different sizes (300000 / 10, 300000 / 1000, 10 / 300000, 1000 / 300000 and 300000 / 300000 base / second rows, each with a high and a low share of common hashes; about a minute): the old query, the `EXISTS` form, the `IN` form and the form the code chooses, best of two runs, materializing the rows.
- `CrossDatabaseQuery_SizeEstimate_ReportsCost` times the size estimate that chooses the form (`MAX(rowid)`) against `COUNT(*)` (`MYDUPFINDER_BENCHMARK_FILES` rows, default 300000).
- `Tracking_MemoryOfTheRows_ReportsComparison` prints the managed memory of the rows of one query (`MYDUPFINDER_BENCHMARK_FILES` rows) with and without change tracking.

Numbers are in the Result section of step 5 in [speed-up-plan.md](speed-up-plan.md).

## applyCheck benchmark

`ApplyCheckBenchmark.Apply_MixedReport_ReportsTimings` measures applying a check report. Same `MYDUPFINDER_BENCHMARK=1` gate; `MYDUPFINDER_BENCHMARK_FILES` is the number of report rows (default 30000). Run it with `--filter "FullyQualifiedName~ApplyCheckBenchmark"`. No files are created: the generator writes a database and a report directly (40 % `Modified`, 10 % `BitRotSuspect`, 10 % `ModifiedNoHashChange`, 10 % `MissingOnDisk`, 30 % `NewOnDisk`; the first four need a database row).

1. Copies the generated database twice.
2. "Before": applies the rows to the first copy the way applyCheck did before this change: one `SaveChanges` (one commit) per row with `ApplyUpdateById`, `RemoveItemById` and a plain `InsertItem` (no lookup of the path).
3. "After": applies the report to the second copy with the real `CheckReportApplier` (transactions of 500 rows, an insert looks for the path first).
4. Compares both databases (path, base path, size, modification date, hash), then applies the report a second time and prints its time. The benchmark fails if the tables differ, if a counter is wrong or if the second apply inserts anything.

```
Report: 30000 rows (database with 21000 rows): 12000 Modified, 3000 BitRotSuspect, 3000 ModifiedNoHashChange, 3000 MissingOnDisk, 9000 NewOnDisk
Before (one commit per row, plain insert): 37.7 s, 795 rows/s
After (transactions of 500 rows, insert looks for the path first): 2.8 s, 10814 rows/s. Updated=18000 Inserted=9000 Removed=3000 Skipped=0 Errors=0
Speed-up: 13.6x
Second apply of the same report: 1.8 s. Inserted=0 AlreadyApplied=9000 Skipped=3000
```

(One run on the development machine, temp folder on an SSD; the commit cost depends on the disk.)

## Hasher benchmark

`FileHasherBenchmark` (step 6 of the plan) compares the old hash expression (`File.OpenRead` + `ComputeHash`) with the shared `FileHasher` at the buffer sizes 64 KB, 256 KB, 1 MB and 4 MB, and with plain `FileStream` reads (1 MB and 4 MB blocks, with the stream's own buffer of 4096 bytes and without it), on one large file and on many small files. Same `MYDUPFINDER_BENCHMARK=1` gate. Run it with `--filter "FullyQualifiedName~FileHasherBenchmark"`, or one of the two with `--filter "FullyQualifiedName~Hasher_LargeFile"` / `"FullyQualifiedName~Hasher_ManySmallFiles"`.

- `Hasher_LargeFile_ReportsBufferSizes`: generates one file of `MYDUPFINDER_BENCHMARK_LARGE_MB` megabytes (default 1024; random content, deleted afterwards) in the temporary folder, so the disk of the temporary folder is the one measured.
- `Hasher_ManySmallFiles_ReportsBufferSizes`: generates `MYDUPFINDER_BENCHMARK_FILES` files (default 20000; 90 % between 64 B and 4 KB, 10 % up to 64 KB, 20 % duplicates) and hashes all of them one after the other.
- `MYDUPFINDER_BENCHMARK_RUNS` is the number of runs per variant (default 3). The order of the variants is rotated every run so that no variant always follows the same one. Every result is compared with the hash of the old expression, so a wrong hash fails the benchmark.
- The output is a table with the best and the median time per variant and MB/s (large file) or files/s (small files).
- **Cache.** A test cannot empty the cache of the operating system, so these are **warm cache** numbers: the large file was just written and stays in memory, and hashing (SHA-512, about 570 MB/s here) is the limit, not the disk. The effect of the block size on a cold hard disk or a network share is not measured by this benchmark; run it against a path on that medium (the temporary folder can be moved with the `TEMP` environment variable) after a restart or with another large file read in between.
- The `FileStream` variants reuse one block per size, so they do not measure allocating a block per file.

Numbers and the chosen default are in the Result section of step 6 in [speed-up-plan.md](speed-up-plan.md).
