// Roger Briggen license this file to you under the MIT license.
//

using System;

namespace RogerBriggen.MyDupFinderLib;

public interface IService
{
    public enum EServiceState
    {
        idle,
        running,
        cancelled,
        finished,

        /// <summary>The run ended because of an error (logged). A run that is over is in exactly one of finished, cancelled and failed.</summary>
        failed
    }

    event EventHandler<int>? ServiceProgressChanged;
    event EventHandler<EServiceState>? ServiceStateChanged;

    IService.EServiceState ServiceState { get; }

    public abstract void Start(IRunner runner);
    public abstract void Stop();
}
