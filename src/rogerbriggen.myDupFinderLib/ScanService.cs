using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib
{


    public class ScanService : IDisposable, IScanService
    {

        private ServiceState _scanState;
        public ServiceState ScanState
        {
            get => _scanState;
            private set
            {
                if (_scanState != value)
                {
                    _scanState = value;
                    OnScanStateChanged(value);
                }
            }
        }

        private CancellationTokenSource? cts;
        //private ScanRunnerParallel? sr;
        private ScanRunnerSingleThread? sr;


        private readonly ILogger<ScanService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private bool _disposedValue;

        public event EventHandler<int>? ScanProgressChanged;
        public event EventHandler<ServiceState>? ScanStateChanged;

        public ScanService(ILogger<ScanService> logger, IServiceProvider serviceProvider)
        {
            ScanState = ServiceState.idle;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public void StartScan(MyDupFinderScanJobDTO scanJobDTO)
        {
            //TODO check if runner for base path exits... if not, create and add to queue

            if (ScanState == ServiceState.idle)
            {
                ScanState = ServiceState.running;
                cts = new CancellationTokenSource();
                //sr = new ScanRunnerParallel(scanJobDTO, _serviceProvider.GetService<ILogger<ScanRunnerParallel>>());
                sr = new ScanRunnerSingleThread(scanJobDTO, _serviceProvider.GetService<ILogger<ScanRunnerSingleThread>>(), _serviceProvider);
                sr.ScanStateChanged += ScanStateEventHandler;
                sr.Start(cts.Token);
            }
        }

        public void StopScan(string basePath)
        {
            if (ScanState == ServiceState.running)
            {
                cts?.Cancel();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: Verwalteten Zustand (verwaltete Objekte) bereinigen
                    cts?.Dispose();
                    sr?.Dispose();
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

        private void ScanStateEventHandler(object? _, ServiceState scanState)
        {
            _logger.LogInformation("Runner changed to state {scanState}", scanState);
            //if (scanState == ServiceState.finished)
            //{
            //    ScanState = scanState;
            //}
            ScanState = scanState;
        }
    }
}
