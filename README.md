# myDupFinder
Find duplicates of files (or checks if the backup still is ok...)

## Project state: in develompent... unusable right now

Build with latest dotnet relased:   ![.NET Core](https://github.com/rogerbriggen/myDupFinder/workflows/.NET%20Core/badge.svg)

## Roadmap:
- :heavy_check_mark: Scan Files and generate hash information
- :heavy_check_mark: Parallelize scan to all cores (this works better than expected... the computer is unusable...)
- :heavy_check_mark: Single threaded scan...
- :heavy_check_mark: Store all the file and hash information in a sqlite db
- :heavy_check_mark: Cancel / Resume scan
- Find dups in one database
    ```sql
    SELECT * FROM ScanItems WHERE FileSha512Hash IN (SELECT FileSha512Hash FROM ScanItems GROUP BY FileSHA512Hash HAVING COUNT(*) >1)
    ``` 
- Find dups in different databases
- Visually show the dups and manually change the state
- Delete / Move the dups
- Refresh a database
- Check a database with the original files (bit rot, changes of files)
