// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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

        public ScanRunner(string basePath, string originComputer, ILogger<ScanRunner> logger)
        {
            ScanState = ServiceState.idle;
            BasePath = basePath;
            OriginComputer = originComputer;
            _logger = logger;
        }

        public event EventHandler<int>? ScanProgressChanged;
        public event EventHandler<ServiceState>? ScanStateChanged;

        private string BasePath { get; set; }

        private string OriginComputer { get; set; }

        private readonly ILogger<ScanRunner> _logger;

        private ServiceState _scanState;

        private CancellationToken CancelToken { get; set; }

        private BlockingCollection<ScanItem> _scanItemCollection = new BlockingCollection<ScanItem>();
        private ConcurrentQueue<ScanItem> _finishedScanItemCollection = new ConcurrentQueue<ScanItem>();
        private ConcurrentQueue<ScanItem> _failedScanItemCollection = new ConcurrentQueue<ScanItem>();
        private bool _disposedValue;

        public void Start(CancellationToken token)
        {
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
                                                  var loopResult = Parallel.ForEach<ScanItem>(_scanItemCollection.GetConsumingEnumerable(), po, (item, loopState, _) =>
                                                  {
                                                      try
                                                      {
                                                          using (var sha512 = SHA512.Create())
                                                          {
                                                              using (var stream = File.OpenRead(item.FilenameAndPath))
                                                              {
                                                                  item.FileSha512Hash = BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", "", StringComparison.Ordinal);
                                                                  _finishedScanItemCollection.Enqueue(item);
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
                                                  _logger.LogInformation("Finished hashing files. Successfully hashed files: {successfullCount}, failed: {failedCount}, Queue: {QueueCount}", _finishedScanItemCollection.Count, _failedScanItemCollection.Count, _scanItemCollection.Count);
                                                  ScanState = ServiceState.finished;
                                              });
                var files = Directory.EnumerateFiles(BasePath, "*", SearchOption.AllDirectories);
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
                        ScanItem si = new ScanItem();
                        si.PathBase = BasePath;
                        si.FilenameAndPath = currentFile;
                        si.FirstScanDateUTC = DateTime.UtcNow;
                        si.OriginComputer = OriginComputer;
                        si.ScanExecutionComputer = Environment.MachineName;
                        si.FileCreationUTC = File.GetCreationTimeUtc(currentFile);
                        si.FileLastModificationUTC = File.GetLastWriteTimeUtc(currentFile);
                        si.FileSize = new FileInfo(currentFile).Length;
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
