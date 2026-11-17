# myDupFinder

Find duplicates of files (or checks if the backup still is ok...)

## Project state: in develompent... unusable right now

Build with latest dotnet relased:   ![.NET 11](https://github.com/rogerbriggen/myDupFinder/workflows/.NET%2011/badge.svg)

## Development container

Start Docker Desktop with Linux containers, open this repository in VS Code with
the Dev Containers extension, then choose **Dev Containers: Reopen in Container**.
The setup follows the [Rust UI devcontainer](https://github.com/rogerbriggen/myDupFinderUiRusty/tree/main/.devcontainer),
adapted for .NET 11 on Ubuntu 24.04.

It includes the .NET 11 SDK, C# Dev Kit, Node.js LTS, GitHub CLI, Claude Code,
Codex CLI, the [Codex plugin for Claude Code](https://github.com/openai/codex-plugin-cc),
Python 3 (with pip and venv), ripgrep, jq, and SQLite CLI. Creation restores NuGet
dependencies and the repository's local .NET tools. If setup is interrupted,
rerun `bash .devcontainer/post-create.sh` inside the container.

Build and test from the container terminal:

```bash
dotnet build --configuration Release
dotnet test --configuration Release --verbosity normal --blame-hang-timeout 5m
dotnet run --project src/rogerbriggen.myDupFinder -- exampleproject projectfile.xml
```

Every test run has a hard limit: the test project's `.runsettings` sets a session timeout of 10 minutes (picked up by a plain `dotnet test`), and `--blame-hang-timeout 5m` reports which test hangs. Run tests in the foreground, not in the background.

The Linux container runs the console app and tests. It also enables
`EnableWindowsTargeting` so the Windows Forms project can be restored and built;
running that UI requires Windows. Use Linux paths in project XML files inside the
container; host drives must be mounted explicitly to scan files outside the workspace.

Named volumes survive container rebuilds:

| Volume | Contents |
| --- | --- |
| `claude-code-config` | Claude Code configuration, plugins, login, and history |
| `codex-config` | Codex configuration, login, and history; also used by Claude's Codex plugin |
| `gh-cli-config` | GitHub CLI configuration and login |
| `nuget-packages` | Shared NuGet package cache |
| `mydupfinder-artifacts-${devcontainerId}` | Container-specific build output and intermediate files |

The three login volumes use the same names as the Rust UI devcontainer, so they
share logins when both containers use the same Docker engine. These volumes do
not automatically import logins from Windows. Build artifacts live in
`/home/vscode/.cache/mydupfinder/artifacts`, separate from host `bin`/`obj` folders,
to avoid mixing Windows and Linux restore results and to speed up builds on
bind-mounted Windows workspaces.

On first use, run `claude`, `gh auth login`, and
`codex login --device-auth` as needed. Fresh Codex configurations use
`cli_auth_credentials_store = "file"` to keep the login in the shared volume,
as described in the [Codex authentication documentation](https://learn.chatgpt.com/docs/auth#credential-storage).
Existing shared Codex settings are preserved; if they explicitly select `keyring`
or `ephemeral`, switch that setting to `file` to persist and share the login.
Inside Claude Code, use `/codex:setup` to check the integration and `/codex:review`
to review changes. Reload plugins or restart Claude if it was open during setup.

## Command Line Usage

```bash
# Generate an example project file
myDupFinder exampleproject projectfile.xml

# Validate a project file without running (dry run)
myDupFinder dryrun projectfile.xml

# Run the duplicate finder with a project file (executes all scan, refresh, find-dups, and check jobs, in this order)
myDupFinder run projectfile.xml

# Apply a check report back to the database referenced in its header
myDupFinder applyCheck check-MyJob-20260613-100000.csv
```

### Cancelling and exit codes

A running job is cancelled by pressing **Enter** in the console (the job stops at the next file and keeps what it has written; a scan can be resumed). If the input is redirected (a pipe, a service, a scheduled task) there is no keyboard to read: the program logs once that cancelling with Enter is not available and the job runs to its end.

**Exit codes** of `myDupFinder`:

| Code | Meaning |
| --- | --- |
| `0` | Everything finished: all jobs of the project finished, or the other command succeeded. |
| `1` | A job failed, or the arguments, the project file or its sanity check were wrong, or `applyCheck` failed, or an unexpected exception reached the program. |
| `2` | A job was cancelled by the user. |

Every job ends as exactly one of *finished*, *cancelled* or *failed* (an error that stops the job: the database cannot be opened or written, the base path cannot be listed, the report cannot be created, and so on; the error is in the log). **After a job that fails or is cancelled the remaining jobs of the project are not run**, because later jobs work on the result of the earlier ones (a find-duplicates run after a failed scan would produce a report that looks complete and is not). The log says which job stopped the project and how many jobs were not run.

## Project File (projectfile.xml)

The application is configured through an XML project file. You can generate an example with the `exampleproject` command. The project file contains scan jobs, check jobs, and find-duplicates jobs.

### Structure

```xml
<?xml version="1.0" encoding="utf-8"?>
<MyDupFinderProjectDTO>

  <!-- Scan jobs: scan a folder and store file metadata + SHA-512 hashes in a SQLite database -->
  <MyDupFinderScanJobDTOs>
    <MyDupFinderScanJobDTO>
      <JobName>Example scanjob name</JobName>
      <BasePath>m:\Test</BasePath>
      <OriginComputer>E6600</OriginComputer>
      <ScanName>Backup of old computer</ScanName>
      <DatabaseFile>m:\finddupdb\base.db</DatabaseFile>
      <ReportPath>m:\finddupdb\</ReportPath>
      <ThrottlePercent>0</ThrottlePercent>
      <TrustDirectoryMetadata>false</TrustDirectoryMetadata>
      <HashWorkers>1</HashWorkers>
      <DatabaseSynchronous>Full</DatabaseSynchronous>
    </MyDupFinderScanJobDTO>
  </MyDupFinderScanJobDTOs>

  <!-- Check jobs: verify a previously scanned database against the original files -->
  <MyDupFinderCheckJobDTOs>
    <MyDupFinderCheckJobDTO>
      <ScanJobDTO>
        <JobName>Example checkjob name</JobName>
        <BasePath>m:\Test</BasePath>
        <OriginComputer>E6600</OriginComputer>
        <ScanName>Backup of old computer</ScanName>
        <DatabaseFile>m:\finddupdb\base.db</DatabaseFile>
        <ReportPath>m:\finddupdb\</ReportPath>
        <ThrottlePercent>0</ThrottlePercent>
        <TrustDirectoryMetadata>false</TrustDirectoryMetadata>
        <DatabaseSynchronous>Full</DatabaseSynchronous>
      </ScanJobDTO>
      <IgnoreBasePath>false</IgnoreBasePath>
      <SkipHashCheck>true</SkipHashCheck>
    </MyDupFinderCheckJobDTO>
  </MyDupFinderCheckJobDTOs>

  <!-- Find duplicates jobs: find duplicate files by comparing SHA-512 hashes -->
  <MyDupFinderFindDupsJobDTOs>
    <MyDupFinderFindDupsJobDTO>
      <JobName>Example FindDupsJob name</JobName>
      <DatabaseFileBase>m:\finddupdb\base.db</DatabaseFileBase>
      <DatabaseFile>m:\finddupdb\newdb.db</DatabaseFile>
      <FindDupsMode>FindOnlyDups</FindDupsMode>
      <ReportPath>m:\finddupdb\</ReportPath>
    </MyDupFinderFindDupsJobDTO>
  </MyDupFinderFindDupsJobDTOs>

  <!-- Refresh jobs: refresh an existing database by adding new files, updating changed files, and removing deleted files -->
  <MyDupFinderRefreshJobDTOs>
    <MyDupFinderRefreshJobDTO>
      <JobName>Example RefreshJob name</JobName>
      <BasePath>m:\Test</BasePath>
      <OriginComputer>E6600</OriginComputer>
      <ScanName>Backup of old computer</ScanName>
      <DatabaseFile>m:\finddupdb\base.db</DatabaseFile>
      <ReportPath>m:\finddupdb\</ReportPath>
      <ThrottlePercent>0</ThrottlePercent>
      <TrustDirectoryMetadata>false</TrustDirectoryMetadata>
    </MyDupFinderRefreshJobDTO>
  </MyDupFinderRefreshJobDTOs>

</MyDupFinderProjectDTO>
```

### Order of the jobs

`run` executes the jobs of a project file in a fixed order, not in the order of the XML elements: **scan jobs, then refresh jobs, then find-duplicates jobs, then check jobs**. Within one type the jobs run in the order of the project file. The jobs that change databases (scan, refresh) run first, the jobs that only read them (find-duplicates, check) after them, so a find-duplicates report of a project always shows the database after the scan and refresh jobs of the same project. A check job runs last, after the refresh jobs: it compares the files with the database as it is after the refresh, so a project that wants to *see* the changes since the last scan in a check report must not refresh the same database in the same run (use two project files). All jobs are validated before the first one starts.

### Database files and report files

- **`DatabaseFile` / `DatabaseFileBase`** may be an absolute path, a relative path or a bare file name such as `scan.db`. A relative path is resolved against the current directory of the process, the missing directories are created, and the database is opened at exactly that full path. Characters like `;`, `=`, `'` or spaces in the path are fine.
- **A find-duplicates report is never overwritten.** The report file is `<ReportPath><JobName> dupReport.csv` (`ReportPath` with or without a trailing separator gives the same file). If it already exists, the project is rejected before it runs; if it appears while the job runs, the job writes nothing, leaves the existing file as it is (it is not renamed either), ends as failed and logs an error. Remove or rename the old report to run the job again.
- **A find-duplicates run that fails or is cancelled after it created the report** keeps the partial file but renames it to `<report file name>.failed` (for example `MyJob dupReport.csv.failed`; an older `.failed` file of the same name is replaced). So the partial report does not block the next run, and it is never mistaken for a complete one. A run that is cancelled before the report was created leaves no file. A cancel is looked at between the phases of the job (opening the database, the queries, building the rows, writing), every few thousand rows while the report is written, and a query that is running is interrupted.

### Database settings: `DatabaseSynchronous`, journal mode and where the database file lives

- **`DatabaseSynchronous`** (scan and refresh jobs, and the scan part of check jobs; applyCheck always uses `Full`, find-duplicates only reads): `Full` (default, also when the element is missing) or `Normal`. It is SQLite's `PRAGMA synchronous`: how hard SQLite works to get a committed batch onto the disk.
  - **`Full`**: every commit is synced to the disk before it counts as done. A committed batch survives a power failure and a crash of the operating system.
  - **`Normal`**: fewer syncs, faster commits. It is **only applied when the database is in WAL mode** (and not on a network location, see below). In WAL mode the database stays **consistent** after a power failure or a crash of the operating system, but the **most recent committed batches (not only the last one) can be lost**. Their files are scanned again on the next run because of the resume logic, so nothing is permanently missing. In the rollback-journal mode `Normal` adds a small risk of a **corrupt database** after a power failure on some file systems, so there it is **ignored**: the job logs a Warning (`DatabaseSynchronous=Normal was ignored ...`) and uses `Full`. A crash of the application alone (without a power failure or a crash of the operating system) loses nothing with either value.
  - Because the scan writes in batches of 500 rows, `Normal` gains little (see the measurements in [docs/speed-up-plan.md](docs/speed-up-plan.md), step 8); the safe default is recommended. The "Database state" log line at the start of a job shows the value that is really used (`synchronous=2 (FULL)` or `synchronous=1 (NORMAL)`).
- **Journal mode.** A database that this program creates on a local disk is in **WAL mode** (EF Core switches a new SQLite database to WAL). The program **never changes the journal mode of an existing database**: a database in the rollback-journal mode (for example one created by another tool) stays as it is; the job logs at Information level that it is not in WAL mode and that `DatabaseSynchronous=Normal` has no effect for it.
- **Network locations.** SQLite's WAL mode must not be used for a database **file** on a network file system ([SQLite: WAL](https://www.sqlite.org/wal.html), all users must be on the same host). This is about where the database file is stored, not about the scanned files, which may be on a network drive. The program treats a UNC path (`\\server\share\...`) and a drive that Windows reports as a network drive as a network location. Not detected: a `subst` drive, a local folder that is a junction or link to a share, a share mounted into a folder, and file systems that the platform reports as local.
  - A **new** database on a network location is created in the rollback-journal mode (`DELETE`), before any table or row is written.
  - An **existing** database in WAL mode on a network location is **not converted**: the job logs a Warning that names the risk and the command to convert it, and continues. To convert it yourself, when no job (and no other program) uses the database: `sqlite3 "<database file>" "PRAGMA journal_mode=DELETE;"`. Then `DatabaseSynchronous=Normal` has no effect for it any more.
- **The files of a WAL database.** A database in WAL mode has two companion files next to the database file, `<name>-wal` and `<name>-shm`. They exist while a connection to the database is open (a running job, or a program that has the database open; the program keeps closed connections in a pool until the process ends, so for a running process the files are there from the first write on, and a clean end of the process removes them again: checked with a finished scan, which left only the `.db` file), and after an unclean end (the process was killed, or the machine lost power), when they stay behind and are used to recover the last committed batches the next time the database is opened. After a clean end of all connections SQLite writes the content of `-wal` back into the database file and removes both. So: **copy or move a database only when no job is running** and, if `-wal` or `-shm` files exist, copy all three files together (or open the database once with a job or with `sqlite3` so they are merged first). Copying only the `.db` file while a job runs, or while a `-wal` file is left over, can lose the most recent batches or give an inconsistent copy.

### Throttling, progress and logging

- **`ThrottlePercent`** (scan, refresh and the scan part of check jobs): `0` to `70`, default `0` = no throttling. The element may be left out. With a value above 0 the job sleeps for that share of its working time, but only after it has worked for 5 minutes, to spare an old hard disk. Values above 70 are rejected.
- **`HashWorkers`** (scan jobs only; refresh and check do not use it): `1` to `64`, default `1`. The element may be left out. With `1` the scan reads and hashes one file after the other, with no extra threads. That is the right setting for a **single hard disk**, which gets slower when several files are read at the same time. With more than `1` the scan runs as a pipeline: one thread lists the files (and reads their metadata and does the resume check), `HashWorkers` threads hash files at the same time, and one thread writes the results to the database. The queues between the stages are bounded (a few files per worker), so the memory use does not depend on the size of the tree. Raise it for **SSD or NVMe storage** (where SHA-512 on one core is the limit) and possibly for **network shares** (where the latency of single reads is the limit). On the development machine (NVMe, warm cache, 20 logical processors) 4 workers were about 2 to 3 times and 16 workers about 3 to 4 times as fast as 1 for a mix of file sizes, and many tiny files gained about 25 % (hard disk and network share were not measured). Whether it helps, and what value is best, depends on the storage and **must be measured on your hardware** (see [docs/benchmark.md](docs/benchmark.md), `MYDUPFINDER_BENCHMARK_WORKERS`): the gain ends when the disk or the database writer is the limit, and many tiny files hardly gain at all. The result is the same for every value (the same rows, errors, unreadable-directory warnings and summary line; only the order in which the rows reach the database differs). Cancelling, resuming and the fatal database error work as in the sequential scan. In the progress line and the summary the `hash` stage time is the **sum over all workers**, so with several workers it can be larger than the elapsed time. With `ThrottlePercent` the pause happens in the thread that lists the files, so it slows the feeding of the workers. Values below 1 and above 64 are rejected.
- **Progress:** every 10 seconds the scan, refresh and check jobs write one line with the number of files, the hashed megabytes and the files/s and MB/s of the last interval, and a summary with the time per stage (enumerate, hash, database) when they finish or are cancelled. The `enumerate` time means the same in all three jobs: the directory listing plus the size and timestamps of the files (one call per file, see `TrustDirectoryMetadata`).
- **Directories that cannot be read** (access denied, I/O or network error, directory vanished during the walk): scan, refresh and check log a warning for each of them, go on with the rest and name them again in the final summary. The files below such a directory are not scanned or checked. A refresh does **not** remove database rows below such a directory, and a check does **not** report them as `MissingOnDisk` (the files may still be there); both count these rows in the summary line (`KeptBecauseDirectoryUnreadable`, `NotReportedMissingBecauseUnreadable`). If the base path itself cannot be listed, refresh and check log the error and change or report nothing.
- **`TrustDirectoryMetadata`** (scan, refresh and the scan part of check jobs): `false` (default, also when the element is missing) or `true`. By default every job reads size, creation time and last write time from the file itself, with one call per file, right before it processes the file, and decides and stores with these values. That is always correct. With `true` the jobs use the values of the directory listing and make no call per file, which is faster on network shares and large hard disk archives (enumerate time of the scan benchmark: about 0.1 s instead of 0.6 s for 20000 files on a local disk, see [docs/benchmark.md](docs/benchmark.md)). **The trade-off:** on NTFS the directory entry of a hard link can keep its old size and timestamps when the file is changed through another link, also after writing has finished (observed on Windows 11). With `true`, refresh then counts such a file as unchanged and keeps the old hash, and check without hash check (`SkipHashCheck`) reports it as Ok. Only set `true` for trees without hard links where the per-file call is expensive. Even with the default, do not scan, refresh or check files that are being written.
- **Hashing:** scan, refresh and check hash a file with one shared routine: the file is opened once for reading (shared for reading only, like before), read in blocks of 1 MB, and the job can be cancelled in the middle of a big file (the file is then not stored, not counted as an error, and the job ends as cancelled).
- **Database state:** after opening the database the jobs log `journal_mode`, `synchronous` and the indexes of the `ScanItems` table once; `synchronous` is the value the job really uses (see `DatabaseSynchronous`). The line itself changes nothing.
- **Per-file messages** (a file was hashed, added, updated, removed, is new/modified/missing in a check) are written at log level `Trace`, which the default configuration does not write. Errors and warnings per file are always written. To see the per-file messages, configure the minimum level `Verbose` (Serilog) / `Trace`.
- To measure how fast a scan is, see [docs/benchmark.md](docs/benchmark.md).

### Find Duplicates Modes

The `MyDupFinderFindDupsJobDTO` supports two modes of operation:

- **Same database**: Leave `DatabaseFile` empty to find duplicates within `DatabaseFileBase` only.
- **Cross-database**: Set `DatabaseFile` to a second database path to compare two scans (e.g. an original and a backup). Files are aligned across the two DBs by their path *relative to* `PathBase`, so the absolute roots can differ (`C:\backup` vs `D:\original`).

The find-duplicates job writes a CSV report to `<ReportPath><JobName> dupReport.csv`. It does not modify either database (the base DB may be schema-migrated on open if it is older).

### FindDupsMode Values

- `FindOnlyDups` — reports only duplicate files.
- `FindDupsTheWholeLot` — reports every file with a category, so you can drive a UI that shows the full picture and act on individual rows.

### Find Duplicates Report Format

The report is a `;`-separated CSV with a header row:

```
FilenameAndPath;FileSize;FileSha512Hash;Category;Source;GroupId
```

| Column | Meaning |
|---|---|
| `FilenameAndPath` | Full path of the file, quoted; embedded `"` is doubled (RFC 4180 style). |
| `FileSize` | File size in bytes, as recorded in the database. |
| `FileSha512Hash` | SHA-512 hash, as recorded in the database. Empty if the row has no hash. |
| `Category` | One of `Duplicate`, `Moved`, `Changed`, `Missing`, `New`, `Unique` (see below). |
| `Source` | `Base` (row's file is in `DatabaseFileBase`) or `Second` (row's file is in `DatabaseFile`). Same-DB jobs always emit `Base`. |
| `GroupId` | Integer that ties related rows together. Two rows with the same `GroupId` describe the same logical file across DBs (or different copies of the same content). Singletons get their own `GroupId`. |

#### Categories

| Category | When emitted | Sides emitted |
|---|---|---|
| `Duplicate` | Cross-DB: same relative path + same hash. Same-DB: a hash that appears more than once. | Cross-DB: both `Base` and `Second` rows (paired by `GroupId`). Same-DB: every copy. |
| `Changed` | Cross-DB: same relative path, different hash. | Both `Base` and `Second` rows. |
| `Moved` | Cross-DB: hash matches on the other side at a *different* relative path. | All `Base` and `Second` rows with that hash, sharing one `GroupId`. |
| `Missing` | Cross-DB only: file exists in base DB, no path or hash match in second DB. | `Base` only. |
| `New` | Cross-DB only: file exists in second DB, no path or hash match in base DB. | `Second` only. |
| `Unique` | Same-DB whole-lot only: a hash that appears exactly once in the database. | The single row. |

Notes:

- `FindOnlyDups` emits only `Duplicate` rows. In the cross-DB case only the `Base` side is included (hash-only intersection — there is no path/changed/moved analysis in this mode).
- The report is sorted so that paired rows appear together (by `Category`, then `GroupId`, then `Source`, then path).
- In a cross-DB job the second database is only read (it is attached to the connection of the base database, not migrated and not changed) and must exist: a missing file is an error and is not created.

#### How `GroupId`s are assigned, and which row is paired

The database returns the rows of each side in one fixed order: by hash, then by path, then by primary key (binary text comparison). The `GroupId`s are handed out in that order, starting with 1, so the report does not depend on the order in which the rows were inserted into the database. The report sort above does not renumber them. Two rows can have the same hash and the same path (they are different rows with different keys); they come in the order of their keys.

A row without a hash (empty `FileSha512Hash`) counts as a hash of its own in same-DB jobs (several of them are duplicates of each other). In cross-DB jobs it never matches by hash, but it can still be paired by relative path with a row without a hash on the other side (`Duplicate`).

| Job | GroupIds |
|---|---|
| Same DB, `FindOnlyDups` | One per hash that occurs more than once, in hash order: the smallest such hash gets 1, the next 2, and so on. |
| Same DB, `FindDupsTheWholeLot` | One per distinct hash, in hash order, `Duplicate` and `Unique` alike: the `GroupId` of a hash is its rank among all hashes of the database, so the numbers of the duplicate groups have gaps where a unique file is. |
| Cross-DB, `FindOnlyDups` | One per hash, in hash order, for the base rows whose hash exists in the second database (the smallest such hash gets 1). Only the base side is reported. |
| Cross-DB, `FindDupsTheWholeLot` | In four phases, see below. |

Cross-DB `FindDupsTheWholeLot`:

1. **By relative path** (the path without `PathBase`, compared ignoring case). The rows of the second database are indexed by relative path. **If several second rows have the same relative path, the last one in the order above wins** (largest hash, then path, then key); the others cannot be paired by path. The base rows are then visited in the order above, and **the first base row of a relative path takes the second row** (so with several base rows of one relative path it is the one with the smallest hash, then path, then key); later base rows of the same relative path find it taken. Every pair gets the next `GroupId`: `Duplicate` if the hashes are equal, `Changed` otherwise.
2. **By hash** (the rows left over, with a non-empty hash): every hash that occurs on both sides, in the order of its first base row, gets the next `GroupId` for all its base and all its second rows: `Moved`.
3. **Missing**: the base rows still left over, one `GroupId` each, in the order above.
4. **New**: the second rows still left over, one `GroupId` each, in the order above.

So a `GroupId` is unique within a report, but its number is only a label: it says which rows belong together, not how important they are, and the numbers of one category are not contiguous.
- For a "file-explorer-like" UI, treat each row as one physical file on disk. Use `Source` to know which side it lives on, `GroupId` to find its partners, and `FilenameAndPath` to actually act on it (e.g. delete).

### Refresh Jobs

Refresh jobs update an existing database to reflect the current state of the files on disk. This is useful when files have been added, modified, or deleted since the last scan.

The refresh is smart and fast:

- **New files** on disk are added to the database with their SHA-512 hash.
- **Modified files** (where the file size or last modification date has changed) are updated with a recalculated hash.- **Unchanged files** (same size and date) skip hash recalculation — only the last scan date is updated.
- **Deleted files** (in the database but no longer on disk) are removed from the database.
- **What "the same file" means.** Two rows describe the same file only if `FilenameAndPath`, `OriginComputer` AND `ScanName` are all equal (compared ordinal and exactly, case matters; SQLite's default BINARY collation for TEXT gives the same result as the C# ordinal comparison, the only difference would be strings with embedded NUL characters, which paths cannot contain). A database can hold the scans of several computers or several named scans whose paths are equal (the same drive letter on two PCs). Refresh therefore only works with the rows of the job's own `OriginComputer` and `ScanName`; rows under the same base path with another identity are not updated, touched, removed or counted (the number of them is logged at the start). **Guard:** if rows exist under the base path but none has the job's identity (a mis-configured job: refreshing would add every file again as a second set of rows), refresh logs an error that names the job's `OriginComputer`/`ScanName` and the identities that exist, changes nothing and ends like a refresh whose base path cannot be listed. A base path without any rows is not this case. The same confinement holds for the resumed scan and for check, see below.
- **One row per path.** Older versions could leave several rows for one path. Refresh keeps all of them in view: for a path that exists on disk it keeps ONE row (the row whose size and last write time equal the file's; if several, or none, the row with the latest `LastScanDateUTC`, then the highest Id) and touches or updates it as described above; every other row of that path is removed. If the file has to be hashed and that fails, nothing of the path is changed or removed. For a path that is gone, ALL its rows are removed (rows below an unreadable directory are all kept). The summary shows `SurplusRowsRemoved` (rows removed although their file exists, not included in `Removed`) and a warning is logged when there were any; `Removed` counts rows, so a gone path with two rows adds 2.

### Resuming a scan

A scan of a base path that is already in the database skips what is already scanned. The identity is the **path plus the computer that scanned it** (`ScanExecutionComputer`; ScanName, OriginComputer and PathBase are not part of it, a row of another computer is left alone and the file is inserted for this one). A file counts as **already scanned** when a row has the same size AND the same last write time as the file now (from the file itself, or from the directory listing with `TrustDirectoryMetadata`); it is then not opened. If rows exist for the path but none matches, the file **changed**: it is hashed and ONE existing row (the latest `LastScanDateUTC`, then the highest Id) is updated in place (size, last write time, hash, both scan dates), other rows of the path are removed; no row is added. If hashing fails, the rows are left untouched and an error item is stored. A path without rows is inserted. Surplus rows next to a matching row are removed as well, without hashing. **Only rows with the job's `OriginComputer` and `ScanName` are ever updated or removed.** A row of another scan (same path, same scanning computer, other `OriginComputer` or `ScanName`) still counts as "already scanned" when it has the same size and last write time (as before), but if the file changed and only rows of other scans exist for the path, a new row is inserted for this job and the other rows are left alone; stale rows of this job next to a matching row of another scan are left alone as well. The summary line shows `SuccessCount` (inserted and updated rows, a removed row counts nothing), `UpdatedExistingRows` and `RemovedSurplusRows`.

### Check Jobs

Check jobs verify that the files on disk still match what was recorded in the database. Their main purpose is to detect **bit rot** (silent corruption that preserves size and modification date) and any other unwanted changes. Unlike refresh, a check job **never modifies the database** — it writes a CSV report instead, which you can review and then replay back to the database with `applyCheck`.

Every file on disk is classified into one of these categories:

- **BitRotSuspect** — size and modification date match the database, but the SHA-512 hash differs. The headline case for silent corruption.
- **Modified** — size or modification date differs and the hash differs as well. A normal edit.
- **ModifiedNoHashChange** — size or modification date differs but the hash is unchanged. The file was touched (e.g. mtime rewritten by a tool) but its content is the same.
- **MissingOnDisk** — the database has a row for a file that is no longer on disk. Also used for **surplus rows**: if a path has several rows (a state older versions produced), the row that matches the file (size and last write time, else the latest scan) is compared as usual and every other row of that path is reported as `MissingOnDisk` with its own `ScanItemId`, so `applyCheck` removes exactly that row and one row per path remains. The summary shows how many (`of which surplus rows`) and a warning explains it. If the path is not on disk all its rows are reported (each subject to the unreadable-directory rule); if the file could not be processed, none is. With `IgnoreBasePath` an exact path match uses the primary row; for a moved path the first unclaimed row of the relative path only decides which path (the rows that share one exact path) belongs to the file, and the primary row inside it is chosen with the metadata of the file exactly as above. **Surplus rows are only reported when the report can bring the kept row up to date:** the primary row matches the file (Ok, or a pure path move, which applyCheck applies without a hash), or the report row of the primary row carries a hash. If the primary row changed and the check ran with `SkipHashCheck` (so its report row has no hash and applyCheck skips it), no surplus row of that path is reported, the path is counted in the summary (`PathsWithSurplusRowsNotReportedWithoutHashing`) and a warning says so; a check with hashing or a refresh cleans such paths up.
- Check only compares and reports rows with the job's own `OriginComputer` and `ScanName` (with `IgnoreBasePath` the rows are loaded by exactly that identity anyway). Rows of other scans under the same base path are never compared and never reported, also not as surplus or `MissingOnDisk`. If rows exist under the base path but none has the job's identity, check logs a warning that the job's `OriginComputer`/`ScanName` do not match the rows and continues (every file is then reported as `NewOnDisk`; applyCheck matches inserts by path only, so it skips such a row when another scan already has that path).
- The `_DB` columns (`FilenameAndPath_DB`, `PathBase_DB`, `FileSize_DB`, `MTime_DB`, `Hash_DB`) are the state of the database row **when the check ran**; `MTime_DB` is the stored UTC time. This is what report **schema version 2** means: `applyCheck` only changes or removes a row that is still in this state (see below). Version 1 reports (older versions of myDupFinder) are rejected by `applyCheck`: run the check again with this version.
- **NewOnDisk** — there is a file on disk that has no row in the database yet.

`Ok` files (everything matches) are not written to the report.
The report file is `check-{JobName}-{timestamp}.csv` under the job's `ReportPath`. It is RFC 4180–quoted, includes the freshly computed disk hashes, and starts with a self-contained `# Key=Value` header so `applyCheck` knows which database to update without needing the project file again.

Two flags tune behavior:

- **SkipHashCheck** — skip SHA-512 recomputation; only compare size and modification date. Much faster on huge corpora, but cannot detect `BitRotSuspect`.
- **IgnoreBasePath** — match database rows by their **relative sub-path** under `BasePath` even if their recorded `PathBase` differs. Useful when the whole tree was moved to a new drive letter or copied to a different computer. The check is scoped to rows with the same `OriginComputer` and `ScanName` as the check job. Files found at the new base path are flagged `PathMoved=true`, and `applyCheck` will rewrite their stored path. Rows from the old base path that no longer exist under the new base path are reported as `MissingOnDisk`, so `applyCheck` can remove them.

### Applying a Check Report

`applyCheck` reads a check CSV and applies the changes to the database recorded in the CSV header:

- **BitRotSuspect / Modified / ModifiedNoHashChange** rows update size, modification date, hash, and last-scan date **together**. When `PathMoved=true`, the path is rewritten as well. Hashes are taken straight from the CSV — no re-hashing. A hash in the database always belongs to the size and modification date stored with it, so size and modification date are never written without the hash from the same row.
- **MissingOnDisk** rows remove the matching database entry, but only if it is still in the state the report saw (see "Stale reports" below). A row that is already gone is skipped (it is the case "applied before").
- **NewOnDisk** rows insert a new database entry using the hash captured during check. Rows without a hash (produced by a `SkipHashCheck` run) are skipped. The row is only inserted if no row with exactly this path exists: if one exists with the same hash, size and modification date, the report was applied before and the row is counted as "already applied"; if one exists with other values, the row is skipped with a warning (the report is out of date for this file, run the check again).

**A report from a `SkipHashCheck` run.** Such a report has no hashes. A row of a file whose size or modification date changed (category `Modified`) is **not applied** (warning with the file name, counted as "without hash" in the summary): applying the new size and date while keeping the old hash would make a refresh believe that the stored hash is current, and it would never hash the file again. Run a check with hashing and apply that report instead, or run a refresh. This also applies to a `Modified` row with `PathMoved=true`: nothing of that row is applied, not even the new path; the next check with hashing reports it completely. The only hashless rows that are applied are rows of files that were only moved (`PathMoved=true`, size and modification date on disk equal the database row): they get their new path and base path, nothing else changes. `NewOnDisk` rows without a hash are skipped as before.

**Stale reports.** A report describes the database as it was when the check ran. `applyCheck` compares every update and `MissingOnDisk` row with the database row as it is now (path, base path, size, modification time and hash of the `_DB` columns, the hash ignoring case): if the row is still in the reported state, the row is applied. If it already has the state the report would write (the report was applied before), nothing is written and the row is counted as `AlreadyApplied`. If it is in any other state, or the row was removed, it was changed after the report was written (by a refresh, another `applyCheck`, a resumed scan): the row is **not applied**, a warning names the file, it is counted as `Skipped` (of which `stale`), and the check must be run again. This way an old report can never overwrite newer data with older values or remove a row that is no longer missing.

**Running it twice.** Applying the same report again is safe: inserts are skipped when the file is already there, updates whose row already has the state of the report are counted as `AlreadyApplied` and write nothing (not even the last-scan dates), and a removed row is skipped the second time. The rows are applied in database transactions of 500 report rows. A row that fails is logged and counted as an error and the other rows of its transaction are still applied. If a commit fails, the rows of that transaction are rolled back, an error names the number of report rows it covered, and they are counted as errors. If the process crashes, at most the open transaction (up to 500 rows) is lost; apply the same report again and only what is missing is applied. The report is read twice and never held in memory: first every row is parsed (a malformed row anywhere makes `applyCheck` stop before anything is applied), then the rows are applied. If the file changes between the two passes so that a row can no longer be read, the open transaction is rolled back and `applyCheck` fails with an error (transactions that were committed before stay). A quoted field of the CSV may contain line breaks (a path with a line break is written and read back exactly). The summary line shows `Updated`, `Inserted`, `Removed`, `AlreadyApplied`, `Skipped` (of which `without hash` and `stale`), `Errors` and `FailedCommits`; the counters only contain what was committed.

Before running `applyCheck`, open the CSV and delete any row you do not want applied — for instance a `BitRotSuspect` you want to investigate manually first. Anything left in the CSV will be applied.

## Roadmap

- :heavy_check_mark: Scan Files and generate hash information
- :heavy_check_mark: Parallelize scan to all cores (this works better than expected... the computer is unusable...)
- :heavy_check_mark: Single threaded scan...
- :heavy_check_mark: Store all the file and hash information in a sqlite db
- :heavy_check_mark: Cancel / Resume scan
- :heavy_check_mark: Find dups in one database

    ```sql
    SELECT * FROM ScanItems WHERE FileSha512Hash IN (SELECT FileSha512Hash FROM ScanItems GROUP BY FileSHA512Hash HAVING COUNT(*) >1)
    ```

- :heavy_check_mark: Create .csv reports of dups
- :heavy_check_mark: Find dups in different databases
- Visually show the dups and manually change the state
- Delete / Move the dups
- :heavy_check_mark: Refresh a database
- :heavy_check_mark: Check a database with the original files (bit rot, changes of files) and create a csv report.
- :heavy_check_mark: Update the database from the csv report so no need to rehash everything.
