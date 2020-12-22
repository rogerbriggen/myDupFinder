// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib
{

    internal class ScanRunner : IDisposable, IScanRunner
    {

        public ServiceState ScanState
        {
            get => _scanState;
            set
            {
                if (_scanState != value)
                {
                    _scanState = value;
                    OnScanStateChanged(value);
                }


            }
        }

        public ScanRunner(MyDupFinderScanJobDTO scanJobDTO, ILogger<ScanRunner>? logger)
        {
            ScanState = ServiceState.idle;
            ScanJobDTO = scanJobDTO;
            _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<ScanRunner>();
            ScanJobDBInserts = new ScanJobDBInserts();
        }

        public event EventHandler<int>? ScanProgressChanged;
        public event EventHandler<ServiceState>? ScanStateChanged;

        private MyDupFinderScanJobDTO ScanJobDTO { get; set; }

        private readonly ILogger<ScanRunner> _logger;

        private ServiceState _scanState;

        private ScanJobDBInserts ScanJobDBInserts { get; set; }

        private CancellationToken CancelToken { get; set; }

        private BlockingCollection<ScanItemDto> _scanItemCollection = new BlockingCollection<ScanItemDto>();
        //private ConcurrentQueue<ScanItem> _finishedScanItemCollection = new ConcurrentQueue<ScanItem>();

        private ConcurrentQueue<ScanItemDto> _failedScanItemCollection = new ConcurrentQueue<ScanItemDto>();
        private bool _disposedValue;

        public void Start(CancellationToken token)
        {
            if (ScanState != ServiceState.idle)
            {
                throw new InvalidOperationException("ScanRunner is not in state idle!");
            }
            //Setup DB
            ScanJobDBInserts.SetupDB(ScanJobDTO.DatabaseFile);
            CancelToken = token;
            ScanState = ServiceState.running;
            Scan();
            Console.ReadKey();
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
                                                          _failedScanItemCollection.Enqueue(item);
                                                          _logger.LogError(e, "Hashing of {file} failed.", item.FilenameAndPath);
                                                      }

                                                  });
                                                  _logger.LogInformation("Finished hashing files. Successfully hashed files: {successfullCount}, failed: {failedCount}, Queue: {QueueCount}", ScanJobDBInserts.TotalCount, _failedScanItemCollection.Count, _scanItemCollection.Count);
                                                  ScanState = ServiceState.finished;
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
                        ScanItemDto si = new ScanItemDto
                        {
                            PathBase = ScanJobDTO.BasePath,
                            FilenameAndPath = currentFile,
                            FirstScanDateUTC = DateTime.UtcNow,
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

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: Verwalteten Zustand (verwaltete Objekte) bereinigen
                    _scanItemCollection.Dispose();
                }

                // TODO: Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
                // TODO: Große Felder auf NULL setzen
                _disposedValue = true;
            }
        }

        // // TODO: Finalizer nur überschreiben, wenn "Dispose(bool disposing)" Code für die Freigabe nicht verwalteter Ressourcen enthält
        // ~ScanRunner()
        // {
        //     // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void OnScanProgressChanged(int progress)
        {
            EventHandler<int>? handler = ScanProgressChanged;
            handler?.Invoke(this, progress);
        }
        protected virtual void OnScanStateChanged(ServiceState state)
        {
            EventHandler<ServiceState>? handler = ScanStateChanged;
            handler?.Invoke(this, state);
        }
    }
}
