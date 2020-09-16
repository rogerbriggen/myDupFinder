using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLib
{
    public delegate void ScanProgressEventHandler(object sender, int itemCount);

    public delegate void ScanStateEventHandler(object sender, ScanService.State scanState);

    public class ScanService : IDisposable
    {
        public enum State
        {
            idle,
            running,
            paused,
            pausing,
            finished
        }


        public event ScanProgressEventHandler ScanProgressChanged;
        public event ScanStateEventHandler ScanStateChanged;

        private State ScanState { get; set; }

        private CancellationTokenSource cts;
        private ScanRunner sr;


        private readonly ILogger<ScanService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private bool _disposedValue;

        public ScanService(ILogger<ScanService> logger, IServiceProvider serviceProvider)
        {
            ScanState = State.idle;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public void StartScan(string basePath, string originComputer, string dbPathAndFilename)
        {
            //TODO check if runner for base path exits... if not, create and add to queue

            if (ScanState == State.idle)
            {
                cts = new CancellationTokenSource();
                sr = new ScanRunner(basePath, originComputer, _serviceProvider.GetService<ILogger<ScanRunner>>());
                sr.Start(cts.Token);
            }
        }

        public void StopScan(string basePath)
        {
            if (ScanState == State.running)
            {
                cts.Cancel();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: Verwalteten Zustand (verwaltete Objekte) bereinigen
                    cts.Dispose();
                    sr.Dispose();
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
    }
}
