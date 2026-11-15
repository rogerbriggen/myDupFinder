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

### Result

Implemented as items 1 and 2 (without the projection), and item 3 in a different way: the sort was **not** reduced, the grouping rule was **documented, not changed**, and the sort was made total. The report writer is still not streaming: all rows of a job are in memory, the report is built from lists and written at the end (out of scope here).

**1. Cross-database only-dups query.** [DupFinderDB.cs](../src/rogerbriggen.myDupFinderDB/DupFinderDB.cs) attaches the second database to the connection of the base database (`ATTACH DATABASE @p0 AS dupfinder_second`, the path is a parameter) and runs one query:

```sql
SELECT s.* FROM ScanItems AS s
WHERE s.FileSha512Hash <> ''
  AND EXISTS (SELECT 1 FROM dupfinder_second.ScanItems AS o WHERE o.FileSha512Hash = s.FileSha512Hash)
ORDER BY s.FileSha512Hash, s.FilenameAndPath, s.Id
```

Plan: `SCAN s USING INDEX IX_ScanItems_FileSha512Hash` / `SEARCH o EXISTS USING COVERING INDEX IX_ScanItems_FileSha512Hash (FileSha512Hash=?)` / `USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY`. The same plan on a second database from the first migration only (no `FilenameAndPath` index).

- **Two SQL forms, chosen at run time (review finding).** The form above (`EXISTS`) always walks the whole hash index of the base database. With a big base and a tiny second database that is much slower than the old query, which only searched the base index for the supplied hashes. The planner does not decide this (no statistics, and the second database must not be analyzed), so the plan of each form is fixed: `EXISTS` = `SCAN s USING INDEX IX_ScanItems_FileSha512Hash` / `SEARCH o EXISTS USING COVERING INDEX IX_ScanItems_FileSha512Hash (FileSha512Hash=?)` / `USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY`; `IN` (`... s.FileSha512Hash IN (SELECT o.FileSha512Hash FROM dupfinder_second.ScanItems AS o WHERE o.FileSha512Hash <> '')`) = `SEARCH s USING INDEX IX_ScanItems_FileSha512Hash (FileSha512Hash=?)` / `LIST SUBQUERY 1` / `SCAN o USING COVERING INDEX IX_ScanItems_FileSha512Hash` / `USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY`. A third form (`CROSS JOIN` with the distinct hashes of the second database) was measured and is never better than `IN` (and needs a full sort). Measured crossover (300000 base rows, 3000 to 150000 second rows): `IN` is faster up to a base about 3 times the size of the second database, equal at about 3 to 4 times, and `EXISTS` is faster beyond. The code reads the size of both tables with `SELECT MAX(rowid)` (one seek to the end of the table: 0.06 ms on 300000 rows, against 2.3 ms for `COUNT(*)`, which scans an index and grows with the table; it is an upper bound of the row count, a table with many deleted rows looks bigger, which can only cost time) and uses `IN` if the second table is less than a third of the base table, `EXISTS` otherwise. Nothing is loaded into client memory or passed as parameters; the result, the order, the null rule and the connection handling are the same for both forms (all equivalence tests run both forms and the chosen one).
- **Asymmetric sizes** (`CrossDatabaseQuery_AsymmetricPairs_ReportTimings`, file databases, seconds, best of two, rows materialized as entities; "before" = old filter-list query):

| Base rows | Second rows | Common share | Matching rows | Before | `EXISTS` | `IN` | Chosen |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 300000 | 10 | 100 % | 10 | 0.003 | 0.055 | 0.001 | `IN`, 0.001 |
| 300000 | 10 | 0 % | 0 | 0.001 | 0.055 | 0.001 | `IN`, 0.001 |
| 300000 | 1000 | 100 % | 1000 | 0.022 | 0.080 | 0.013 | `IN`, 0.013 |
| 300000 | 1000 | 1 % | 10 | 0.012 | 0.071 | 0.006 | `IN`, 0.006 |
| 10 | 300000 | 100 % (10 distinct hashes) | 10 | 0.044 | 0.001 | 0.049 | `EXISTS`, 0.001 |
| 10 | 300000 | 0 % | 0 | FAILED (variables) | 0.001 | 0.166 | `EXISTS`, 0.001 |
| 1000 | 300000 | 100 % (1000 distinct hashes) | 1000 | 0.058 | 0.010 | 0.060 | `EXISTS`, 0.010 |
| 1000 | 300000 | 1 % | 1000 | FAILED (variables) | 0.009 | 0.186 | `EXISTS`, 0.009 |
| 300000 | 300000 | 90 % | 270000 | FAILED (variables) | 2.450 | 2.583 | `EXISTS`, 2.448 |
| 300000 | 300000 | 1 % | 3000 | FAILED (variables) | 0.157 | 0.272 | `EXISTS`, 0.157 |

- **What the old code did.** It loaded all distinct non-empty hashes of the second database into a list and passed the list back as a `Contains` filter. That works up to about 32500 distinct hashes and fails above about 32700 with `SQLite Error 1: 'too many SQL variables'` (limit 32766 variables; measured 32500 ok, 32700 failed), so the job failed with a second database of more than that many distinct hashes (100000 rows per database: 73445 distinct hashes, failed). Below the limit the time grows faster than linear.
- **Result is the same.** Same rows, null exactly when the second database has no non-empty hash (an empty list when it has some but nothing matches), rows without a hash in the base database never match, ordered by hash (and now by path and key, see 3). The old implementation is kept as `ReferenceFindDups` in the test project, with equivalence tests over 12 hand-made database pairs and 5 generated ones.
- **The second database is not modified.** No migration, no pragma, no schema change; it is opened by SQLite through the attach only. Tests compare size, time stamp and SHA-256 of the file, and the file list of the directory, before and after, for a database in WAL mode, in rollback mode and from the first migration only (no migration history row added, no index added), with no other connection open: nothing changed, no `-wal`, `-shm` or journal file was left. SQLite could write to the file only if the attach was the last connection to a WAL database that has uncheckpointed data in its `-wal` file (the close then checkpoints), which is also what the old code did through its own connection. **Not read-only at the SQLite level:** `ATTACH 'file:...?mode=ro'` fails with `unable to open database` when the base connection was opened with a plain path, because the SQLite provider only enables URI file names for a connection whose data source starts with `file:` (demonstrated: the same attach works if the main connection is opened as `file:...`). Changing how every connection is opened was out of scope.
- **Connection handling.** The attach belongs to the connection, so the context opens the connection explicitly, attaches, runs the query, detaches in a `finally` (if the detach fails the connection is removed from the pool so the attachment cannot leak) and closes the connection again if it was closed before. Tests: twice in a row, another query afterwards, the pooled connection of the next context has no attachment, a failing query (no `ScanItems` table, not a database file) detaches and leaves the context usable.
- **Missing second file.** Before: the old code opened the second database with EF Core, SQLite created an empty file (0 bytes, left behind) and the query failed with `SQLite Error 1: 'no such table: ScanItems'`. Now: `FileNotFoundException` with the full path before anything is opened, no file is created. The same check was added to `GetAllScanItemsFromOtherDB` (whole-lot mode), which had the same open path.
- **Second path equal to the base path.** Before and now the same: no error, every base row with a hash is reported (each row is its own match); also with another spelling of the same path.

**2. No change tracking.** `FindDupsInSameDB`, `GetAllScanItems`, `GetAllScanItemsFromOtherDB` and the new query use `AsNoTracking()`. No consumer relies on identity resolution: the runners read properties, and use the rows as members of a `HashSet` and as dictionary values by reference (`consumedSecond`, `movedBase`, `movedSecond`, the relative-path dictionary), but `ScanItemDto` does not override `Equals`, every row of one result list is a different object with or without tracking (the key is unique, so no row is returned twice), the base and the second rows come from two different contexts, and a list is never mixed with the result of another query on the same context. Memory of 100000 rows (`Tracking_MemoryOfTheRows_ReportsComparison`): tracked, context alive 113.5 MB; the list alone after the context is gone 71.9 MB; not tracked 71.2 MB. So tracking costs about 42 MB per 100000 rows as long as the context lives, which in the runners is the whole job. No projection to a smaller type yet.

**3. Grouping and sort.** The SQL sort is kept and made total: hash, path, primary key (`GetAllScanItems`, `GetAllScanItemsFromOtherDB`, `FindDupsInSameDB` and the cross-database query). Plans (empty-table plans as in step 3, populated tables the same): `GetAllScanItems` and `GetAllScanItemsFromOtherDB`: `SCAN s USING INDEX IX_ScanItems_FileSha512Hash` + `USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY` (before: `LAST TERM`); `FindDupsInSameDB`: `SEARCH ScanItems USING INDEX IX_ScanItems_FileSha512Hash (FileSha512Hash=?)` with `LIST SUBQUERY 1` / `SCAN ScanItems USING COVERING INDEX IX_ScanItems_FileSha512Hash`, plus `USE TEMP B-TREE FOR LAST 2 TERMS OF ORDER BY` (before: no sort at all); so the order by hash comes from the index and only the rows of one hash are sorted by path and key, never the whole result. The rules as they are (and were) are written in the XML documentation of `FindDupsInSameDB` and `FindDupsInDifferentDBs` and in the README: GroupIds are handed out in the order hash, path, key; in the whole-lot cross-database mode the last second row of a relative path in that order is the one that can be paired, and the first base row of a relative path claims it. Tests with hand-made databases pin each rule including ties, and the complete report of all four modes is byte-identical between two runs and between three insertion orders (as inserted, shuffled, reversed; the plain-SQL insert keeps the physical order, which EF Core does not). Without the key in the sort these tests fail (checked), so they would catch a regression. There is no case in which the report depends on the insertion order with the total order.

**Benchmark** (`FindDupsBenchmark`, see [benchmark.md](benchmark.md); Release, Windows 11, one machine that varies by tens of percent between runs; peak = managed heap above start):

100000 rows per database (84966 / 73445 distinct hashes, 57330 in common):

| Mode | Query before (s) | Query after (s) | Total before (s) | Total after (s) | Report rows | Peak before (MB) | Peak after (MB) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| same database, only dups | 0.38 | 0.24 | 0.38 | 0.28 | 26028 | 42 | 30 |
| same database, whole lot | 1.15 | 0.77 | 1.16 | 0.96 | 100000 | 157 | 111 |
| two databases, only dups | FAILED (`too many SQL variables`) | 0.60 | FAILED | 0.71 | 70937 | - | 66 |
| two databases, whole lot | 2.04 | 1.64 | 2.63 | 2.02 | 200000 | 305 | 218 |

Sizes where the old query still works (query part / total, seconds; peak MB), before then after:

| Rows per database | two databases, only dups before | two databases, only dups after | report rows |
| --- | --- | --- | --- |
| 20000 (14684 distinct hashes in the second) | 1.17 / 1.12 s, 35 MB | 0.13 / 0.16 s, 16 MB | 14059 |
| 40000 (29351 distinct hashes in the second) | 3.69 / 3.68 s, 72 MB | 0.26 / 0.31 s, 33 MB | 28498 |

The cross-database query alone (`CrossDatabaseQuery_ReferenceVersusAttached_ReportsTimings`; the second database has the given number of distinct hashes and half of the base rows match): 10000 hashes 0.59 s before, 0.08 s after; 30000: 3.79 s, 0.26 s; 100000: failed, 0.99 s; 300000: failed, 2.96 s. The other modes only gain from the lighter rows (no tracking) and are otherwise unchanged.

## Step 6 — Hash I/O

- Open files with `FileOptions.SequentialScan`, read into a pooled buffer, and feed `IncrementalHash`. Replace `BitConverter.ToString(...).Replace("-", "")` with `Convert.ToHexString`. Put this in one shared `FileHasher`; the code is duplicated in four runners.
- Buffer size is a measured parameter. Compare several sizes (for example 64 KB, 256 KB, 1 MB, 4 MB) on HDD, SSD and a network share before fixing a default.
- **Detect changes during hashing.** With enumeration ahead of hashing, metadata gets older. Re-read size and modification time after the file was hashed. If they differ, retry once; if still unstable, record the file as unstable instead of storing a hash that may not match its metadata.

### Result

Implemented as planned, with one shared `FileHasher` (`Helper/FileHasher.cs`) used by `ScanRunnerSingleThread`, `RefreshRunner` and `CheckRunner`. `ScanRunnerParallel` was **not** changed: its hash is inline in a lambda that has no result type for an unstable file and no resume or cancel path; it is retired in step 7.

**What the hasher does.**

- `File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan)`: the same access and sharing as `File.OpenRead` (which is `FileAccess.Read`, `FileShare.Read`) plus the sequential-scan hint, no `FileStream` buffering. Reads with `RandomAccess.Read` into a pooled buffer (`ArrayPool<byte>.Shared`, the buffer size stays at or below its limit of 1 MB), feeds `IncrementalHash` (SHA-512), `Convert.ToHexString`. The token is checked before every read, so a cancel does not wait for the end of a big file.
- Equality with the old expression (`BitConverter.ToString(...).Replace("-", "")`, kept as `ReferenceFileHash` in the test project) is tested for sizes 0, 1, buffer-1, buffer, buffer+1, several buffers, 60 random sizes with random buffer sizes and content, and for buffer sizes from 1 byte to 4 MB. The sharing semantics are tested by holding the file open with every combination of `FileAccess` (Read, Write, ReadWrite) and seven `FileShare` values and comparing with `File.OpenRead`: the same files can be hashed and the same ones fail with the same exception type; also for a missing file, a missing directory and a directory path.
- A read request is never larger than the rest of the file (size from the handle) plus one byte, and 4 KB at or beyond the expected end. Without this a big buffer made every small file slower (ReadFile into a 4 MB buffer for a 2 KB file: 20000 small files 1.30 s with 4 MB against 1.16 s with 64 KB); with it the buffer size has no effect on small files. The result does not depend on it, the loop always reads until a read returns 0.
- The ThroughputTracker hash stage still covers open, read, hash and close (the call of the hasher, retry included).

**Buffer size** (`FileHasherBenchmark`, see [benchmark.md](benchmark.md); Release, Windows 11, NVMe SSD (the drive of the temporary folder); median of 5 runs, order rotated every run; **warm cache**, see below):

| Variant | 1 GB file, MB/s | 20000 small files (101 MB), files/s |
| --- | --- | --- |
| old: `File.OpenRead` + `ComputeHash` | 430 | 20474 |
| `FileHasher` 64 KB | 557 | 17302 |
| `FileHasher` 256 KB | 568 | 17567 |
| `FileHasher` 1 MB | 570 | 17663 |
| `FileHasher` 4 MB | 576 | 17761 |
| `FileStream`, 1 MB blocks, buffered (4096) / unbuffered (0) | 568 / 569 | 21602 / 21374 |
| `FileStream`, 4 MB blocks, buffered / unbuffered | 582 / 573 | 18276 / 18410 |

Default: **1 MB** (`FileHasher.DefaultBufferSize`, an internal constant, no job setting). The numbers do not separate 256 KB, 1 MB and 4 MB (differences below 1.5 %, run-to-run noise on this machine); 64 KB is about 2 % slower. 1 MB is a tie-break, not a measured optimum: it is the largest size the shared array pool serves and it means fewer requests on media where every request is a round trip. The sequential-scan hint, `FileStream` buffered versus unbuffered: no difference when the blocks are 1 MB or larger (the stream's own buffer is bypassed for big reads).
Compared with the old expression the large file is 30 % faster (the 4096-byte reads are gone). On many small files the new hasher is about 12 to 15 % slower than the old expression (1.13 s against 0.98 s for 20000 files, i.e. about 8 microseconds per file): that is the price of the change detection, four handle queries per file (size and last write time before and after reading). A `FileStream` variant without these queries measures 0.93 s, so the queries are the difference. The scan benchmark in the table below shows the effect in a whole run.

**What could not be measured.** Cold cache (the cache of the operating system cannot be emptied from a test without administrator rights; the large file was written just before and 63 GB of RAM keep it, so all numbers are CPU bound by SHA-512 at about 570 MB/s and cannot show the effect of the block size on the disk), hard disk (an HDD is in the machine but the benchmark ran in the temporary folder on the NVMe SSD), network shares, and the effect with several workers (step 7). The decision between 256 KB and 4 MB should be revisited with a cold-cache run on the HDD and a share before step 7.

**Unstable files.**

- Size and last write time are read from the open handle (`RandomAccess.GetLength`, `File.GetLastWriteTimeUtc(SafeFileHandle)`, both exist in .NET 11) right after opening and again after the last read; the bytes actually read are counted. **Stable** = size and time equal before and after and bytes read = size. Otherwise the file is read once more from the start (new open); if that is not stable either, the file is **unstable**. The values of the handle equal the path based ones tick for tick (`HandleValues_EqualThePathValues_TickForTick`: four timestamps including odd ticks, compared with `File.GetLastWriteTimeUtc(path)`, `FileInfo` and `FileMetadata.Read`, `Kind` is Utc).
- Stable result, default mode: the size and last write time **returned by the hasher** are stored with the hash (scan item, refresh update and new row, check report). Creation time stays as it was read before the hash. They can differ from the values read before the hash if the file changed in between; they belong to the hash. Check also compares and classifies with them.
- Stable result, `TrustDirectoryMetadata`: the directory listing values are stored as before step 6, so that the next refresh, which compares the listing with the database, sees the file as unchanged; the hash can then belong to a newer state of the file than the stored size and time (the documented trade-off of the setting). The stability check itself uses the handle values in both modes.
- Unstable: **scan** stores no scan item, stores a scan error item (message: the file changed while it was read), logs a warning, goes on. **Refresh**: an existing row stays exactly as it is (not updated, not touched, not removed), a new file is not added; counted in `ErrorCount` and in the new `UnstableCount`; warning. **Check**: no report row (not even `NewOnDisk` for a new file), the database row is claimed so it is not reported as `MissingOnDisk`; counted in `ErrorCount` and `UnstableCount`; warning. Every summary line has the unstable count (`UnstableFiles=` in the scan line, `Unstable=` in the refresh and check lines).
- A file that is skipped (resume filter, refresh unchanged, check with `SkipHashCheck`) is not opened at all, as before; tests count the opens through a hook: second scan 0, untouched refresh 0, check with `SkipHashCheck` 0, a changed file in refresh 1, a normal check opens every file exactly once.
- **What this does and does not catch.** With `FileShare.Read` (same as before) Windows refuses every writer while the hasher reads, so on Windows a file is practically stable during the read (the tests that change a file during the read use an internal `FileShare.ReadWrite` hasher for that reason, and one test pins that a writer is refused with the default). Where locks are only advisory or absent (network shares, other systems) the check does catch size changes and the time change of writers that update it. It does not catch a change that keeps size and last write time, and it does not cover the time between the metadata read of the runner and the open of the file.

**Cancel inside a file.** The hasher throws `OperationCanceledException` before the next block. Scan: the file gets no item and no error item, the pending items are written, state `cancelled`, the same as a cancel between two files (the existing check after the hash call sees the token). Refresh: the per-file catch handles it before the generic error catch: pending writes flushed, nothing stored or counted for the file, state `cancelled`. Check: same, the report is flushed, no row, no error count. Each runner has a test with a deterministic cancel after the second block of a multi-block file, and a restart that completes the job.

**Benchmarks** (`MYDUPFINDER_BENCHMARK=1`, 20000 files, default mode, one machine, noisy):

| Benchmark | Before | After |
| --- | --- | --- |
| Scan (`Scan_GeneratedTree_ReportsTimings`, 2817 MB) | `9.9 s, 283.3 MB/s, enumerate=0.6 s, hash=7.3 s, database=2.0 s` | `8.5 s, 332.2 MB/s, enumerate=0.7 s, hash=5.9 s, database=1.9 s` |
| First scan of small files (`Rescan_...`, 101 MB) | `4.2 s, enumerate=0.6 s, hash=1.0 s, database=2.5 s` | `4.4 s, enumerate=0.6 s, hash=1.1 s, database=2.5 s` |
| Resume (second run) | `2.1 s, enumerate=0.7 s, hash=0.0 s, database=1.4 s` | `2.1 s, enumerate=0.7 s, hash=0.0 s, database=1.4 s` |
| Check `IgnoreBasePath` | `0.91 s total` | `0.95 s total` |

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
