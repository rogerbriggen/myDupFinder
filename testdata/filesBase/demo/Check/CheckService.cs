// Roger Briggen license this file to you under the MIT license.
//

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib;


public class CheckService : BasicService<CheckService>, ICheckService
{
    // To detect redundant calls
    private bool _disposed = false;

    public CheckService(ILogger<CheckService> logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
    {

    }

    public void StartCheck(MyDupFinderCheckJobDTO checkJobDTO)
    {
        if (ServiceState == IService.EServiceState.idle)
        {
            var cr = new CheckRunner(checkJobDTO, _serviceProvider.GetService<ILogger<CheckRunner>>(), _serviceProvider);
            base.Start(cr);
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
