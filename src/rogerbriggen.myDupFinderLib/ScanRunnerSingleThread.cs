// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib
{
    /// <summary>
    /// This will scan the files with a single thread and also throttle the hdd disk access a bit...
    /// </summary>
    internal class ScanRunnerSingleThread : IDisposable, IScanRunner
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

        public ScanRunnerSingleThread(MyDupFinderScanJobDTO scanJobDTO, ILogger<ScanRunnerSingleThread>? logger, IServiceProvider serviceProvider)
        {
            ScanState = ServiceState.idle;
            ScanJobDTO = scanJobDTO;
            _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<ScanRunnerSingleThread>();
            _serviceProvider = serviceProvider;
            ScanJobDBInserts = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        }

        public event EventHandler<int>? ScanProgressChanged;
        public event EventHandler<ServiceState>? ScanStateChanged;

        private MyDupFinderScanJobDTO ScanJobDTO { get; set; }

        private readonly ILogger<ScanRunnerSingleThread> _logger;
        private readonly IServiceProvider _serviceProvider;

        private ServiceState _scanState;

        private ScanJobDBInserts ScanJobDBInserts { get; set; }

        private CancellationToken CancelToken { get; set; }

        private BlockingCollection<ScanItemDto> _scanItemCollection = new BlockingCollection<ScanItemDto>();

        private bool _disposedValue;

        private DateTime _runStarted;

        private IEnumerateFilesToIndex? _enumerateFiles;

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
            _runStarted = DateTime.UtcNow;
            _enumerateFiles = new EnumerateFilesFromDiscToIndex(ScanJobDTO, _serviceProvider.GetService<ILogger<EnumerateFilesFromDiscToIndex>>());
            Scan();
            ScanJobDBInserts.Dispose();
        }

        private void Scan()
        {
            DiscThrottle discThrottle = new DiscThrottle(10, TimeSpan.FromMinutes(5).TotalMilliseconds);
            bool bFinish = false;
            //Don't filter if db is empty...
            bool bFilter = !ScanJobDBInserts.IsEmptyScanItemTable();
            if (bFilter)
            {
                //Check if there is at least one item from this base path... if no, we don't filter
                bFilter = ScanJobDBInserts.IsBasePathAlreadyInDB(ScanJobDTO.BasePath);
            }
            try
            {
                while (!bFinish)
                {
                    //CancelToken?
                    if (CancelToken.IsCancellationRequested)
                    {
                        ScanJobDBInserts.WriteChanges();
                        _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
                        ScanState = ServiceState.paused;
                        return;
                    }

                    //Throttle
                    discThrottle.Throttle(CancelToken);
                    
                    //Enumerate
                    if (!(_enumerateFiles?.HasMore == true))
                    {
                        bFinish = true;
                        continue;
                    }
                    _enumerateFiles.EnumerateFiles();
                    
                    

                    //CalcHash
                    while(true)
                    {
                        //Get item
                        ScanItemDto? si;
                        if (!_enumerateFiles.ScanItemCollection.TryDequeue(out si))
                        {
                            break;
                        }

                        //Filter
                        if (bFilter)
                        {
                            if (ScanJobDBInserts.IsAlreadyInDB(si))
                            {
                                continue;
                            }
                        }

                        //Calc hash
                        CalcHash(si);
                        
                        //CancelToken?
                        if (CancelToken.IsCancellationRequested)
                        {
                            ScanJobDBInserts.WriteChanges();
                            _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
                            ScanState = ServiceState.paused;
                            return;
                        }

                        //Breath...
                        System.Threading.Thread.Sleep(10);

                        //Throttle
                        discThrottle.Throttle(CancelToken);
                    }
                    _logger.LogDebug($"Scanning... FileEnumeration currentCount={_enumerateFiles.CurrentCount}  errorCount={_enumerateFiles.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
                }
                ScanJobDBInserts.WriteChanges();
                ScanState = ServiceState.finished;
                _logger.LogInformation($"Scanning finished... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Scan!");
            }
        }


        private void CalcHash(ScanItemDto item)
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
