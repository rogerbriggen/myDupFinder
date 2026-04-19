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

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Finds duplicates across two different databases by comparing SHA-512 hashes.
/// </summary>
internal class FindDupsInDifferentDBs : BasicRunner<FindDupsInDifferentDBs>, IFindDupsRunner
{
    public FindDupsInDifferentDBs(MyDupFinderFindDupsJobDTO findDupsJobDTO, ILogger<FindDupsInDifferentDBs>? logger, IServiceProvider serviceProvider) : base(logger, serviceProvider)
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

        //Setup base DB
        DubFinderDB.SetupDB(FindDupsJobDTO.DatabaseFileBase);

        var dupList = DubFinderDB.FindDupsInDifferentDBs(FindDupsJobDTO.DatabaseFile);
        if (dupList is not null)
        {
            long totalBytesDuplicate = 0;
            foreach (var item in dupList)
            {
                totalBytesDuplicate += item.FileSize;
            }
            _logger.LogInformation($"Found {dupList.Count} files in base DB that also exist in second DB. Total bytes: {totalBytesDuplicate:N0} which is {totalBytesDuplicate / 1024 / 1024:N0} MB");
            CreateDuplicateReport(FindDupsJobDTO.ReportPath + FindDupsJobDTO.JobName + " dupReport.csv", dupList, FindDupsJobDTO);
        }
        DubFinderDB.Dispose();
    }


    public void CreateDuplicateReport(string reportPathAndName, List<ScanItemDto> dupList, MyDupFinderFindDupsJobDTO findDupsJobDTO)
    {
        using (var fs = new StreamWriter(reportPathAndName))
        {
            string lastHash = "";

            foreach (ScanItemDto si in dupList)
            {
                if (si.FileSha512Hash != lastHash)
                {
                    //Hash is not the same... so start another group
                    fs.WriteLine("");
                }
                fs.WriteLine("\"" + si.FilenameAndPath + "\";" + si.FileSize);
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
        }

        // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
        // TODO: set large fields to null.
        _disposed = true;

        // Call base class implementation.
        base.Dispose(disposing);
    }


}
