// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Refreshes an existing database by:
/// - Adding new files that are not yet in the database
/// - Updating existing files whose size or modification date has changed (recalculates hash)
/// - Removing files from the database that no longer exist on disk
/// - Skipping hash recalculation for files where size and date are unchanged
/// </summary>
internal class RefreshRunner : BasicRunner<RefreshRunner>, IRefreshRunner
{
    public RefreshRunner(MyDupFinderRefreshJobDTO refreshJobDTO, ILogger<RefreshRunner>? logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
    {
        RefreshJobDTO = refreshJobDTO;
        ScanJobDBInserts = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
    }

    // To detect redundant calls
    private bool _disposed = false;
    private MyDupFinderRefreshJobDTO RefreshJobDTO { get; set; }

    private ScanJobDBInserts ScanJobDBInserts { get; set; }

    public int AddedCount { get; private set; }
    public int UpdatedCount { get; private set; }
    public int RemovedCount { get; private set; }
    public int UnchangedCount { get; private set; }
    public int ErrorCount { get; private set; }

    public override void Start(CancellationToken token)
    {
        base.Start(token);

        //Setup DB
        ScanJobDBInserts.SetupDB(RefreshJobDTO.DatabaseFile);
        Refresh();
        ScanJobDBInserts.Dispose();
    }

    private void Refresh()
    {
        try
        {
            // Step 1: Get all existing items from the database for this base path
            var existingItems = ScanJobDBInserts.GetAllItemsByBasePath(RefreshJobDTO.BasePath);
            var existingByPath = new Dictionary<string, ScanItemDto>(StringComparer.Ordinal);
            foreach (var item in existingItems)
            {
                existingByPath[item.FilenameAndPath] = item;
            }

            _logger.LogInformation("Refresh: Found {count} existing items in database for base path {basePath}", existingItems.Count, RefreshJobDTO.BasePath);

            // Step 2: Enumerate files on disk and process them
            var filesOnDisk = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(RefreshJobDTO.BasePath, "*", SearchOption.AllDirectories);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                _logger.LogError(ex, "Error enumerating files in {basePath}", RefreshJobDTO.BasePath);
                RunnerState = IService.EServiceState.finished;
                return;
            }

            foreach (string currentFile in files)
            {
                //CancelToken?
                if (CancelToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Refresh was cancelled by user... Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
                    RunnerState = IService.EServiceState.cancelled;
                    return;
                }

                filesOnDisk.Add(currentFile);

                try
                {
                    var fileInfo = new FileInfo(currentFile);
                    long fileSize = fileInfo.Length;
                    DateTime fileLastModificationUTC = File.GetLastWriteTimeUtc(currentFile);

                    if (existingByPath.TryGetValue(currentFile, out var existingItem))
                    {
                        // File exists in DB - check if it changed
                        if (existingItem.FileSize == fileSize && existingItem.FileLastModificationUTC == fileLastModificationUTC)
                        {
                            // Size and date unchanged - skip hash recalculation, just touch
                            ScanJobDBInserts.TouchItem(existingItem, DateTime.UtcNow);
                            UnchangedCount++;
                        }
                        else
                        {
                            // Size or date changed - recalculate hash
                            string newHash = CalcHash(currentFile);
                            ScanJobDBInserts.UpdateItem(existingItem, fileSize, fileLastModificationUTC, newHash, DateTime.UtcNow);
                            UpdatedCount++;
                            _logger.LogInformation("Refresh: Updated {file} (size: {oldSize}->{newSize}, date changed)", currentFile, existingItem.FileSize, fileSize);
                        }
                    }
                    else
                    {
                        // New file - add to DB
                        DateTime currDate = DateTime.UtcNow;
                        var si = new ScanItemDto
                        {
                            PathBase = RefreshJobDTO.BasePath,
                            FilenameAndPath = currentFile,
                            FirstScanDateUTC = currDate,
                            LastScanDateUTC = currDate,
                            LastSha512ScanDateUTC = currDate,
                            OriginComputer = RefreshJobDTO.OriginComputer,
                            ScanName = RefreshJobDTO.ScanName,
                            ScanExecutionComputer = Environment.MachineName,
                            FileCreationUTC = File.GetCreationTimeUtc(currentFile),
                            FileLastModificationUTC = fileLastModificationUTC,
                            FileSize = fileSize
                        };

                        string hash = CalcHash(currentFile);
                        si.FileSha512Hash = hash;
                        ScanJobDBInserts.Enqueue(si);
                        AddedCount++;
                        _logger.LogInformation("Refresh: Added new file {file}", currentFile);
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    _logger.LogError(ex, "Refresh: Error processing file {file}", currentFile);
                    ErrorCount++;
                }

                //Breath...
                System.Threading.Thread.Sleep(10);
            }

            // Step 3: Remove items from DB that no longer exist on disk
            foreach (var kvp in existingByPath)
            {
                if (CancelToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Refresh was cancelled by user during removal phase... Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
                    RunnerState = IService.EServiceState.cancelled;
                    return;
                }

                if (!filesOnDisk.Contains(kvp.Key))
                {
                    try
                    {
                        ScanJobDBInserts.RemoveItem(kvp.Value);
                        RemovedCount++;
                        _logger.LogInformation("Refresh: Removed deleted file {file}", kvp.Key);
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                    {
                        _logger.LogError(ex, "Refresh: Error removing file {file} from database", kvp.Key);
                        ErrorCount++;
                    }
                }
            }

            ScanJobDBInserts.WriteChanges();
            RunnerState = IService.EServiceState.finished;
            _logger.LogInformation("Refresh finished. Added={added} Updated={updated} Removed={removed} Unchanged={unchanged} Errors={errors}", AddedCount, UpdatedCount, RemovedCount, UnchangedCount, ErrorCount);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Error in Refresh!");
        }
    }

    private string CalcHash(string filePath)
    {
        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(filePath);
        return BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", "", StringComparison.Ordinal);
    }

    // Protected implementation of Dispose pattern.
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // Dispose managed state (managed objects).
        }

        // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
        // TODO: set large fields to null.
        _disposed = true;

        // Call base class implementation.
        base.Dispose(disposing);
    }
}
