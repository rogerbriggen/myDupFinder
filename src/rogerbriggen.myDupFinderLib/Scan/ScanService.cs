using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib
{


    public class ScanService : BasicService<ScanService>, IScanService
    {
        // To detect redundant calls
        private bool _disposed = false;

        public ScanService(ILogger<ScanService> logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
        {
            
        }

        public void StartScan(MyDupFinderScanJobDTO scanJobDTO)
        {
            //TODO check if runner for base path exits... if not, create and add to queue

            if (ScanState == IService.ServiceState.idle)
            {
                var sr = new ScanRunnerSingleThread(scanJobDTO, _serviceProvider.GetService<ILogger<ScanRunnerSingleThread>>(), _serviceProvider);
                base.Start(sr);
            }
        }

        // Protected implementation of Dispose pattern.
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Dispose managed state (managed objects).
                
            }

            // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
            // TODO: set large fields to null.
            _disposed = true;

            // Call base class implementation.
            base.Dispose(disposing);
        }

    }
}
