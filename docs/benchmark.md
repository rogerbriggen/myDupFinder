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
  - `enumerate`: in the scan, walking the directories and reading file attributes; in refresh and check, only the directory listing,
  - `hash`: opening, reading, hashing (SHA-512) and closing the files,
  - `database`: everything done with the database, i.e. resume lookups, inserts, updates, removals and commits (check: the initial load of the rows).

  The stages do not add up to elapsed exactly: code that no stage measures (and the throttle) is missing.

The same progress line (every 10 seconds) and summary line are written by the scan, refresh and check jobs to the normal log, see the README.
