// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Walks the disk and compares every file against the database without modifying it.
/// Each disk file is classified as Ok, BitRotSuspect, Modified, ModifiedNoHashChange, or NewOnDisk.
/// DB rows that have no matching disk file are reported as MissingOnDisk.
/// The result is written as a self-contained CSV that applyCheck can later replay against the DB.
/// </summary>
internal class CheckRunner : BasicRunner<CheckRunner>, ICheckRunner
{
    public CheckRunner(MyDupFinderCheckJobDTO checkJobDTO, ILogger<CheckRunner>? logger, IServiceProvider serviceProvider) : this(checkJobDTO, logger, serviceProvider, null)
    {
    }

    /// <param name="fileSource">Only for tests: where the files come from (for example with unreadable directories). Default: the real file system.</param>
    /// <param name="fileHasher">Only for tests: a hasher with hooks or another buffer size. Default: <see cref="FileHasher.Default"/>.</param>
    internal CheckRunner(MyDupFinderCheckJobDTO checkJobDTO, ILogger<CheckRunner>? logger, IServiceProvider serviceProvider, IFileEnumerationSource? fileSource, FileHasher? fileHasher = null) : base(logger, serviceProvider)
    {
        CheckJobDTO = checkJobDTO;
        _fileSource = fileSource ?? FileTreeEnumerationSource.Instance;
        _fileHasher = fileHasher ?? FileHasher.Default;
        DbInserts = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        Throughput = new ThroughputTracker(_logger);
    }

    private bool _disposed;
    private MyDupFinderCheckJobDTO CheckJobDTO { get; }
    private ScanJobDBInserts DbInserts { get; }
    private readonly IFileEnumerationSource _fileSource;
    private readonly FileHasher _fileHasher;

    public int BitRotSuspectCount { get; private set; }
    public int ModifiedCount { get; private set; }
    public int ModifiedNoHashChangeCount { get; private set; }
    public int MissingOnDiskCount { get; private set; }
    public int NewOnDiskCount { get; private set; }
    public int OkCount { get; private set; }
    public int ErrorCount { get; private set; }

    /// <summary>
    /// Rows reported as MissingOnDisk although the file of their path exists: a path that has more than one row (a state that older versions of the scan produced)
    /// has one primary row (see <see cref="PrimaryRowChooser"/>) that is compared with the file, every other row of the path is surplus. Included in <see cref="MissingOnDiskCount"/>.
    /// The category is the same as for a row whose file is gone on purpose: applyCheck removes exactly the row with the id of the report row.
    /// </summary>
    public int SurplusRowsReportedMissingCount { get; private set; }

    /// <summary>
    /// Paths with several rows whose surplus rows were NOT reported, because the check ran without hashing (SkipHashCheck) and the primary row of the path changed:
    /// the report row of the primary row has no hash, applyCheck would skip it, and removing the other rows would leave only a stale row. A check with hashing or a refresh cleans these paths up.
    /// A surplus row is reported only when the primary row matches the file (Ok, or a pure path move that applyCheck applies without a hash) or the report row of the primary row carries a hash.
    /// </summary>
    public int PathsWithSurplusRowsNotReportedCount { get; private set; }

    public string? ReportFilePath { get; private set; }

    /// <summary>
    /// Rows that were NOT reported as MissingOnDisk although no file was enumerated for them, because they lie below a directory that
    /// could not be read. These files might well still exist.
    /// </summary>
    public int UnreadableProtectedCount { get; private set; }

    /// <summary>
    /// Directories that could not be read completely during the enumeration (final after the enumeration ended).
    /// </summary>
    public IReadOnlyList<UnreadableDirectory> UnreadableDirectories { get; private set; } = Array.Empty<UnreadableDirectory>();

    /// <summary>
    /// Files, bytes and stage times of the current run. Files that are not hashed (SkipHashCheck) count as files
    /// but add no hashed bytes. The enumerate time covers the directory listing and the metadata (size, timestamps) of the files (one call per file
    /// unless TrustDirectoryMetadata is set), the same as in scan and refresh. The database time is the initial load of the rows.
    /// </summary>
    internal ThroughputTracker Throughput { get; private set; }

    public override void Start(CancellationToken token)
    {
        base.Start(token);

        try
        {
            DbInserts.SetupDB(CheckJobDTO.ScanJobDTO.DatabaseFile, CheckJobDTO.ScanJobDTO.DatabaseSynchronous);
            _logger.LogInformation("Database state: {diagnostics}", DbInserts.GetDatabaseDiagnostics());
            Throughput = new ThroughputTracker(_logger);
            Check();
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            // Everything that is not handled per file: the database setup, the initial load of the rows, the creation of the report file, the missing-detection, the flush of the report
            _logger.LogError(ex, "Check failed. {summary}", BuildSummary());
            if (RunnerState == IService.EServiceState.running)
            {
                RunnerState = IService.EServiceState.failed;
            }
        }
        finally
        {
            try
            {
                DbInserts.Dispose();
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Releasing the database failed.");
            }
        }
    }

    private void Check()
    {
        var scanJob = CheckJobDTO.ScanJobDTO;
        var basePath = scanJob.BasePath;
        //No throttling when ThrottlePercent is 0
        DiscThrottle? discThrottle = DiscThrottle.CreateForJob(scanJob.ThrottlePercent);

        long loadStart = Throughput.StartStage();
        var existingItems = CheckJobDTO.IgnoreBasePath
            ? DbInserts.GetAllItemsByScanIdentity(scanJob.OriginComputer, scanJob.ScanName)
            : DbInserts.GetAllItemsByBasePath(basePath);
        Throughput.EndStage(ThroughputStage.Database, loadStart);
        // The identity rule (the one place in this runner): only the rows of this job's OriginComputer and ScanName are compared and reported. Without IgnoreBasePath the rows
        // are loaded by base path only, so rows of other computers or other named scans with equal path strings can be among them: they are never compared, reported or counted.
        // (With IgnoreBasePath the rows are loaded by exactly this identity, so the filter keeps everything.)
        var ownItems = existingItems.Where(i => PrimaryRowChooser.HasIdentity(i, scanJob.OriginComputer, scanJob.ScanName)).ToList();
        if (existingItems.Count > 0 && ownItems.Count == 0)
        {
            var identities = existingItems.Select(i => $"{i.OriginComputer}/{i.ScanName}").Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal);
            _logger.LogWarning("Check: the database has {count} rows for base path {basePath}, but none belongs to this job's OriginComputer/ScanName ({origin}/{scan}); the job's OriginComputer/ScanName do not match the rows. The identities that exist under this base path: {identities}. These rows are not compared, so every file is reported as NewOnDisk.", existingItems.Count, basePath, scanJob.OriginComputer, scanJob.ScanName, string.Join(", ", identities));
        }
        else if (ownItems.Count < existingItems.Count)
        {
            _logger.LogInformation("Check: {foreign} database rows for base path {basePath} belong to another OriginComputer/ScanName than this job ({origin}/{scan}) and are ignored", existingItems.Count - ownItems.Count, basePath, scanJob.OriginComputer, scanJob.ScanName);
        }
        // All rows of a path are kept (older versions of the scan could leave several rows for one path)
        var existingByPath = PrimaryRowChooser.GroupByPath(ownItems);
        // Rows that are claimed: the row that was compared with a file, the surplus rows of that file's path (they are not available for a relative match either)
        var seenIds = new HashSet<Guid>();
        // The surplus rows among the claimed ones (other rows of a path whose file was found): reported as MissingOnDisk anyway
        var surplusIds = new HashSet<Guid>();

        _logger.LogInformation(
            "Check: Found {count} existing items in database for base path {basePath} (IgnoreBasePath={ignoreBasePath})",
            existingItems.Count,
            basePath,
            CheckJobDTO.IgnoreBasePath);

        var reportPath = BuildReportPath(scanJob);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");

        using var writer = CreateReportWriter(reportPath);
        CheckReportCsv.WriteHeader(writer, new CheckReportHeader
        {
            SchemaVersion = CheckReportHeader.CurrentSchemaVersion,
            DatabaseFile = Path.GetFullPath(scanJob.DatabaseFile),
            BasePath = basePath,
            JobName = scanJob.JobName,
            OriginComputer = scanJob.OriginComputer,
            ScanName = scanJob.ScanName,
            SkipHashCheck = CheckJobDTO.SkipHashCheck,
            IgnoreBasePath = CheckJobDTO.IgnoreBasePath,
            GeneratedUTC = DateTime.UtcNow,
        });

        // With IgnoreBasePath rows are also matched by their relative path: index them once instead of walking all rows for every file
        var relativeIndex = CheckJobDTO.IgnoreBasePath ? new RelativePathIndex(existingByPath.Values.SelectMany(rows => rows), basePath) : null;

        using var fileEnumerator = _fileSource.Open(basePath);
        UnreadableDirectories = fileEnumerator.UnreadableDirectories;
        int unreadableLogged = 0;
        while (true)
        {
            long enumerateStart = Throughput.StartStage();
            bool hasNext;
            try
            {
                hasNext = fileEnumerator.MoveNext();
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                // The base path itself cannot be listed: no file was enumerated, so no "missing" row may be reported
                _logger.LogError(ex, "Error enumerating files in {basePath}. No missing files are reported.", basePath);
                writer.Flush();
                RunnerState = IService.EServiceState.failed;
                return;
            }
            finally
            {
                Throughput.EndStage(ThroughputStage.Enumerate, enumerateStart);
            }
            UnreadableDirectoryLog.LogNew(_logger, fileEnumerator.UnreadableDirectories, ref unreadableLogged);
            if (!hasNext)
            {
                break;
            }
            FileEntryInfo currentEntry = fileEnumerator.Current;
            string currentFile = currentEntry.FullPath;

            if (CancelToken.IsCancellationRequested)
            {
                writer.Flush();
                _logger.LogInformation("Check was cancelled by user... {summary}", BuildSummary());
                Throughput.LogSummary("Check cancelled");
                RunnerState = IService.EServiceState.cancelled;
                return;
            }

            long hashedBytes = 0;
            _rowsOfCurrentFile = null;
            try
            {
                hashedBytes = ProcessFile(currentEntry, basePath, existingByPath, relativeIndex, seenIds, surplusIds, writer);
            }
            catch (OperationCanceledException) when (CancelToken.IsCancellationRequested)
            {
                // Cancelled inside a big file: no report row and no count for it, the report written so far is flushed, the run ends as cancelled
                writer.Flush();
                _logger.LogInformation("Check was cancelled by user while a file was read... {summary}", BuildSummary());
                Throughput.LogSummary("Check cancelled");
                RunnerState = IService.EServiceState.cancelled;
                return;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Check: Error processing file {file}", currentFile);
                ErrorCount++;
                // The file exists in the listing, so none of the rows of its path may be reported as missing: claim ALL rows of the path that belongs to it
                // (the rows ProcessFile already matched, else the rows of the exact path, or with IgnoreBasePath the path of the first unclaimed row with the same relative path)
                var rowsOfFile = _rowsOfCurrentFile;
                if (rowsOfFile is null && !existingByPath.TryGetValue(currentFile, out rowsOfFile))
                {
                    var claimed = relativeIndex?.Find(currentFile, seenIds);
                    rowsOfFile = claimed is null ? null : existingByPath[claimed.FilenameAndPath];
                }
                if (rowsOfFile is not null)
                {
                    foreach (var row in rowsOfFile)
                    {
                        seenIds.Add(row.Id);
                        surplusIds.Remove(row.Id);
                    }
                }
            }

            Throughput.AddFile(hashedBytes);
            Throughput.LogProgressIfDue();

            // Throttle
            discThrottle?.Throttle(CancelToken);
        }

        // Anything in the DB under this base path that we did not see on disk is now missing, except below directories that could not be read:
        // the enumeration is complete here (the loop ended because MoveNext returned false; a cancel or an error returned before), so the list
        // of unreadable directories is final. Files below them were not enumerated although they may exist.
        // (UnreadableDirectories is the live list of the enumeration, final from here on)
        var unreadableMatcher = new UnreadableDirectoryMatcher(UnreadableDirectories.Select(d => d.Path), basePath);
        foreach (var dbItem in existingByPath.Values.SelectMany(rows => rows))
        {
            if (CancelToken.IsCancellationRequested)
            {
                writer.Flush();
                _logger.LogInformation("Check was cancelled by user during missing-detection... {summary}", BuildSummary());
                Throughput.LogSummary("Check cancelled");
                RunnerState = IService.EServiceState.cancelled;
                return;
            }

            bool isSurplus = surplusIds.Contains(dbItem.Id);
            if (seenIds.Contains(dbItem.Id) && !isSurplus)
            {
                continue;
            }

            // A surplus row belongs to a path whose file exists, so the protection of unreadable directories does not apply to it
            bool protectedByUnreadable = !isSurplus && (CheckJobDTO.IgnoreBasePath
                ? unreadableMatcher.IsProtectedByAnyBasePath(dbItem.FilenameAndPath, dbItem.PathBase)
                : unreadableMatcher.IsProtected(dbItem.FilenameAndPath));
            if (protectedByUnreadable)
            {
                UnreadableProtectedCount++;
                _logger.LogTrace("Check: not reported as missing, below a directory that could not be read: {file}", dbItem.FilenameAndPath);
                continue;
            }
            var row = new CheckReportRow
            {
                Category = CheckCategory.MissingOnDisk,
                ScanItemId = dbItem.Id,
                FilenameAndPathDB = dbItem.FilenameAndPath,
                PathBaseDB = dbItem.PathBase,
                FileSizeDB = dbItem.FileSize,
                MTimeDB = dbItem.FileLastModificationUTC,
                HashDB = dbItem.FileSha512Hash,
            };
            CheckReportCsv.WriteRow(writer, row);
            MissingOnDiskCount++;
            if (isSurplus)
            {
                SurplusRowsReportedMissingCount++;
            }
            _logger.LogTrace("Check: MissingOnDisk {file}{surplus}", dbItem.FilenameAndPath, isSurplus ? " (surplus row of a path that has several rows)" : string.Empty);
        }

        writer.Flush();
        RunnerState = IService.EServiceState.finished;
        if (PathsWithSurplusRowsNotReportedCount > 0)
        {
            _logger.LogWarning("Check: the surplus rows of {paths} paths that have several rows were not reported because the check ran without hashing (SkipHashCheck) and the file of the path changed, so the row that is kept cannot be brought up to date by this report. Run a check with hashing or a refresh to clean them up.", PathsWithSurplusRowsNotReportedCount);
        }
        if (SurplusRowsReportedMissingCount > 0)
        {
            _logger.LogWarning("Check: {surplus} surplus rows were reported as MissingOnDisk. Some paths have more than one row in the database (an old database state). One row per path is compared with the file (the one that matches size and last write time, otherwise the one scanned last), every other row of the path is reported as MissingOnDisk with its own id, so that applyCheck removes exactly that row.", SurplusRowsReportedMissingCount);
        }
        if (UnreadableDirectories.Count > 0)
        {
            _logger.LogWarning("Check: {count} directories could not be read completely. {kept} database rows below them were NOT reported as missing (their files were not checked at all). Directories: {directories}", UnreadableDirectories.Count, UnreadableProtectedCount, UnreadableDirectoryLog.Join(UnreadableDirectories));
        }
        _logger.LogInformation("Check finished. Report={report} {summary}", ReportFilePath, BuildSummary());
        Throughput.LogSummary("Check finished");
    }

    /// <returns>The number of bytes that were hashed for this file (0 when it was not hashed or hashing failed).</returns>
    private long ProcessFile(FileEntryInfo listedEntry, string basePath, Dictionary<string, List<ScanItemDto>> existingByPath, RelativePathIndex? relativeIndex, HashSet<Guid> seenIds, HashSet<Guid> surplusIds, TextWriter writer)
    {
        // Authoritative values from the file itself (one call) unless TrustDirectoryMetadata is set; read now, right before the file is processed.
        // A failure is an error of this file (the caller counts and logs it, the row of the file is then not reported as missing).
        long metadataStart = Throughput.StartStage();
        FileEntryInfo currentEntry;
        try
        {
            currentEntry = FileMetadata.Resolve(listedEntry, CheckJobDTO.ScanJobDTO.TrustDirectoryMetadata);
        }
        finally
        {
            Throughput.EndStage(ThroughputStage.Enumerate, metadataStart);
        }
        string currentFile = currentEntry.FullPath;
        long fileSize = currentEntry.Length;
        DateTime fileLastModificationUTC = currentEntry.LastWriteTimeUtc;

        ScanItemDto? matched = null;
        bool pathMoved = false;
        if (existingByPath.TryGetValue(currentFile, out var exactRows))
        {
            // Exact path: the primary row of the path is compared with the file
            matched = PrimaryRowChooser.Choose(exactRows, fileSize, fileLastModificationUTC);
        }
        else if (CheckJobDTO.IgnoreBasePath)
        {
            // The first unclaimed row of the relative path only decides WHICH PATH (the group of rows with this exact FilenameAndPath) belongs to the file;
            // the primary row inside the group is chosen with the metadata of the file, exactly as for an exact path (a row that matches wins, else the latest scan)
            var firstOfGroup = relativeIndex?.Find(currentFile, seenIds);
            if (firstOfGroup is not null)
            {
                matched = PrimaryRowChooser.Choose(existingByPath[firstOfGroup.FilenameAndPath], fileSize, fileLastModificationUTC);
                pathMoved = true;
            }
        }

        if (matched is null)
        {
            // No DB row at all => new on disk. Compute hash so applyCheck can insert without re-reading.
            FileHashResult? newHashed = CheckJobDTO.SkipHashCheck ? null : ComputeHashOrNull(currentFile);
            string? newHash = newHashed?.Hash;
            if (newHashed is null && !CheckJobDTO.SkipHashCheck)
            {
                // Hashing was attempted and failed (ComputeHashOrNull logged it): the row is still reported, without hash
                ErrorCount++;
            }
            var row = new CheckReportRow
            {
                Category = CheckCategory.NewOnDisk,
                ScanItemId = Guid.Empty,
                FilenameAndPathDisk = currentFile,
                PathBaseDisk = basePath,
                FileSizeDisk = fileSize,
                MTimeDisk = fileLastModificationUTC,
                HashDisk = newHash,
                CreationTimeDisk = currentEntry.CreationTimeUtc,
            };
            CheckReportCsv.WriteRow(writer, row);
            NewOnDiskCount++;
            _logger.LogTrace("Check: NewOnDisk {file}", currentFile);
            return newHashed?.BytesRead ?? 0;
        }

        // Claim the matched row and every other row of its path; the others are surplus and are reported as MissingOnDisk at the end
        // (unless this file ends in an error: then nothing of the path is reported, see the handler in Check)
        var rowsOfPath = existingByPath[matched.FilenameAndPath];
        _rowsOfCurrentFile = rowsOfPath;
        seenIds.Add(matched.Id);
        foreach (var other in rowsOfPath)
        {
            if (other.Id != matched.Id)
            {
                seenIds.Add(other.Id);
                surplusIds.Add(other.Id);
            }
        }

        bool sizeOrMtimeChanged = matched.FileSize != fileSize || matched.FileLastModificationUTC != fileLastModificationUTC;

        if (CheckJobDTO.SkipHashCheck)
        {
            if (!sizeOrMtimeChanged && !pathMoved)
            {
                OkCount++;
                return 0;
            }

            if (sizeOrMtimeChanged)
            {
                // The report row of the primary row has no hash, so applyCheck will skip it (SkippedNoHash) and the row stays stale. Removing the other rows of the path
                // on the strength of such a report could leave only the stale row (or lose the row with a valid hash), so they are not reported; a check with hashing or a refresh cleans them up.
                if (rowsOfPath.Count > 1)
                {
                    foreach (var other in rowsOfPath)
                    {
                        surplusIds.Remove(other.Id);
                    }
                    PathsWithSurplusRowsNotReportedCount++;
                    _logger.LogTrace("Check: surplus rows of {file} are not reported, the check runs without hashing and the row that is kept cannot be brought up to date", currentFile);
                }
            }

            // Treat as Modified-like; hash is unknown so leave HashDisk null. applyCheck will then
            // update size/mtime/path only and leave Hash + LastSha512ScanDateUTC alone.
            var row = new CheckReportRow
            {
                Category = sizeOrMtimeChanged ? CheckCategory.Modified : CheckCategory.ModifiedNoHashChange,
                PathMoved = pathMoved,
                ScanItemId = matched.Id,
                FilenameAndPathDB = matched.FilenameAndPath,
                FilenameAndPathDisk = currentFile,
                PathBaseDB = matched.PathBase,
                PathBaseDisk = basePath,
                FileSizeDB = matched.FileSize,
                FileSizeDisk = fileSize,
                MTimeDB = matched.FileLastModificationUTC,
                MTimeDisk = fileLastModificationUTC,
                HashDB = matched.FileSha512Hash,
                HashDisk = null,
            };
            CheckReportCsv.WriteRow(writer, row);
            if (sizeOrMtimeChanged)
            {
                ModifiedCount++;
            }
            else
            {
                ModifiedNoHashChangeCount++;
            }
            return 0;
        }

        FileHashResult? hashed = ComputeHashOrNull(currentFile);
        if (hashed is null)
        {
            ErrorCount++;
            // The file could not be verified: none of the rows of its path is reported
            foreach (var other in rowsOfPath)
            {
                surplusIds.Remove(other.Id);
            }
            return 0;
        }
        string diskHash = hashed.Value.Hash;
        long bytesHashed = hashed.Value.BytesRead;

        bool hashDiffers = !string.Equals(diskHash, matched.FileSha512Hash, StringComparison.OrdinalIgnoreCase);

        CheckCategory? category = null;
        if (sizeOrMtimeChanged && hashDiffers)
        {
            category = CheckCategory.Modified;
            ModifiedCount++;
            _logger.LogTrace("Check: Modified {file}", currentFile);
        }
        else if (sizeOrMtimeChanged && !hashDiffers)
        {
            category = CheckCategory.ModifiedNoHashChange;
            ModifiedNoHashChangeCount++;
            _logger.LogTrace("Check: ModifiedNoHashChange {file}", currentFile);
        }
        else if (!sizeOrMtimeChanged && hashDiffers)
        {
            category = CheckCategory.BitRotSuspect;
            BitRotSuspectCount++;
            _logger.LogWarning("Check: BitRotSuspect {file}", currentFile);
        }
        else if (pathMoved)
        {
            // Size, mtime and hash all match - only the location moved. Report so applyCheck can rewrite PathBase.
            category = CheckCategory.ModifiedNoHashChange;
            ModifiedNoHashChangeCount++;
            _logger.LogTrace("Check: PathMoved (no content change) {file}", currentFile);
        }
        else
        {
            OkCount++;
            return bytesHashed;
        }

        var fullRow = new CheckReportRow
        {
            Category = category.Value,
            PathMoved = pathMoved,
            ScanItemId = matched.Id,
            FilenameAndPathDB = matched.FilenameAndPath,
            FilenameAndPathDisk = currentFile,
            PathBaseDB = matched.PathBase,
            PathBaseDisk = basePath,
            FileSizeDB = matched.FileSize,
            FileSizeDisk = fileSize,
            MTimeDB = matched.FileLastModificationUTC,
            MTimeDisk = fileLastModificationUTC,
            HashDB = matched.FileSha512Hash,
            HashDisk = diskHash,
        };
        CheckReportCsv.WriteRow(writer, fullRow);
        return bytesHashed;
    }

    /// <returns>The result of the hasher, or null when the file could not be read (logged).</returns>
    /// <exception cref="OperationCanceledException">The run was cancelled while the file was read.</exception>
    private FileHashResult? ComputeHashOrNull(string filePath)
    {
        long hashStart = Throughput.StartStage();
        try
        {
            return _fileHasher.Compute(filePath, CancelToken);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex) when (!(ex is OperationCanceledException && CancelToken.IsCancellationRequested))
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Check: Failed to hash {file}", filePath);
            return null;
        }
        finally
        {
            Throughput.EndStage(ThroughputStage.Hash, hashStart);
        }
    }

    /// <summary>
    /// Highest number of report files with the same name (<c>-2</c> ... <c>-N</c> suffix) that are tried before the check fails.
    /// </summary>
    private const int MaxReportNameAttempts = 1000;

    /// <summary>
    /// Creates the report file exclusively (an existing file is never overwritten). When the name is taken (two checks of the same job in the same second),
    /// <c>-2</c>, <c>-3</c> ... is added before the extension. <see cref="ReportFilePath"/> is the name that was used.
    /// </summary>
    internal StreamWriter CreateReportWriter(string reportPath)
    {
        string withoutExtension = Path.Combine(Path.GetDirectoryName(reportPath) ?? ".", Path.GetFileNameWithoutExtension(reportPath));
        string extension = Path.GetExtension(reportPath);
        for (int attempt = 1; attempt <= MaxReportNameAttempts; attempt++)
        {
            string candidate = attempt == 1 ? reportPath : string.Create(CultureInfo.InvariantCulture, $"{withoutExtension}-{attempt}{extension}");
            FileStream stream;
            try
            {
                stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Already exists (not any other IO error): try the next name
                continue;
            }
            ReportFilePath = candidate;
            // Same encoding as StreamWriter(path): UTF-8 without BOM
            return new StreamWriter(stream);
        }
        throw new IOException($"Could not create a report file: {reportPath} and the names with suffix -2 to -{MaxReportNameAttempts} all exist.");
    }

    internal static string BuildReportPath(MyDupFinderScanJobDTO scanJob)
    {
        var folder = string.IsNullOrWhiteSpace(scanJob.ReportPath)
            ? Path.GetDirectoryName(Path.GetFullPath(scanJob.DatabaseFile)) ?? "."
            : scanJob.ReportPath;
        var safeJobName = string.Concat(scanJob.JobName.Split(Path.GetInvalidFileNameChars()));
        var fileName = string.Format(CultureInfo.InvariantCulture, "check-{0}-{1:yyyyMMdd-HHmmss}.csv", safeJobName, DateTime.UtcNow);
        return Path.Combine(folder, fileName);
    }

    // The rows of the path of the file that ProcessFile is working on (null before it matched a row), for the error handler in Check
    private List<ScanItemDto>? _rowsOfCurrentFile;

    private string BuildSummary() =>
        string.Format(CultureInfo.InvariantCulture,
            "BitRot={0} Modified={1} ModifiedNoHashChange={2} Missing={3} (of which surplus rows of paths with several rows={4}) New={5} Ok={6} Errors={7} UnreadableDirectories={8} NotReportedMissingBecauseUnreadable={9} PathsWithSurplusRowsNotReportedWithoutHashing={10}",
            BitRotSuspectCount, ModifiedCount, ModifiedNoHashChangeCount, MissingOnDiskCount, SurplusRowsReportedMissingCount, NewOnDiskCount, OkCount, ErrorCount, UnreadableDirectories.Count, UnreadableProtectedCount, PathsWithSurplusRowsNotReportedCount);

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        if (disposing)
        {
            // No managed disposables of our own; DbInserts is disposed at the end of Start().
        }
        _disposed = true;
        base.Dispose(disposing);
    }
}
