// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Threading;

namespace RogerBriggen.MyDupFinderLib
{
    internal interface IScanRunner
    {
        ServiceState ScanState { get; set; }

        event EventHandler<int>? ScanProgressChanged;
        event EventHandler<ServiceState>? ScanStateChanged;

        void Dispose();
        void Start(CancellationToken token);
    }
}
