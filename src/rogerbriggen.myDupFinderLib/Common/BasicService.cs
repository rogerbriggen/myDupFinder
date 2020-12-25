using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLib
{


    public abstract class BasicService<T> : IDisposable, IService
    {

        private IService.EServiceState _serviceState;
        public IService.EServiceState ServiceState
        {
            get => _serviceState;
            private set
            {
                if (_serviceState != value)
                {
                    _serviceState = value;
                    OnServiceStateChanged(value);
                }
            }
        }

        protected CancellationTokenSource? _cts;
        protected IRunner? _runner;

        protected readonly ILogger<T> _logger;
        protected readonly IServiceProvider _serviceProvider;
        private bool _disposedValue;

        public event EventHandler<int>? ServiceProgressChanged;
        public event EventHandler<IService.EServiceState>? ServiceStateChanged;

        public BasicService(ILogger<T> logger, IServiceProvider serviceProvider)
        {
            ServiceState = IService.EServiceState.idle;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public virtual void Start(IRunner runner)
        {
            _runner = runner;
            if (ServiceState == IService.EServiceState.idle)
            {
                ServiceState = IService.EServiceState.running;
                _cts = new CancellationTokenSource();
                _runner.RunnerStateChanged += RunnerStateEventHandler;
                _runner.Start(_cts.Token);
            }
        }

        public virtual void Stop()
        {
            if (ServiceState == IService.EServiceState.running)
            {
                _cts?.Cancel();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: Verwalteten Zustand (verwaltete Objekte) bereinigen
                    _cts?.Dispose();
                    _runner?.Dispose();
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

        protected virtual void OnServiceProgressChanged(int progress)
        {
            EventHandler<int>? handler = ServiceProgressChanged;
            handler?.Invoke(this, progress);
        }
        protected virtual void OnServiceStateChanged(IService.EServiceState state)
        {
            EventHandler<IService.EServiceState>? handler = ServiceStateChanged;
            handler?.Invoke(this, state);
        }

        private void RunnerStateEventHandler(object? _, IService.EServiceState runnerState)
        {
            _logger.LogInformation("Runner changed to state {runnerState}", runnerState);
            ServiceState = runnerState;
        }
    }
}
