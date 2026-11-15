// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The batching wrapper of the scan around the shared file enumeration (<see cref="FileTreeEnumeration"/>):
/// turns the next files into <see cref="ScanItemDto"/> objects in <see cref="ScanItemCollection"/>.
/// The items carry size and timestamps from the directory listing; unless the job sets TrustDirectoryMetadata, the runner replaces them with the values of
/// the file itself (<see cref="TryRefreshMetadata"/>, one call per file) when the item is processed, because the directory entry can be stale.
/// Directories that cannot be read are logged as a warning when they are found, the enumeration goes on.
/// </summary>
public class EnumerateFilesFromDiscToIndex : IEnumerateFilesToIndex, IDisposable
{
    public int CurrentCount { get; private set; }

    public int ErrorCount { get; private set; }

    public bool HasMore { get; private set; }

    public ConcurrentQueue<ScanItemDto> ScanItemCollection { get; private set; }

    public IReadOnlyList<UnreadableDirectory> UnreadableDirectories => _files.UnreadableDirectories;

    private readonly ILogger<EnumerateFilesFromDiscToIndex> _logger;

    private MyDupFinderScanJobDTO _scanJobDto;

    private readonly IFileEnumeration _files;

    private int _unreadableLogged;

    public EnumerateFilesFromDiscToIndex(MyDupFinderScanJobDTO scanJobDto, ILogger<EnumerateFilesFromDiscToIndex>? logger) : this(scanJobDto, logger, null)
    {
    }

    /// <param name="fileSource">Only for tests: where the files come from. Default: the real file system.</param>
    internal EnumerateFilesFromDiscToIndex(MyDupFinderScanJobDTO scanJobDto, ILogger<EnumerateFilesFromDiscToIndex>? logger, IFileEnumerationSource? fileSource)
    {
        _scanJobDto = scanJobDto;
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<EnumerateFilesFromDiscToIndex>();
        ScanItemCollection = new ConcurrentQueue<ScanItemDto>();
        _files = (fileSource ?? FileTreeEnumerationSource.Instance).Open(_scanJobDto.BasePath);
        HasMore = true;
    }

    public void EnumerateFiles(int maxFiles = 100)
    {
        if (!HasMore)
        {
            //This way we can dispose the enumerator once we are through
            return;
        }
        int loopCount = 0;
        while (MoveNext())
        {
            FileEntryInfo file = _files.Current;
            try
            {
                DateTime currDate = DateTime.UtcNow;
                ScanItemDto si = new ScanItemDto
                {
                    PathBase = _scanJobDto.BasePath,
                    FilenameAndPath = file.FullPath,

                    FirstScanDateUTC = currDate,
                    LastScanDateUTC = currDate,
                    LastSha512ScanDateUTC = currDate,
                    OriginComputer = _scanJobDto.OriginComputer,
                    ScanName = _scanJobDto.ScanName,
                    ScanExecutionComputer = Environment.MachineName,
                    FileCreationUTC = file.CreationTimeUtc,
                    FileLastModificationUTC = file.LastWriteTimeUtc,
                    FileSize = file.Length
                };
                ScanItemCollection.Enqueue(si);
                loopCount += 1;
                CurrentCount += 1;
                //Check if we are done
                if (loopCount >= maxFiles)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace($"EnumerateFiles: CurrentCount: {CurrentCount} File: {file.FullPath}");
                    }
                    //make a break
                    return;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "There was an exception during file attribute reading of file {file}", file.FullPath);
                ErrorCount += 1;
            }

        }
        //We are done
        HasMore = false;
        _files.Dispose();
    }

    /// <summary>
    /// Next file of the enumeration. A directory that could not be read is found while the enumeration looks for the next file, so the warning is written right after.
    /// </summary>
    private bool MoveNext()
    {
        bool hasNext = _files.MoveNext();
        UnreadableDirectoryLog.LogNew(_logger, _files.UnreadableDirectories, ref _unreadableLogged);
        return hasNext;
    }

    /// <inheritdoc />
    public bool TryRefreshMetadata(ScanItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_scanJobDto.TrustDirectoryMetadata)
        {
            return true;
        }
        try
        {
            FileEntryInfo metadata = FileMetadata.Read(item.FilenameAndPath);
            item.FileSize = metadata.Length;
            item.FileCreationUTC = metadata.CreationTimeUtc;
            item.FileLastModificationUTC = metadata.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex)
        {
            // Same handling as before step 4, when the attribute reading failed in the enumeration: error log, error count, the file is not scanned
            _logger.LogError(ex, "There was an exception during file attribute reading of file {file}", item.FilenameAndPath);
            ErrorCount += 1;
            CurrentCount -= 1;
            return false;
        }
    }

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}
