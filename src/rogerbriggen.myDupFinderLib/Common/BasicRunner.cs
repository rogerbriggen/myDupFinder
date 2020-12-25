// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RogerBriggen.MyDupFinderLib
{
    /// <summary>
    /// 
    /// </summary>
    internal class BasicRunner<T> : IDisposable, IRunner
    {

        public IService.ServiceState RunnerState
        {
            get => _scanState;
            set
            {
                if (_scanState != value)
                {
                    _scanState = value;
                    OnRunnerStateChanged(value);
                }


            }
        }

        public BasicRunner(ILogger<T>? logger, IServiceProvider serviceProvider)
        {
            RunnerState = IService.ServiceState.idle;
            _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<T>();
            _serviceProvider = serviceProvider;
        }

        public event EventHandler<int>? RunnerProgressChanged;
        public event EventHandler<IService.ServiceState>? RunnerStateChanged;


        protected readonly ILogger<T> _logger;
        protected readonly IServiceProvider _serviceProvider;

        private IService.ServiceState _scanState;

        protected CancellationToken CancelToken { get; set; }

        private bool _disposedValue;

        protected DateTime _runStarted;

        

        public virtual void Start(CancellationToken token)
        {
            if (RunnerState != IService.ServiceState.idle)
            {
                throw new InvalidOperationException("Runner is not in state idle!");
            }
            CancelToken = token;
            RunnerState = IService.ServiceState.running;
            _runStarted = DateTime.UtcNow;
        }


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: Verwalteten Zustand (verwaltete Objekte) bereinigen
                    
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

        protected virtual void OnRunnerProgressChanged(int progress)
        {
            EventHandler<int>? handler = RunnerProgressChanged;
            handler?.Invoke(this, progress);
        }
        protected virtual void OnRunnerStateChanged(IService.ServiceState state)
        {
            EventHandler<IService.ServiceState>? handler = RunnerStateChanged;
            handler?.Invoke(this, state);
        }
    }
}
