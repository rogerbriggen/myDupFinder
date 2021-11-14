// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Threading;

namespace RogerBriggen.MyDupFinderLib;

public interface IRunner
{
    IService.EServiceState RunnerState { get; set; }

    event EventHandler<int>? RunnerProgressChanged;
    event EventHandler<IService.EServiceState>? RunnerStateChanged;

    void Dispose();
    void Start(CancellationToken token);
}
