# Speed-up plan for scanning and duplicate finding

Most of the time today is lost to deliberate sleeping, one database transaction per file, and redundant file system calls, not to hashing itself. The steps are ordered so the cheap, safe wins come first and the riskier design changes come last. Nothing here is measured yet; the gains are estimates from reading the code, so step 0 is a baseline.

Revision 2: reworked after review feedback from Sol. The main changes are a missing-index migration moved forward, safety rules for inaccessible directories, an explicit batch lifecycle, a more careful SQLite section, and semantic instead of byte-wise regression tests.

## Step 0 — Baseline

- Add a small benchmark harness (a test tree generator plus stopwatch logging per stage: enumerate, hash, DB write) so each later step can be verified.
- Log throughput (files/s, MB/s) every few seconds in the runners. This becomes the normal progress output once per-file messages are gone (step 1).
- Record the actual state of an existing database: `PRAGMA journal_mode`, `PRAGMA synchronous`, and the list of indexes. Steps 3 and 8 depend on it.

## Step 1 — Sleeps and logging

1. **Remove the fixed `Thread.Sleep(10)` per file.**
   - It is in [ScanRunnerSingleThread.cs](../src/rogerbriggen.myDupFinderLib/Scan/ScanRunnerSingleThread.cs) (line 118), [RefreshRunner.cs](../src/rogerbriggen.myDupFinderLib/Refresh/RefreshRunner.cs) (line 154) and [CheckRunner.cs](../src/rogerbriggen.myDupFinderLib/Check/CheckRunner.cs) (line 135).
   - On Windows a 10 ms sleep is usually about 15 ms, so 1 million files cost roughly 3–4 hours of pure waiting.
   - Refresh and check pay it even for unchanged files that are never hashed.
   - Make throttling a job setting (off by default), and let `DiscThrottle` be the only throttle.

2. **Turn per-file success messages off, not just down.**
   - `LogInformation("File {file} successfull finished")` is written for every file.
   - Lowering it to Debug is not enough: the default configuration in [Program.cs](../src/rogerbriggen.myDupFinder/Program.cs) (line 296) sets `MinimumLevel.Debug()`, so the message would still reach the file log.
   - Remove per-file success messages from normal operation (or put them behind an explicit diagnostic switch) and rely on the periodic progress line. Errors and warnings per file stay.

## Step 2 — Batching and change-tracker limits

**Diagnosis.** `Enqueue` in [ScanJobDBInserts.cs](../src/rogerbriggen.myDupFinderDB/ScanJobDBInserts.cs) (line 47) calls `WriteChangesInternal` every time, so each file is its own `SaveChanges` and its own SQLite transaction. `_commitCount = 100` batches nothing. Because `written` is always 1, the `written % _commitCount == 0` check never fires, so the context is never recreated and the change tracker grows for the whole run.

**Why a simple "flush at N items" is not enough.** Two existing gaps become data loss once writes are deferred:

- `Dispose()` calls `_dbContext.SaveChanges()` but does not add the items still waiting in the two queues.
- Refresh returns on cancel without calling `WriteChanges()`.

**Explicit batch lifecycle.**

- Flush when the queue reaches N items or T seconds have passed.
- Flush on normal completion and on orderly cancel, in every runner (scan, refresh, check apply).
- `Dispose()` flushes pending items as a last resort and logs if it had to.
- Clear the queues and advance `TotalSuccessCount` / `TotalErrorCount` only after the commit succeeded. On a failed commit the items stay queued and the error is reported.
- Keep a transaction open only while writing, never across hashing.
- After each commit, bound the tracker with `ChangeTracker.Clear()` (or recreate the context; measure which is cheaper).

**Related write paths.**

- `TouchItem` marks the whole entity as modified, although only `LastScanDateUTC` changes. Use targeted, batched updates (`ExecuteUpdate` over a batch of ids, or a property-level modified flag).
- `UpdateItem`, `RemoveItem`, and the `applyCheck` path in [CheckReportApplier.cs](../src/rogerbriggen.myDupFinderLib/Check/CheckReportApplier.cs) (`InsertItem`, `ApplyUpdateById`, `RemoveItemById`) each save once per row and should use the same batching.
- If writing is still expensive after batching, use reused parameterised SQLite commands for the insert path only, inside one transaction, without replacing EF elsewhere. See the [Microsoft bulk insert guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/bulk-insert).

## Step 3 — Index migration and resume

1. **Add the missing `FilenameAndPath` index migration.**
   - The index is declared in [DubFinderContext.cs](../src/rogerbriggen.myDupFinderDB/DubFinderContext.cs) (line 37), but it is in neither the only migration nor the [model snapshot](../src/rogerbriggen.myDupFinderDB/Migrations/DubFinderContextModelSnapshot.cs), which list only `FileSha512Hash`, `FileSize` and `Filename`.
   - Databases created through `Migrate()` therefore do not have it. The resume check `IsAlreadyInDB` and `GetItemByPath` filter by path and currently scan the table for every file.
   - This is the highest-value single fix for resuming a large scan.

2. **Check further indexes with `EXPLAIN QUERY PLAN`.**
   - `PathBase`: used by `IsBasePathAlreadyInDB` and `GetAllItemsByBasePath`.
   - `(OriginComputer, ScanName)`: used by `GetAllItemsByScanIdentity`.
   - Add an index only where the query plan shows a scan on a query that runs often or on a large table. A compiled EF query does not replace a missing index.

3. **Define the scan identity before changing the resume filter.**
   - Today `IsAlreadyInDB` matches on path, size and `ScanExecutionComputer`. An in-memory set of `(path, size)` would not behave the same.
   - Decide first what identifies "the same file in the same scan" (candidates: `PathBase`, `OriginComputer`, `ScanName`, `ScanExecutionComputer`).
   - The current rule also misses changes that keep the file size. Adding the modification time narrows that gap, but it is not an integrity check; that remains the job of the check run.
   - Only then choose between the indexed per-file query and a preloaded set keyed on the full identity.

4. **Small query fixes.** `IsEmptyScanItemTable` and `IsBasePathAlreadyInDB` use `Count()` where `Any()` is enough.

### Result

Implemented as step 3.1 and 3.4: the migration `AddFilenameAndPathIndex` and `Any()` instead of `Count()`. Step 3.3 is left as it is (the resume identity is unchanged, see [DatabaseIndexMigrationTest.cs](../tests/rogerbriggen.myDupFinderLibUnitTest/DatabaseIndexMigrationTest.cs) for the plans below).

**Query plans** (`EXPLAIN QUERY PLAN` of the SQL that EF generates, empty table, after the migration):

- `IsEmptyScanItemTable`: `SCAN s USING COVERING INDEX IX_ScanItems_FileSize`. Stops at the first row, so it is cheap.
- `IsAlreadyInDB` (per file when resuming): `SEARCH s USING INDEX IX_ScanItems_FilenameAndPath (FilenameAndPath=?)`.
- `GetItemByPath`: `SEARCH s USING INDEX IX_ScanItems_FilenameAndPath (FilenameAndPath=?)`.
- `IsBasePathAlreadyInDB`: `SCAN s`. Stops at the first match; a base path that is not in the database yet scans the whole table once.
- `GetAllItemsByBasePath`: `SCAN s`.
- `GetAllItemsByScanIdentity`: `SCAN s`.
- `GetItemsByRelativeSuffix`: `SCAN s` (`LIKE '%suffix'` cannot use an index). Not called by any runner at the moment.
- `GetItemById`: `SEARCH s USING INDEX sqlite_autoindex_ScanItems_1 (Id=?)` (primary key).
- `FindDupsInSameDB`: `SEARCH ScanItems USING INDEX IX_ScanItems_FileSha512Hash (FileSha512Hash=?)` with the subquery `SCAN ScanItems USING COVERING INDEX IX_ScanItems_FileSha512Hash`.
- `GetAllScanItems`: `SCAN s USING INDEX IX_ScanItems_FileSha512Hash` plus `USE TEMP B-TREE FOR LAST TERM OF ORDER BY` (the sort by path).
- `FindDupsInDifferentDBs`: `SCAN s USING COVERING INDEX IX_ScanItems_FileSha512Hash` on the second database to read the distinct hashes, then one `SEARCH s USING INDEX IX_ScanItems_FileSha512Hash` per hash in the base database.

**The planner did not pick the new index at first.** With the original expression of `IsAlreadyInDB` (path, size and computer all compared with `=`) SQLite, which has no table statistics here, chose `IX_ScanItems_FileSize` instead of `IX_ScanItems_FilenameAndPath`. The size index is far less selective (many files share a size). The query now compares `FileSize + 0`, which has the same value but cannot use the size index, so the path index is always the one used. The result is unchanged.

**Decision: no index on `PathBase` or on `(OriginComputer, ScanName)`.** `IsBasePathAlreadyInDB`, `GetAllItemsByBasePath` and `GetAllItemsByScanIdentity` run once per scan, refresh or check run (scan: `IsBasePathAlreadyInDB` once and only if the table is not empty; refresh: `GetAllItemsByBasePath` once; check: `GetAllItemsByBasePath` or, with `IgnoreBasePath`, `GetAllItemsByScanIdentity` once). They return a large part of the table or all of it, so a full scan is the right plan, while every additional index makes every insert slower. `IsAlreadyInDB` is the only query that runs once per file.

**Resume benchmark** (`Rescan_GeneratedTree_ReportsResumeTimings`, 20000 small files, the second run skips every file; "Scan:" line of the second run):

| Case | Line |
| --- | --- |
| After (migration applied, path index used) | `Scan: 20000 files, 0.0 MB hashed in 1.8 s. Average 11379.1 files/s, 0.0 MB/s. Stage time: enumerate=0.8 s, hash=0.0 s, database=0.9 s` |
| Before, as the databases are today (no path index, the planner uses the size index; file sizes 64 B to 64 KB, so only a few files share a size) | `Scan: 20000 files, 0.0 MB hashed in 2.4 s. Average 8467.9 files/s, 0.0 MB/s. Stage time: enumerate=1.0 s, hash=0.0 s, database=1.4 s` |
| Before, no usable index (all files the same size, so the size index does not help) | `Scan: 20000 files, 0.0 MB hashed in 132.0 s. Average 151.6 files/s, 0.0 MB/s. Stage time: enumerate=0.8 s, hash=0.0 s, database=131.1 s` |
| Before, no usable index (the new query with the index dropped, different sizes) | `Scan: 20000 files, 0.0 MB hashed in 125.6 s. Average 159.3 files/s, 0.0 MB/s. Stage time: enumerate=0.8 s, hash=0.0 s, database=124.7 s` |

The gain is small when file sizes are spread out, because the size index happens to narrow the search to a few rows. It is large when many files share a size (empty files, thumbnails, generated files): the cost per file then grows with the table and the resume of a large scan becomes quadratic. The path index makes the cost per file independent of both. (The "after" run was measured on the spread-out tree only.)

## Step 4 — Enumeration and check lookup

1. **Read metadata from the directory listing.**
   - [EnumerateFilesFromDiscToIndex.cs](../src/rogerbriggen.myDupFinderLib/Helper/EnumerateFilesFromDiscToIndex.cs) (line 64) calls `GetCreationTimeUtc`, `GetLastWriteTimeUtc` and `new FileInfo().Length` separately for each file. The listing already returned all three.
   - Switch to `FileSystemEnumerable<T>` (or `DirectoryInfo.EnumerateFiles`) and take size and timestamps from the entry.
   - Set `AttributesToSkip = 0` explicitly; the default would silently drop hidden and system files.
   - Share one enumerator between scan, refresh and check.

2. **Inaccessible directories must be reported, not skipped silently.**
   - Today one access-denied folder aborts the whole enumeration. Continuing past it is desirable for scan.
   - It is dangerous for refresh and check: [RefreshRunner.cs](../src/rogerbriggen.myDupFinderLib/Refresh/RefreshRunner.cs) (line 157) removes every database row whose file was not enumerated, and check reports such rows as `MissingOnDisk`. A skipped folder would look like deleted files.
   - The enumerator therefore returns the list of subtrees it could not read completely. Refresh must not delete, and check must not report missing, any row under such a subtree. The unread subtrees are logged and counted in the summary.

3. **Quadratic lookup in check with `IgnoreBasePath`.**
   - `FindByRelativePath` in [CheckRunner.cs](../src/rogerbriggen.myDupFinderLib/Check/CheckRunner.cs) (line 316) scans all database rows for every file without an exact path match. When a whole tree has moved, that is N × N comparisons.
   - Build a dictionary from relative path to rows once, before the loop. Keep the current tie-breaking (first unclaimed row not under the current base path).

### Result

Implemented as planned, with one shared enumerator for scan, refresh and check (`FileTreeEnumeration` in `Helper/`) and one change to the plan: the metadata of the directory listing is **not** used for decisions by default (see "Metadata" below).

- **Same files as before.** `AttributesToSkip = 0` is set explicitly. This reproduces `Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)`, which uses `EnumerationOptions.CompatibleRecursive` (no attribute skipped, `IgnoreInaccessible = false`, special directories not returned) and, on Windows, enters junctions and directory symbolic links like normal directories (checked: it returns the files below them). Tests compare the new enumeration with `Directory.EnumerateFiles` (same paths, same order, several forms of the base path, hidden and system files and directories, empty and deep directories, junction and symbolic links where they can be created) and the values of the listing with the file APIs (`Kind == Utc`, equal ticks, assorted timestamps).
- **Metadata: safe by default, fast on request.** The enumerator delivers path, size and times from the directory entry, but the directory entry can be stale: after a file is changed through another NTFS hard link, the entry of this link can keep its old size and timestamps, also after writing has finished (observed on Windows 11 in `Refresh_Default_DetectsAChangeMadeThroughAnotherHardLink`: the listing said 300 bytes and the old time, the file itself 900 bytes). Refresh would then skip the hashing and keep an obsolete hash, and check without hash check would report Ok; a later re-check after hashing (step 6) cannot protect a file whose hashing was skipped. By default every runner therefore reads size, creation time and last write time from the file itself with **one** call (`FileMetadata.Read`: one `FileInfo`, which loads the attribute data once on the first property access and serves the other properties from it; the values equal `File.GetCreationTimeUtc`, `File.GetLastWriteTimeUtc` and `new FileInfo(p).Length`, tested), right before the file is processed, and decides and stores with these values (refresh: comparison, update and new rows including the creation time; check: comparison and all report values; scan: resume filter and stored row). That is one call per file instead of the three before step 4. The job setting `TrustDirectoryMetadata` (default `false`; scan and refresh jobs, check uses the value of its scan job) switches to the directory values and no call per file. Trade-off: with `true` a change made through another hard link can be missed by refresh and by check without hash check; recommended only for trees without hard links where the per-file call is expensive (network shares, large HDD archives).
- **A file whose metadata cannot be read** (default mode): refresh and check count an error and log it, go on, and keep the row (the file was enumerated, so it is neither removed nor reported missing). Scan logs the error, counts it in `ErrorCount` of the enumeration as before step 4 and does not scan that file.
- **Unreadable directories.** The enumerator lists one directory at a time from an explicit queue and records every directory whose listing fails (any exception), so the failing directory is known exactly. Scan logs a warning per directory and in the summary. Refresh keeps (does not remove) database rows below such a directory and check does not report them as `MissingOnDisk`; both count them in the summary. The decision is made at a directory boundary, ignoring case and the kind of separator. If the base path itself cannot be listed, nothing is removed or reported.
- **Check with `IgnoreBasePath`.** The rows are indexed once by relative path (`RelativePathIndex`); a randomized test (4000 seeds) shows the same row is picked as by the old walk.
- **Known limitation of the enumerator.** The directory entry values can lag behind the real file for hard linked files and for files that are being written; with the enumeration running ahead of the hashing they are older still when the file is read. The runners only rely on them when `TrustDirectoryMetadata` is set. Step 6 still re-reads size and time after hashing. Even in the default mode, do not scan, refresh or check files that are being written.

Benchmarks (`MYDUPFINDER_BENCHMARK=1`, 20000 files, Release, Windows 11, one machine; `MYDUPFINDER_BENCHMARK_TRUST=1` selects the trusting mode). The two right-hand columns are one session, measured after a damaged line in the scan loop (the resume filter ran for every file) was repaired and a regression test added; this machine is noisy, the same code varies by a second or more in the total:

| Benchmark | Before step 4 | Step 4 as first committed (directory values only) | Now, default (one call per file) | Now, `TrustDirectoryMetadata` |
| --- | --- | --- | --- | --- |
| Scan (`Scan_GeneratedTree_ReportsTimings`) | `8.4 s total, enumerate=0.9 s, hash=5.6 s, database=1.9 s` | `7.0 s, enumerate=0.1 s, hash=5.5 s, database=1.4 s` | `10.0 s, enumerate=0.7 s, hash=7.3 s, database=2.0 s` | `7.6 s, enumerate=0.1 s, hash=5.6 s, database=1.9 s` |
| Resume, second run (`Rescan_GeneratedTree_ReportsResumeTimings`) | `1.8 s, enumerate=0.9 s, database=0.9 s` | `1.1 s, enumerate=0.1 s, database=1.1 s` | `2.0 s, enumerate=0.7 s, database=1.3 s` | `1.5 s, enumerate=0.1 s, database=1.4 s` |
| Check `IgnoreBasePath`, moved tree (`Check_IgnoreBasePath_MovedTree_ReportsTimings`) | `3.07 s total, enumerate=0.1 s` | `0.28 s total, enumerate=0.0 s` | `0.85 s total, enumerate=0.6 s` | `0.28 s total, enumerate=0.0 s` |

Full lines of the two new modes:

```
default:  Scan: 20000 files, 2817.4 MB hashed in 10.0 s. Average 2003.1 files/s, 282.2 MB/s. Stage time: enumerate=0.7 s, hash=7.3 s, database=2.0 s
trust:    Scan: 20000 files, 2817.4 MB hashed in 7.6 s. Average 2648.9 files/s, 373.2 MB/s. Stage time: enumerate=0.1 s, hash=5.6 s, database=1.9 s
default:  Scan: 20000 files, 0.0 MB hashed in 2.0 s. Average 9915.6 files/s, 0.0 MB/s. Stage time: enumerate=0.7 s, hash=0.0 s, database=1.3 s   (resume)
trust:    Scan: 20000 files, 0.0 MB hashed in 1.5 s. Average 13195.1 files/s, 0.0 MB/s. Stage time: enumerate=0.1 s, hash=0.0 s, database=1.4 s   (resume)
default:  Check IgnoreBasePath, 20000 files in a moved tree: 0.85 s total. 20000 files, 0.0 MB hashed in 0.8 s. Average 23792.7 files/s, 0.0 MB/s. Stage time: enumerate=0.6 s, hash=0.0 s, database=0.1 s
trust:    Check IgnoreBasePath, 20000 files in a moved tree: 0.28 s total. 20000 files, 0.0 MB hashed in 0.3 s. Average 73612.5 files/s, 0.0 MB/s. Stage time: enumerate=0.0 s, hash=0.0 s, database=0.1 s
```

The enumerate stage is the stable comparison: the safe default spends 0.6 to 0.7 s per 20000 files on the metadata (one call per file) on a warm local disk, against 0.9 s for the three calls before step 4; with `TrustDirectoryMetadata` it is 0.1 s. The totals, in particular the hash and database times, vary by more than a second between runs on this machine, so total differences of that size are noise (the default scan total of 10.0 s is mostly a slower hash stage, 7.3 s against 5.6 s). On hard disks and network shares, where each call is a round trip, the saving of two calls out of three and the gain of `TrustDirectoryMetadata` should be larger (not measured). The check before-time was small because files and rows come in nearly the same order and the old walk skips claimed rows with a cheap set lookup, but it still grows with the square of the number of files.

## Step 5 — Cross-database and report queries

1. **Cross-database only-dups query** in [DupFinderDB.cs](../src/rogerbriggen.myDupFinderDB/DupFinderDB.cs) (line 105).
   - It loads every distinct hash from the second database and passes the list back as a `Contains` filter. With hundreds of thousands of 128-character hashes this is slow and can hit SQLite parameter limits.
   - Use `ATTACH DATABASE` and one `EXISTS` join on the two hash indexes, or match in memory with a `HashSet`.

2. **Read-only queries without tracking.** `FindDupsInSameDB`, `GetAllScanItems` and `GetAllScanItemsFromOtherDB` load full tracked entities. Add `AsNoTracking()` and project only the columns the report uses.

3. **Fix the grouping rule first, then simplify the sort.**
   - `GetAllScanItems` orders by hash and then path, which forces a full sort in SQLite.
   - That order is not redundant: `GroupId` values are handed out in iteration order, and in cross-database mode the order also decides pairing (last entry wins for a repeated relative path, first base row claims a match). The later report sort does not undo this.
   - Define a deterministic grouping and pairing rule that does not depend on query order, cover it with tests, and only then reduce the SQL ordering.

## Step 6 — Hash I/O

- Open files with `FileOptions.SequentialScan`, read into a pooled buffer, and feed `IncrementalHash`. Replace `BitConverter.ToString(...).Replace("-", "")` with `Convert.ToHexString`. Put this in one shared `FileHasher`; the code is duplicated in four runners.
- Buffer size is a measured parameter. Compare several sizes (for example 64 KB, 256 KB, 1 MB, 4 MB) on HDD, SSD and a network share before fixing a default.
- **Detect changes during hashing.** With enumeration ahead of hashing, metadata gets older. Re-read size and modification time after the file was hashed. If they differ, retry once; if still unstable, record the file as unstable instead of storing a hash that may not match its metadata.

## Step 7 — Scan pipeline

Replace the single-thread loop with three stages connected by bounded channels: one enumerator, N hash workers, one database writer.

- Worker count is a job setting and a measured parameter. One worker is the starting point for HDDs. For SSD, NVMe and network shares, compare several counts; whether the disk or SHA-512 is the limit is not yet known.
- Bounded queues keep memory flat. The existing `ScanRunnerParallel` has an unbounded queue, no resume filter and a stray `Console.ReadKey()`; retire it rather than fix it.
- A single database writer removes lock contention and owns the batch lifecycle from step 2.
- Optionally run scan jobs on different physical disks in parallel. [Program.cs](../src/rogerbriggen.myDupFinder/Program.cs) (line 98) runs jobs strictly one after another. If two such jobs write to the same database file, they must share one writer.
- Cancellation: workers finish the current file, the writer flushes, then the state changes to cancelled.

## Step 8 — SQLite settings, measured separately

- Query the real state first. EF Core already enables WAL for SQLite databases it creates, so `journal_mode` may need no change; older or externally created files may differ. See the [Microsoft.Data.Sqlite notes](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).
- `synchronous=NORMAL` in WAL mode keeps the database consistent after a power failure, but several recent commits that were not yet synced can be lost, not just the last one. See the [SQLite pragma documentation](https://www.sqlite.org/pragma.html#pragma_synchronous). Make this an explicit setting with the safe value as default, and measure the gain after batching, when it will be much smaller.
- WAL is not suitable for a database file on a network file system. See [SQLite WAL](https://www.sqlite.org/wal.html). This concerns where the database is stored; the scanned files can be on a network drive. Detect or document this case and fall back to a rollback journal.
- `cache_size` and `temp_store` are measured the same way.

## Optional later steps

These change the schema or the meaning of the database and are only worth doing if the steps above are not enough.

- **Hash only possible duplicates.** A file with a unique size cannot have a duplicate. Staged approach: size, then a partial hash, then the full SHA-512 for remaining candidates. Files without a full hash can no longer be used for bit-rot checks or compared against another database later, so this would be a separate scan mode with a nullable hash column.
- **Store the hash as a 64-byte BLOB** instead of 128-character text. Smaller rows and index; needs a migration and touches the CSV report code.
- **Sequential primary key.** The random `Guid` key scatters inserts. Check reports reference the Guid, so it would stay as a secondary column.
- **Drop rarely used indexes** (`Filename`, `FileSize`) during an initial bulk scan and create them at the end.

## Correctness safeguards

- **Semantic regression tests.** Databases contain fresh GUIDs and timestamps, so a byte-wise comparison is meaningless. Compare normalised records (path, size, hash, identity fields) and deterministic reports before and after each step.
- **Failure-path tests:**
  - orderly cancel in scan, refresh and check: nothing hashed is lost, nothing is doubled after restart;
  - process kill mid-batch: the database opens cleanly and the scan resumes;
  - a failing batch commit: items stay queued, counters are not advanced;
  - an inaccessible directory: refresh deletes nothing and check reports nothing missing below it.
- Keep SHA-512 as the only hash that decides "duplicate".

## Order

| Step | Content | Risk |
|---|---|---|
| 0 | Baseline and current database state | None |
| 1 | Sleeps and logging | Low |
| 2 | Batching and tracker limits | Medium (data-loss paths must be tested) |
| 3 | Index migration and resume | Low |
| 4 | Enumeration and check dictionary | Medium (delete and missing detection) |
| 5 | Cross-database and report queries | Low to medium (grouping rule) |
| 6 | Hash I/O | Low |
| 7 | Scan pipeline | Medium |
| 8 | SQLite settings | Measured separately, configurable |
| – | Selective hashing, schema changes | High, optional |
