// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib
{
    /// <summary>
    /// This will scan the files with a single thread and also throttle the hdd disk access a bit...
    /// </summary>
    internal class ScanRunnerSingleThread : BasicRunner<ScanRunnerSingleThread>, IScanRunner
    {

        public ScanRunnerSingleThread(MyDupFinderScanJobDTO scanJobDTO, ILogger<ScanRunnerSingleThread>? logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
        {            
            ScanJobDTO = scanJobDTO;
            ScanJobDBInserts = new ScanJobDBInserts(_serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        }

        // To detect redundant calls
        private bool _disposed = false;
        private MyDupFinderScanJobDTO ScanJobDTO { get; set; }

        private ScanJobDBInserts ScanJobDBInserts { get; set; }

        private BlockingCollection<ScanItemDto> _scanItemCollection = new BlockingCollection<ScanItemDto>();

        private IEnumerateFilesToIndex? _enumerateFiles;

        public override void Start(CancellationToken token)
        {
            base.Start(token);

            //Setup DB
            ScanJobDBInserts.SetupDB(ScanJobDTO.DatabaseFile);
            _enumerateFiles = new EnumerateFilesFromDiscToIndex(ScanJobDTO, _serviceProvider.GetService<ILogger<EnumerateFilesFromDiscToIndex>>());
            Scan();
            ScanJobDBInserts.Dispose();
        }

        private void Scan()
        {
            DiscThrottle discThrottle = new DiscThrottle(10, TimeSpan.FromMinutes(5).TotalMilliseconds);
            bool bFinish = false;
            //Don't filter if db is empty...
            bool bFilter = !ScanJobDBInserts.IsEmptyScanItemTable();
            if (bFilter)
            {
                //Check if there is at least one item from this base path... if no, we don't filter
                bFilter = ScanJobDBInserts.IsBasePathAlreadyInDB(ScanJobDTO.BasePath);
            }
            try
            {
                while (!bFinish)
                {
                    //CancelToken?
                    if (CancelToken.IsCancellationRequested)
                    {
                        ScanJobDBInserts.WriteChanges();
                        _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
                        RunnerState = IService.ServiceState.cancelled;
                        return;
                    }

                    //Throttle
                    discThrottle.Throttle(CancelToken);
                    
                    //Enumerate
                    if (!(_enumerateFiles?.HasMore == true))
                    {
                        bFinish = true;
                        continue;
                    }
                    _enumerateFiles.EnumerateFiles();
                    
                    

                    //CalcHash
                    while(true)
                    {
                        //Get item
                        ScanItemDto? si;
                        if (!_enumerateFiles.ScanItemCollection.TryDequeue(out si))
                        {
                            break;
                        }

                        //Filter
                        if (bFilter)
                        {
                            if (ScanJobDBInserts.IsAlreadyInDB(si))
                            {
                                continue;
                            }
                        }

                        //Calc hash
                        CalcHash(si);
                        
                        //CancelToken?
                        if (CancelToken.IsCancellationRequested)
                        {
                            ScanJobDBInserts.WriteChanges();
                            _logger.LogInformation($"Scanning was cancelled by user... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
                            RunnerState = IService.ServiceState.cancelled;
                            return;
                        }

                        //Breath...
                        System.Threading.Thread.Sleep(10);

                        //Throttle
                        discThrottle.Throttle(CancelToken);
                    }
                    _logger.LogDebug($"Scanning... FileEnumeration currentCount={_enumerateFiles.CurrentCount}  errorCount={_enumerateFiles.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
                }
                ScanJobDBInserts.WriteChanges();
                RunnerState = IService.ServiceState.finished;
                _logger.LogInformation($"Scanning finished... FileEnumeration currentCount={_enumerateFiles?.CurrentCount}  errorCount={_enumerateFiles?.ErrorCount}  AddedToDB SuccessCount={ScanJobDBInserts.TotalSuccessCount} ErrorCount={ScanJobDBInserts.TotalErrorCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Scan!");
            }
        }


        private void CalcHash(ScanItemDto item)
        {
            try
            {
                using (var sha512 = SHA512.Create())
                {
                    using (var stream = File.OpenRead(item.FilenameAndPath))
                    {
                        item.FileSha512Hash = BitConverter.ToString(sha512.ComputeHash(stream)).Replace("-", "", StringComparison.Ordinal);
                        ScanJobDBInserts.Enqueue(item);
                        _logger.LogInformation("File {file} successfull finished", item.FilenameAndPath);
                    }
                }
            }
#pragma warning disable CA1031 // Keine allgemeinen Ausnahmetypen abfangen
            catch (Exception e)
#pragma warning restore CA1031 // Keine allgemeinen Ausnahmetypen abfangen
            {
                ScanJobDBInserts.Enqueue(new ScanErrorItemDto(item, e, _runStarted));
                _logger.LogError(e, "Hashing of {file} failed.", item.FilenameAndPath);
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
                _scanItemCollection.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
            // TODO: set large fields to null.
            _disposed = true;

            // Call base class implementation.
            base.Dispose(disposing);
        }


    }
}
