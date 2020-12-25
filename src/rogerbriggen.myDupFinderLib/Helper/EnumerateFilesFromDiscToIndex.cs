// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib
{
    public class EnumerateFilesFromDiscToIndex : IEnumerateFilesToIndex
    {
        public int CurrentCount { get; private set; }

        public int ErrorCount { get; private set; }

        public bool HasMore { get; private set; }

        public ConcurrentQueue<ScanItemDto> ScanItemCollection { get; private set; }

        private readonly ILogger<EnumerateFilesFromDiscToIndex> _logger;

        private MyDupFinderScanJobDTO _scanJobDto;

        private IEnumerator<string> _files;


        public EnumerateFilesFromDiscToIndex(MyDupFinderScanJobDTO scanJobDto, ILogger<EnumerateFilesFromDiscToIndex>? logger)
        {
            _scanJobDto = scanJobDto;
            _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<EnumerateFilesFromDiscToIndex>();
            ScanItemCollection = new ConcurrentQueue<ScanItemDto>();
            _files = Directory.EnumerateFiles(_scanJobDto.BasePath, "*", SearchOption.AllDirectories).GetEnumerator();
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
            while(_files.MoveNext())
            {
                try
                {
                    DateTime currDate = DateTime.UtcNow;
                    ScanItemDto si = new ScanItemDto
                    {
                        PathBase = _scanJobDto.BasePath,
                        FilenameAndPath = _files.Current,

                        FirstScanDateUTC = currDate,
                        LastScanDateUTC = currDate,
                        LastSha512ScanDateUTC = currDate,
                        OriginComputer = _scanJobDto.OriginComputer,
                        ScanName = _scanJobDto.ScanName,
                        ScanExecutionComputer = Environment.MachineName,
                        FileCreationUTC = File.GetCreationTimeUtc(_files.Current),
                        FileLastModificationUTC = File.GetLastWriteTimeUtc(_files.Current),
                        FileSize = new FileInfo(_files.Current).Length
                    };
                    ScanItemCollection.Enqueue(si);
                    loopCount += 1;
                    CurrentCount += 1;
                    //Check if we are done
                    if (loopCount >= maxFiles)
                    {
                        _logger.LogDebug($"EnumerateFiles: CurrentCount: {CurrentCount} File: {_files.Current}");
                        //make a break
                        return;
                    }

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "There was an exception during file attribute reading of file {file}", _files.Current);
                    ErrorCount += 1;
                }

            }
            //We are done
            HasMore = false;
            _files.Dispose();
        }
    }
}
