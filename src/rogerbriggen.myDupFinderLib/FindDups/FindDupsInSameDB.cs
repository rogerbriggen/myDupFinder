// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib
{
    /// <summary>
    /// 
    /// </summary>
    internal class FindDupsInSameDB : BasicRunner<FindDupsInSameDB>, IFindDupsRunner
    {

        public FindDupsInSameDB(MyDupFinderFindDupsJobDTO findDupsJobDTO, ILogger<FindDupsInSameDB>? logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
        {
            FindDupsJobDTO = findDupsJobDTO;
            DubFinderDB = new DubFinderDB(_serviceProvider.GetService<ILogger<DubFinderDB>>());
        }

        // To detect redundant calls
        private bool _disposed = false;
        private MyDupFinderFindDupsJobDTO FindDupsJobDTO { get; set; }

        private DubFinderDB DubFinderDB { get; set; }

        
        public override void Start(CancellationToken token)
        {
            base.Start(token);

            //Setup DB
            DubFinderDB.SetupDB(FindDupsJobDTO.DatabaseFileBase);
            
            var dupList = DubFinderDB.FindDupsInSameDB();
            if (dupList is not null)
            {
                string lastHash = "";
                long totalBytesDuplicate = 0;
                foreach (var item in dupList)
                {
                    if (item.FileSha512Hash == lastHash)
                    {
                        totalBytesDuplicate += item.FileSize;
                    }
                    lastHash = item.FileSha512Hash;
                }
                _logger.LogInformation($"Possible Savings in bytes: {totalBytesDuplicate:N0} which is {totalBytesDuplicate / 1024 / 1024:N0} MB");
                createDuplicateReport(FindDupsJobDTO.ReportPath + FindDupsJobDTO.JobName + " dupReport.csv", dupList, FindDupsJobDTO);
            }
            dupList = null;
            DubFinderDB.Dispose();
        }

        
        public void createDuplicateReport(string reportPathAndName, List<ScanItemDto> dupList, MyDupFinderFindDupsJobDTO findDupsJobDTO)
        {
            using (var fs = new StreamWriter(reportPathAndName))
            {
                string lastHash = "";
                
                foreach (ScanItemDto si in dupList)
                {
                    if (si.FileSha512Hash != lastHash)
                    {
                        fs.WriteLine("\"" + si.FilenameAndPath + "\"," + si.FileSize);
                    }
                    else
                    {
                        fs.WriteLine("\"" + si.FilenameAndPath + "\"," + si.FileSize);
                        fs.WriteLine("");
                    }
                    lastHash = si.FileSha512Hash;
                }
            }
            _logger.LogInformation($"Duplicate report successfully created: {reportPathAndName}");
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
                //_findDupsItemCollection.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
            // TODO: set large fields to null.
            _disposed = true;

            // Call base class implementation.
            base.Dispose(disposing);
        }


    }
}
