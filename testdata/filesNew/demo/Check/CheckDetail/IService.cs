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
        finished
    }

    event EventHandler<int>? ServiceProgressChanged;
    event EventHandler<EServiceState>? ServiceStateChanged;

    IService.EServiceState ServiceState { get; }

    public abstract void Start(IRunner runner);
    public abstract void Stop();
}
