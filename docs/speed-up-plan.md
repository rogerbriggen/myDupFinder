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
