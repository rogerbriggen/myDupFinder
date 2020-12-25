using System;

namespace RogerBriggen.MyDupFinderLib
{

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
}
