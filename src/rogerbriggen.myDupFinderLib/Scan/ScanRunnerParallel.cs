// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib
{
    /// <summary>
    /// This will scan the files in parallel... this will almost deadlock your pc and will use your hdd to 100%...
    /// </summary>
    internal class ScanRunnerParallel : BasicRunner<ScanRunnerParallel>, IScanRunner
    {

        public ScanRunnerParallel(MyDupFinderScanJobDTO scanJobDTO, ILogger<ScanRunnerParallel>? logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
        {
            ScanJobDTO = scanJobDTO;
            ScanJobDBInserts = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        }


        private MyDupFinderScanJobDTO ScanJobDTO { get; set; }

        private ScanJobDBInserts ScanJobDBInserts { get; set; }

        private BlockingCollection<ScanItemDto> _scanItemCollection = new BlockingCollection<ScanItemDto>();

        // To detect redundant calls
        private bool _disposed = false;


        public override void Start(CancellationToken token)
        {
            base.Start(token);
            //Setup DB
            ScanJobDBInserts.SetupDB(ScanJobDTO.DatabaseFile);       
            Scan();
            Console.ReadKey();
            ScanJobDBInserts.Dispose();
        }

        private void Scan()
        {
            try
            {
                Task taskHash = Task.Run(() =>
                                              {
                                                  ParallelOptions po = new ParallelOptions();
                                                  po.CancellationToken = CancelToken;
                                                  var loopResult = Parallel.ForEach<ScanItemDto>(_scanItemCollection.GetConsumingEnumerable(), po, (item, loopState, _) =>
                                                  {
                                                      try
                                                      {
                                                          using (var sha512 = SHA512.Create())
                                                          {
                                                              using (var stream = File.OpenRead(item.FilenameAndPath))
                                                              {
                                                                  item.FileSha512Hash = BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", "", StringComparison.Ordinal);
                                                                  ScanJobDBInserts.Enqueue(item);
                                                                  _logger.LogInformation("File {file} successfull finished", item.FilenameAndPath);
                                                              }
                                                          }
                                                      }
#pragma warning disable CA1031 // Keine allgemeinen Ausnahmetypen abfangen
                                                      catch (Exception e)
#pragma warning restore CA1031 // Keine allgemeinen Ausnahmetypen abfangen
                                                      {
                                                          ScanJobDBInserts.Enqueue(new ScanErrorItemDto(item, e, _runStarted));
                                                          _logger.LogError(e, "Hashing of {file} failed.", item.FilenameAndPath);
                                                      }

                                                  });
                                                  _logger.LogInformation("Finished hashing files. Successfully hashed files: {successfullCount}, failed: {failedCount}, Queue: {QueueCount}", ScanJobDBInserts.TotalSuccessCount, ScanJobDBInserts.TotalErrorCount, _scanItemCollection.Count);
                                                  RunnerState = IService.EServiceState.finished;
                                              });
                var files = Directory.EnumerateFiles(ScanJobDTO.BasePath, "*", SearchOption.AllDirectories);
                foreach (string currentFile in files)
                {
                    if (CancelToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Current scan is canceled by user request");
                        _scanItemCollection.CompleteAdding();
                        return;
                    }
                    try
                    {
                        DateTime currDate = DateTime.UtcNow;
                        ScanItemDto si = new ScanItemDto
                        {
                            PathBase = ScanJobDTO.BasePath,
                            FilenameAndPath = currentFile,
                            
                            FirstScanDateUTC = currDate,
                            LastScanDateUTC = currDate,
                            LastSha512ScanDateUTC = currDate,
                            OriginComputer = ScanJobDTO.OriginComputer,
                            ScanName = ScanJobDTO.ScanName,
                            ScanExecutionComputer = Environment.MachineName,
                            FileCreationUTC = File.GetCreationTimeUtc(currentFile),
                            FileLastModificationUTC = File.GetLastWriteTimeUtc(currentFile),
                            FileSize = new FileInfo(currentFile).Length
                        };
                        _scanItemCollection.TryAdd(si, -1, CancelToken);

                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Current scan is canceled by user request");
                        _scanItemCollection.CompleteAdding();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "There was an exception during file attribute reading of file {file}", currentFile);
                    }

                }
                _scanItemCollection.CompleteAdding();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "There was an exception during file enumeration");
            }
            _scanItemCollection.CompleteAdding();
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
                _scanItemCollection.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
            // TODO: set large fields to null.
            _disposed = true;

            // Call base class implementation.
            base.Dispose(disposing);
        }


    }
}
