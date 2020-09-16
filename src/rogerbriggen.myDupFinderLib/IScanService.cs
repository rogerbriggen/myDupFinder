using System;

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
        void StartScan(string basePath, string originComputer, string dbPathAndFilename);
        void StopScan(string basePath);
    }
}
