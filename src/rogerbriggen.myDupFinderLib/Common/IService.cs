using System;

namespace RogerBriggen.MyDupFinderLib
{

    public interface IService
    {
        public enum ServiceState
        {
            idle,
            running,
            cancelled,
            finished
        }

        event EventHandler<int>? ServiceProgressChanged;
        event EventHandler<ServiceState>? ServiceStateChanged;

        void Dispose();

        public abstract void Start(IRunner runner);
        public abstract void Stop();
    }
}
