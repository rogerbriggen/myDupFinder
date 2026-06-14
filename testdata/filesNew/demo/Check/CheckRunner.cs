// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;


namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Walks the disk and compares every file against the database without modifying it.
/// Each disk file is classified as Ok, BitRotSuspect, Modified, ModifiedNoHashChange, or NewOnDisk.
/// DB rows that have no matching disk file are reported as MissingOnDisk.
/// The result is written as a self-contained CSV that applyCheck can later replay against the DB.
/// </summary>
internal class CheckRunner : BasicRunner<CheckRunner>, ICheckRunner
{
    public CheckRunner(MyDupFinderCheckJobDTO checkJobDTO, ILogger<CheckRunner>? logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
    {
        CheckJobDTO = checkJobDTO;
        DbInserts = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
    }

    private bool _disposed;
    private MyDupFinderCheckJobDTO CheckJobDTO { get; }
    private ScanJobDBInserts DbInserts { get; }

    public int BitRotSuspectCount { get; private set; }
    public int ModifiedCount { get; private set; }
    public int ModifiedNoHashChangeCount { get; private set; }
    public int MissingOnDiskCount { get; private set; }
    public int NewOnDiskCount { get; private set; }
    public int OkCount { get; private set; }
    public int ErrorCount { get; private set; }
    public string? ReportFilePath { get; private set; }

    public override void Start(CancellationToken token)
    {
        base.Start(token);

        DbInserts.SetupDB(CheckJobDTO.ScanJobDTO.DatabaseFile);
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

        var existingItems = DbInserts.GetAllItemsByBasePath(basePath);
        var existingByPath = new Dictionary<string, ScanItemDto>(StringComparer.Ordinal);
        foreach (var item in existingItems)
        {
            existingByPath[item.FilenameAndPath] = item;
        }
        var seenIds = new HashSet<Guid>();

        _logger.LogInformation("Check: Found {count} existing items in database for base path {basePath}", existingItems.Count, basePath);

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

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Error enumerating files in {basePath}", basePath);
            RunnerState = IService.EServiceState.finished;
            return;
        }

        foreach (string currentFile in files)
        {
            if (CancelToken.IsCancellationRequested)
            {
                writer.Flush();
                _logger.LogInformation("Check was cancelled by user... {summary}", BuildSummary());
                RunnerState = IService.EServiceState.cancelled;
                return;
            }

            try
            {
                ProcessFile(currentFile, basePath, existingByPath, seenIds, writer);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Check: Error processing file {file}", currentFile);
                ErrorCount++;
            }

            // Breath...
            Thread.Sleep(10);
        }

        // Anything in the DB under this base path that we did not see on disk is now missing.
        foreach (var kvp in existingByPath)
        {
            if (CancelToken.IsCancellationRequested)
            {
                writer.Flush();
                _logger.LogInformation("Check was cancelled by user during missing-detection... {summary}", BuildSummary());
                RunnerState = IService.EServiceState.cancelled;
                return;
            }

            if (seenIds.Contains(kvp.Value.Id))
            {
                continue;
            }

            var dbItem = kvp.Value;
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
            _logger.LogInformation("Check: MissingOnDisk {file}", dbItem.FilenameAndPath);
        }

        writer.Flush();
        RunnerState = IService.EServiceState.finished;
        _logger.LogInformation("Check finished. Report={report} {summary}", reportPath, BuildSummary());
    }

    private void ProcessFile(string currentFile, string basePath, Dictionary<string, ScanItemDto> existingByPath, HashSet<Guid> seenIds, TextWriter writer)
    {
        var fileInfo = new FileInfo(currentFile);
        long fileSize = fileInfo.Length;
        DateTime fileLastModificationUTC = File.GetLastWriteTimeUtc(currentFile);

        ScanItemDto? matched = null;
        bool pathMoved = false;
        if (existingByPath.TryGetValue(currentFile, out var exact))
        {
            matched = exact;
        }
        else if (CheckJobDTO.IgnoreBasePath)
        {
            matched = FindByRelativeSuffix(currentFile, basePath, seenIds);
            pathMoved = matched is not null;
        }

        if (matched is null)
        {
            // No DB row at all => new on disk. Compute hash so applyCheck can insert without re-reading.
            string? newHash = CheckJobDTO.SkipHashCheck ? null : ComputeHashOrNull(currentFile);
            var row = new CheckReportRow
            {
                Category = CheckCategory.NewOnDisk,
                ScanItemId = Guid.Empty,
                FilenameAndPathDisk = currentFile,
                PathBaseDisk = basePath,
                FileSizeDisk = fileSize,
                MTimeDisk = fileLastModificationUTC,
                HashDisk = newHash,
                CreationTimeDisk = File.GetCreationTimeUtc(currentFile),
            };
            CheckReportCsv.WriteRow(writer, row);
            NewOnDiskCount++;
            _logger.LogInformation("Check: NewOnDisk {file}", currentFile);
            return;
        }

        seenIds.Add(matched.Id);

        bool sizeOrMtimeChanged = matched.FileSize != fileSize || matched.FileLastModificationUTC != fileLastModificationUTC;

        if (CheckJobDTO.SkipHashCheck)
        {
            if (!sizeOrMtimeChanged && !pathMoved)
            {
                OkCount++;
                return;
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
            return;
        }

        string? diskHash = ComputeHashOrNull(currentFile);
        if (diskHash is null)
        {
            ErrorCount++;
            return;
        }

        bool hashDiffers = !string.Equals(diskHash, matched.FileSha512Hash, StringComparison.OrdinalIgnoreCase);

        CheckCategory? category = null;
        if (sizeOrMtimeChanged && hashDiffers)
        {
            category = CheckCategory.Modified;
            ModifiedCount++;
            _logger.LogInformation("Check: Modified {file}", currentFile);
        }
        else if (sizeOrMtimeChanged && !hashDiffers)
        {
            category = CheckCategory.ModifiedNoHashChange;
            ModifiedNoHashChangeCount++;
            _logger.LogInformation("Check: ModifiedNoHashChange {file}", currentFile);
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
            _logger.LogInformation("Check: PathMoved (no content change) {file}", currentFile);
        }
        else
        {
            OkCount++;
            return;
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
    }

    private ScanItemDto? FindByRelativeSuffix(string currentFile, string basePath, HashSet<Guid> seenIds)
    {
        if (!currentFile.StartsWith(basePath, StringComparison.Ordinal))
        {
            return null;
        }
        var relative = currentFile.Substring(basePath.Length);
        // Use a leading separator so we don't accidentally match "anothersub\file" against "sub\file".
        var suffix = Path.DirectorySeparatorChar + relative;
        var candidates = DbInserts.GetItemsByRelativeSuffix(suffix);
        if (candidates.Count == 0)
        {
            return null;
        }
        foreach (var candidate in candidates)
        {
            // Skip rows already claimed by an exact match in this run.
            if (seenIds.Contains(candidate.Id))
            {
                continue;
            }
            // Skip rows that already live under our current BasePath - those would have been caught by the exact-path branch.
            if (string.Equals(candidate.PathBase, basePath, StringComparison.Ordinal))
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    private string? ComputeHashOrNull(string filePath)
    {
        try
        {
            using var sha512 = SHA512.Create();
            using var stream = File.OpenRead(filePath);
            return BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", "", StringComparison.Ordinal);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Check: Failed to hash {file}", filePath);
            return null;
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
            "BitRot={0} Modified={1} ModifiedNoHashChange={2} Missing={3} New={4} Ok={5} Errors={6}",
            BitRotSuspectCount, ModifiedCount, ModifiedNoHashChangeCount, MissingOnDiskCount, NewOnDiskCount, OkCount, ErrorCount);

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
