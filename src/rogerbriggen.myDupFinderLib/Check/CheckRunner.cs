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
/// A file that changes while it is hashed (twice in a row, see <see cref="FileHasher"/>) gets no row, its database row is not reported as missing, and it is counted in
/// <see cref="ErrorCount"/> and <see cref="UnstableCount"/>. Without TrustDirectoryMetadata the size and last write time compared and reported with a hash are the ones of the
/// open file that was hashed; with it the values of the directory listing.
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
    /// Files that changed while they were read, twice in a row. They are also counted in <see cref="ErrorCount"/>. No report row is written for them
    /// (a hash that matches neither state of the file would be misleading), and their database row is not reported as <c>MissingOnDisk</c>.
    /// </summary>
    public int UnstableCount { get; private set; }

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

        DbInserts.SetupDB(CheckJobDTO.ScanJobDTO.DatabaseFile);
        _logger.LogInformation("Database state: {diagnostics}", DbInserts.GetDatabaseDiagnostics());
        Throughput = new ThroughputTracker(_logger);
        try
        {
            Check();
        }
        finally
        {
            DbInserts.Dispose();
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
        var existingByPath = new Dictionary<string, ScanItemDto>(StringComparer.Ordinal);
        foreach (var item in existingItems)
        {
            existingByPath[item.FilenameAndPath] = item;
        }
        var seenIds = new HashSet<Guid>();

        _logger.LogInformation(
            "Check: Found {count} existing items in database for base path {basePath} (IgnoreBasePath={ignoreBasePath})",
            existingItems.Count,
            basePath,
            CheckJobDTO.IgnoreBasePath);

        var reportPath = BuildReportPath(scanJob);
        ReportFilePath = reportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");

        using var writer = new StreamWriter(reportPath, append: false);
        CheckReportCsv.WriteHeader(writer, new CheckReportHeader
        {
            SchemaVersion = CheckReportHeader.CurrentSchemaVersion,
            DatabaseFile = scanJob.DatabaseFile,
            BasePath = basePath,
            JobName = scanJob.JobName,
            OriginComputer = scanJob.OriginComputer,
            ScanName = scanJob.ScanName,
            SkipHashCheck = CheckJobDTO.SkipHashCheck,
            IgnoreBasePath = CheckJobDTO.IgnoreBasePath,
            GeneratedUTC = DateTime.UtcNow,
        });

        // With IgnoreBasePath rows are also matched by their relative path: index them once instead of walking all rows for every file
        var relativeIndex = CheckJobDTO.IgnoreBasePath ? new RelativePathIndex(existingByPath.Values, basePath) : null;

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
                RunnerState = IService.EServiceState.finished;
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
            int claimedBefore = seenIds.Count;
            try
            {
                hashedBytes = ProcessFile(currentEntry, basePath, existingByPath, relativeIndex, seenIds, writer);
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
                // The file exists in the listing, so its row must not be reported as missing: claim the row that belongs to it
                // (exact path, or with IgnoreBasePath the first unclaimed row with the same relative path)
                var claimed = seenIds.Count != claimedBefore ? null : existingByPath.TryGetValue(currentFile, out var exactRow) ? exactRow : relativeIndex?.Find(currentFile, seenIds);
                if (claimed is not null)
                {
                    seenIds.Add(claimed.Id);
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
        foreach (var kvp in existingByPath)
        {
            if (CancelToken.IsCancellationRequested)
            {
                writer.Flush();
                _logger.LogInformation("Check was cancelled by user during missing-detection... {summary}", BuildSummary());
                Throughput.LogSummary("Check cancelled");
                RunnerState = IService.EServiceState.cancelled;
                return;
            }

            if (seenIds.Contains(kvp.Value.Id))
            {
                continue;
            }

            var dbItem = kvp.Value;
            bool protectedByUnreadable = CheckJobDTO.IgnoreBasePath
                ? unreadableMatcher.IsProtectedByAnyBasePath(dbItem.FilenameAndPath, dbItem.PathBase)
                : unreadableMatcher.IsProtected(dbItem.FilenameAndPath);
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
            _logger.LogTrace("Check: MissingOnDisk {file}", dbItem.FilenameAndPath);
        }

        writer.Flush();
        RunnerState = IService.EServiceState.finished;
        if (UnreadableDirectories.Count > 0)
        {
            _logger.LogWarning("Check: {count} directories could not be read completely. {kept} database rows below them were NOT reported as missing (their files were not checked at all). Directories: {directories}", UnreadableDirectories.Count, UnreadableProtectedCount, UnreadableDirectoryLog.Join(UnreadableDirectories));
        }
        _logger.LogInformation("Check finished. Report={report} {summary}", reportPath, BuildSummary());
        Throughput.LogSummary("Check finished");
    }

    /// <returns>The number of bytes that were hashed for this file (0 when it was not hashed or hashing failed).</returns>
    private long ProcessFile(FileEntryInfo listedEntry, string basePath, Dictionary<string, ScanItemDto> existingByPath, RelativePathIndex? relativeIndex, HashSet<Guid> seenIds, TextWriter writer)
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
        if (existingByPath.TryGetValue(currentFile, out var exact))
        {
            matched = exact;
        }
        else if (CheckJobDTO.IgnoreBasePath)
        {
            matched = relativeIndex?.Find(currentFile, seenIds);
            pathMoved = matched is not null;
        }

        if (matched is null)
        {
            // No DB row at all => new on disk. Compute hash so applyCheck can insert without re-reading.
            FileHashResult? newHashed = CheckJobDTO.SkipHashCheck ? null : ComputeHashOrNull(currentFile);
            if (newHashed is { IsStable: false } newUnstable)
            {
                // No row at all: there is no database row to claim and a half-true hash must not be written
                RecordUnstable(currentFile, newUnstable);
                return 0;
            }
            string? newHash = newHashed?.Hash;
            long newBytesHashed = 0;
            if (newHashed is { IsStable: true } newStable)
            {
                newBytesHashed = newStable.BytesRead;
                if (!CheckJobDTO.ScanJobDTO.TrustDirectoryMetadata)
                {
                    // Size and time of the file itself belong to the hash, so applyCheck stores a consistent row (with TrustDirectoryMetadata the listing values stay)
                    fileSize = newStable.Length;
                    fileLastModificationUTC = newStable.LastWriteTimeUtc;
                }
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
            return newBytesHashed;
        }

        seenIds.Add(matched.Id);

        bool sizeOrMtimeChanged = matched.FileSize != fileSize || matched.FileLastModificationUTC != fileLastModificationUTC;

        if (CheckJobDTO.SkipHashCheck)
        {
            if (!sizeOrMtimeChanged && !pathMoved)
            {
                OkCount++;
                return 0;
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
            return 0;
        }
        if (!hashed.Value.IsStable)
        {
            // The row of the database was claimed above (seenIds), so it is not reported as MissingOnDisk. No report row.
            RecordUnstable(currentFile, hashed.Value);
            return 0;
        }
        string diskHash = hashed.Value.Hash!;
        long bytesHashed = hashed.Value.BytesRead;
        if (!CheckJobDTO.ScanJobDTO.TrustDirectoryMetadata)
        {
            // Compare and report the size and time of the file itself that belong to the hash (with TrustDirectoryMetadata the listing values stay)
            fileSize = hashed.Value.Length;
            fileLastModificationUTC = hashed.Value.LastWriteTimeUtc;
            sizeOrMtimeChanged = matched.FileSize != fileSize || matched.FileLastModificationUTC != fileLastModificationUTC;
        }

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

    private void RecordUnstable(string filePath, FileHashResult hashed)
    {
        ErrorCount++;
        UnstableCount++;
        _logger.LogWarning("Check: {file} changed while it was read (read twice from the start, not stable either time: {description}). No report row was written for it.", filePath, hashed.InstabilityDescription);
    }

    /// <returns>The result of the hasher (stable or not), or null when the file could not be read (logged).</returns>
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

    private static string BuildReportPath(MyDupFinderScanJobDTO scanJob)
    {
        var folder = string.IsNullOrWhiteSpace(scanJob.ReportPath)
            ? Path.GetDirectoryName(scanJob.DatabaseFile) ?? "."
            : scanJob.ReportPath;
        var safeJobName = string.Concat(scanJob.JobName.Split(Path.GetInvalidFileNameChars()));
        var fileName = string.Format(CultureInfo.InvariantCulture, "check-{0}-{1:yyyyMMdd-HHmmss}.csv", safeJobName, DateTime.UtcNow);
        return Path.Combine(folder, fileName);
    }

    private string BuildSummary() =>
        string.Format(CultureInfo.InvariantCulture,
            "BitRot={0} Modified={1} ModifiedNoHashChange={2} Missing={3} New={4} Ok={5} Errors={6} Unstable={9} UnreadableDirectories={7} NotReportedMissingBecauseUnreadable={8}",
            BitRotSuspectCount, ModifiedCount, ModifiedNoHashChangeCount, MissingOnDiskCount, NewOnDiskCount, OkCount, ErrorCount, UnreadableDirectories.Count, UnreadableProtectedCount, UnstableCount);

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
