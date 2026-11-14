# Speed-up plan for scanning and duplicate finding

Most of the time today is lost to deliberate sleeping, one database transaction per file, and redundant file system calls, not to hashing itself. The plan is ordered so the cheap, safe wins come first and the riskier design changes come last. Nothing here is measured yet; the gains are estimates from reading the code, so step 0 is a benchmark.

## Phase 0 — Measure first

- Add a small benchmark harness (a test tree generator plus stopwatch logging per stage: enumerate, hash, DB write) so each later phase can be verified.
- Log throughput (files/s, MB/s) every few seconds in the runners; this also replaces the per-file log lines removed in Phase 1.

## Phase 1 — Quick wins, no design change

1. **Remove the fixed `Thread.Sleep(10)` per file.**
   - It is in [ScanRunnerSingleThread.cs](../src/rogerbriggen.myDupFinderLib/Scan/ScanRunnerSingleThread.cs) (line 118), [RefreshRunner.cs](../src/rogerbriggen.myDupFinderLib/Refresh/RefreshRunner.cs) (line 154) and [CheckRunner.cs](../src/rogerbriggen.myDupFinderLib/Check/CheckRunner.cs) (line 135).
   - On Windows a 10 ms sleep is usually about 15 ms, so 1 million files cost roughly 3–4 hours of pure waiting.
   - Refresh and check pay it even for unchanged files that are never hashed.
   - Make throttling a job setting (off by default), and let `DiscThrottle` be the only throttle.

2. **Batch the database writes.**
   - `Enqueue` in [ScanJobDBInserts.cs](../src/rogerbriggen.myDupFinderDB/ScanJobDBInserts.cs) (line 47) calls `WriteChangesInternal` every time, so each file is its own `SaveChanges` and its own SQLite transaction with a disk sync. `_commitCount = 100` is never used for batching.
   - Because `written` is always 1, the `written % _commitCount == 0` check never fires, so the context is never recreated and the change tracker grows for the whole multi-day run.
   - Fix: flush only when the queue reaches N items (for example 500) or T seconds have passed, in one transaction, then recreate the context.
   - Resume still works: a crash loses at most one batch, which is re-hashed on the next run. Cancel already calls `WriteChanges`.
   - Apply the same batching to `TouchItem`, `UpdateItem` and `RemoveItem`, which refresh calls once per file. For unchanged files, one bulk `ExecuteUpdate` of `LastScanDateUTC` per batch of ids is enough.

3. **Set SQLite pragmas when opening the connection** in [DubFinderContextFactory.cs](../src/rogerbriggen.myDupFinderDB/DubFinderContextFactory.cs) (line 22).
   - `journal_mode=WAL`, `synchronous=NORMAL`, a larger `cache_size`, `temp_store=MEMORY`.
   - WAL with `NORMAL` stays consistent after a crash; only the last transaction can be lost on power failure, which the resume logic already tolerates.

4. **Stop asking the file system three times per file.**
   - [EnumerateFilesFromDiscToIndex.cs](../src/rogerbriggen.myDupFinderLib/Helper/EnumerateFilesFromDiscToIndex.cs) (line 64) calls `GetCreationTimeUtc`, `GetLastWriteTimeUtc` and `new FileInfo().Length` separately. The directory listing already returned all three.
   - Switch to `FileSystemEnumerable<T>` (or `DirectoryInfo.EnumerateFiles`) and read size and timestamps from the enumeration entry. This matters most on HDDs and network shares.
   - Use `EnumerationOptions` with `IgnoreInaccessible = true` and `AttributesToSkip = 0`. Today one access-denied folder aborts the whole enumeration; the default `AttributesToSkip` would silently drop hidden and system files, so it must be set explicitly.
   - Share this one enumerator between scan, refresh and check, which currently each have their own copy.

5. **Read files for hashing with a large buffer.**
   - `File.OpenRead` plus `ComputeHash(stream)` reads in small (about 4 KB) chunks.
   - Open with `FileOptions.SequentialScan`, no `FileStream` buffering, and feed a pooled 1–4 MB buffer into `IncrementalHash`.
   - Replace `BitConverter.ToString(...).Replace("-", "")` with `Convert.ToHexString`.
   - Put this in one shared `FileHasher` helper; the same code is duplicated in four runners.

6. **Cut per-file logging.**
   - `LogInformation("File {file} successfull finished")` goes to the synchronous console sink for every file. Move it to Debug and rely on the periodic progress line.

7. **Cheaper resume filter.**
   - `IsAlreadyInDB` runs one SQL query per file under a lock. Load the `(FilenameAndPath, FileSize)` pairs for the base path once into a `HashSet` instead. If memory is a concern at many millions of files, use a compiled query instead.
   - `IsEmptyScanItemTable` and `IsBasePathAlreadyInDB` use `Count()`; `PathBase` has no index, so that is a full table scan. Use `Any()`.

## Phase 2 — Scan pipeline

Replace the single-thread loop with three stages connected by bounded channels: one enumerator, N hash workers, one DB writer.

- **Configurable worker count per scan job** in `MyDupFinderScanJobDTO`: 1 for HDDs (parallel reads cause seeking and are slower), 4–8 for SSD/NVMe, where SHA-512 on one core is the limit.
- **Bounded queues** so memory stays flat. The existing `ScanRunnerParallel` has an unbounded queue, no resume filter and a stray `Console.ReadKey()`; retire it rather than fix it.
- **Single DB writer** removes the lock contention in `ScanJobDBInserts`.
- **Run scan jobs on different physical disks in parallel.** [Program.cs](../src/rogerbriggen.myDupFinder/Program.cs) (line 98) runs jobs strictly one after another. Add an optional "disk group" per job so jobs on separate disks overlap. For a multi-disk project this is the largest single gain.
- Cancellation: workers finish the current file, the writer flushes, then the state changes to cancelled.

## Phase 3 — Finding duplicates

1. **Cross-database only-dups query** in [DupFinderDB.cs](../src/rogerbriggen.myDupFinderDB/DupFinderDB.cs) (line 105).
   - It loads every distinct hash from the second DB into memory and passes the list back as a `Contains` filter. With hundreds of thousands of 128-character hashes this is slow and can hit SQLite parameter limits.
   - Use `ATTACH DATABASE` and a single `EXISTS` join on the two existing hash indexes, or do the match in memory with a `HashSet`.

2. **Read-only queries without tracking.**
   - `FindDupsInSameDB`, `GetAllScanItems` and `GetAllScanItemsFromOtherDB` load full tracked entities. Add `AsNoTracking()` and project only the columns the report uses (path, base path, size, hash).

3. **Drop the SQL sort that is redone anyway.**
   - `GetAllScanItems` orders by hash and then path, which forces a full sort in SQLite; `DupReportWriter` re-sorts everything afterwards.
   - Order by hash only (served by the index). Group ids depend on iteration order, so check the existing tests that pin them.

4. **Quadratic lookup in check with `IgnoreBasePath`.**
   - `FindByRelativePath` in [CheckRunner.cs](../src/rogerbriggen.myDupFinderLib/Check/CheckRunner.cs) (line 316) scans all DB rows for every file that has no exact path match. When a whole tree has moved, that is N × N comparisons.
   - Build a dictionary from relative path to rows once, before the loop.

5. **Streaming report.** `DupReportWriter` is fine for now; only revisit if memory becomes a problem at many millions of rows.

## Phase 4 — Optional, larger changes

These change the schema or the meaning of the database, so they should only be done if Phases 1–3 are not enough.

- **Hash only possible duplicates.** A file with a unique size cannot have a duplicate. Staged approach: size, then a partial hash of the first and last 64 KB, then the full SHA-512 only for remaining candidates. This can skip most of the reading, but files without a full hash can no longer be used for bit-rot checks or compared against another database later. It would have to be a separate scan mode with a nullable hash column.
- **Store the hash as a 64-byte BLOB instead of 128-character text.** This roughly halves the hash index and the row size. It needs a migration and touches the CSV report code.
- **Sequential primary key.** The random `Guid` key scatters inserts across the index. Check reports reference the Guid, so it would have to stay as a secondary column.
- **Drop rarely used indexes** (`Filename`, `FileSize`) during the initial bulk scan and create them at the end.

## Correctness safeguards for every phase

- A regression test that scans the `testdata` trees before and after each change and compares the DB contents and reports byte for byte.
- A resume test: cancel mid-scan, restart, and confirm no file is missing or doubled.
- Keep SHA-512 as the only hash that decides "duplicate".

## Suggested order

| Step | Content | Expected effect | Risk |
|---|---|---|---|
| 1 | Phase 0 + Phase 1 items 1–3 | Largest gain for many small files | Low |
| 2 | Phase 1 items 4–7 | Faster enumeration, resume and large-file reads | Low |
| 3 | Phase 3 items 1–4 | Find-dups and check scale to millions of rows | Low |
| 4 | Phase 2 | Uses multiple cores and disks | Medium |
| 5 | Phase 4 | Only if still needed | High |
