// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLib;


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
            bool runnerStarted = false;
            try
            {
                SetServiceState(IService.EServiceState.running);
                _cts = new CancellationTokenSource();
                _runner.RunnerStateChanged += RunnerStateEventHandler;
                runnerStarted = true;
                _runner.Start(_cts.Token);
            }
#pragma warning disable CA1031 // Keine allgemeinen Ausnahmetypen abfangen
            catch (Exception ex)
#pragma warning restore CA1031 // Keine allgemeinen Ausnahmetypen abfangen
            {
                // Safety net: a runner should end in finished, cancelled or failed by itself, but nothing may escape from here
                // (the job runs on a worker thread, an unhandled exception there ends the process)
                _logger.LogError(ex, "The runner ended with an unexpected exception.");
                if (!runnerStarted)
                {
                    // The runner never ran: the job did not happen
                    SetServiceState(IService.EServiceState.failed);
                    return;
                }
            }
            FollowFinalRunnerState();
        }
    }

    /// <summary>
    /// Sets the service state. The backing state is set before the event is raised, so a subscriber that throws (it is logged, never rethrown) cannot keep the state from changing.
    /// </summary>
    private void SetServiceState(IService.EServiceState state)
    {
        try
        {
            ServiceState = state;
        }
#pragma warning disable CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        catch (Exception ex)
#pragma warning restore CA1031 // Keine allgemeinen Ausnahmetypen abfangen
        {
            _logger.LogError(ex, "A subscriber of ServiceStateChanged threw while the state changed to {state}.", state);
        }
    }

    /// <summary>
    /// After the runner has returned the service state is the final state of the runner. A runner that returns in any other state (running, idle) did not end properly: failed.
    /// </summary>
    private void FollowFinalRunnerState()
    {
        IService.EServiceState runnerState = _runner!.RunnerState;
        if (runnerState is IService.EServiceState.finished or IService.EServiceState.cancelled or IService.EServiceState.failed)
        {
            SetServiceState(runnerState);
        }
        else
        {
            _logger.LogError("The runner returned in state {runnerState}, which is not a final state. The service state is set to failed.", runnerState);
            SetServiceState(IService.EServiceState.failed);
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
        // Called from inside the runner: an exception here must not disturb the run
        SetServiceState(runnerState);
    }
}
