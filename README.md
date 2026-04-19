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

# Run the duplicate finder with a project file
myDupFinder run projectfile.xml
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

## Roadmap:
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
- Check a database with the original files (bit rot, changes of files)
