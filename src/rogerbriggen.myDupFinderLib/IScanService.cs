using System;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib
{

    public enum ServiceState
    {
        idle,
        running,
        paused,
        pausing,
        finished
    }

    public interface IScanService
    {
        event EventHandler<int>? ScanProgressChanged;
        event EventHandler<ServiceState>? ScanStateChanged;

        void Dispose();
        void StartScan(MyDupFinderScanJobDTO scanJobDTO);
        void StopScan(string basePath);
    }
}
