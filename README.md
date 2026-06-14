# myDupFinder

Find duplicates of files (or checks if the backup still is ok...)

## Project state: in develompent... unusable right now

Build with latest dotnet relased:   ![.NET 10](https://github.com/rogerbriggen/myDupFinder/workflows/.NET%2010/badge.svg)

## Command Line Usage

```bash
# Generate an example project file
myDupFinder exampleproject projectfile.xml

# Validate a project file without running (dry run)
myDupFinder dryrun projectfile.xml

# Run the duplicate finder with a project file (executes all scan, check, find-dups, and refresh jobs)
myDupFinder run projectfile.xml

# Apply a check report back to the database referenced in its header
myDupFinder applyCheck check-MyJob-20260613-100000.csv
```

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
    </MyDupFinderRefreshJobDTO>
  </MyDupFinderRefreshJobDTOs>

</MyDupFinderProjectDTO>
```

### Find Duplicates Modes

The `MyDupFinderFindDupsJobDTO` supports two modes of operation:

- **Same database**: Leave `DatabaseFile` empty to find duplicates within `DatabaseFileBase` only.
- **Cross-database**: Set `DatabaseFile` to a second database path to find files in `DatabaseFileBase` that also exist in `DatabaseFile`. This allows you to scan multiple folders into separate databases and then compare them.

You get a csv report. The path in the csv report is alsways from the base database.

### FindDupsMode Values

- `FindOnlyDups` — reports only duplicate files
- `FindDupsTheWholeLot` — reports everything: duplicates, missing files, new files, changed files

### Refresh Jobs

Refresh jobs update an existing database to reflect the current state of the files on disk. This is useful when files have been added, modified, or deleted since the last scan.

The refresh is smart and fast:

- **New files** on disk are added to the database with their SHA-512 hash.
- **Modified files** (where the file size or last modification date has changed) are updated with a recalculated hash.
- **Unchanged files** (same size and date) skip hash recalculation — only the last scan date is updated.
- **Deleted files** (in the database but no longer on disk) are removed from the database.

### Check Jobs

Check jobs verify that the files on disk still match what was recorded in the database. Their main purpose is to detect **bit rot** (silent corruption that preserves size and modification date) and any other unwanted changes. Unlike refresh, a check job **never modifies the database** — it writes a CSV report instead, which you can review and then replay back to the database with `applyCheck`.

Every file on disk is classified into one of these categories:

- **BitRotSuspect** — size and modification date match the database, but the SHA-512 hash differs. The headline case for silent corruption.
- **Modified** — size or modification date differs and the hash differs as well. A normal edit.
- **ModifiedNoHashChange** — size or modification date differs but the hash is unchanged. The file was touched (e.g. mtime rewritten by a tool) but its content is the same.
- **MissingOnDisk** — the database has a row for a file that is no longer on disk.
- **NewOnDisk** — there is a file on disk that has no row in the database yet.

`Ok` files (everything matches) are not written to the report.

The report file is `check-{JobName}-{timestamp}.csv` under the job's `ReportPath`. It is RFC 4180–quoted, includes the freshly computed disk hashes, and starts with a self-contained `# Key=Value` header so `applyCheck` knows which database to update without needing the project file again.

Two flags tune behavior:

- **SkipHashCheck** — skip SHA-512 recomputation; only compare size and modification date. Much faster on huge corpora, but cannot detect `BitRotSuspect`.
- **IgnoreBasePath** — match database rows by their **relative sub-path** under `BasePath` even if their recorded `PathBase` differs. Useful when the whole tree was moved to a new drive letter or copied to a different computer. The check is scoped to rows with the same `OriginComputer` and `ScanName` as the check job. Files found at the new base path are flagged `PathMoved=true`, and `applyCheck` will rewrite their stored path. Rows from the old base path that no longer exist under the new base path are reported as `MissingOnDisk`, so `applyCheck` can remove them.

### Applying a Check Report

`applyCheck` reads a check CSV and applies the changes to the database recorded in the CSV header:

- **BitRotSuspect / Modified / ModifiedNoHashChange** rows update size, modification date, hash, and last-scan date. When `PathMoved=true`, the path is rewritten as well. Hashes are taken straight from the CSV — no re-hashing.
- **MissingOnDisk** rows remove the matching database entry.
- **NewOnDisk** rows insert a new database entry using the hash captured during check. Rows without a hash (produced by a `SkipHashCheck` run) are skipped.

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
