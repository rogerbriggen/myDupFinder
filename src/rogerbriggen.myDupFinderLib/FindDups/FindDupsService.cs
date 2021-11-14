// Roger Briggen license this file to you under the MIT license.
//

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib;


public class FindDupsService : BasicService<FindDupsService>, IFindDupsService
{
    // To detect redundant calls
    private bool _disposed = false;

    public FindDupsService(ILogger<FindDupsService> logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
    {

    }

    public void Start(MyDupFinderFindDupsJobDTO findDupsJobDTO)
    {
        //TODO check if runner for base path exits... if not, create and add to queue

        if (ServiceState == IService.EServiceState.idle)
        {

            var fdr = new FindDupsInSameDB(findDupsJobDTO, _serviceProvider.GetService<ILogger<FindDupsInSameDB>>(), _serviceProvider);
            base.Start(fdr);
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
