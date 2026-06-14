// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// Finds duplicates inside a single database by comparing SHA-512 hashes.
/// Items sharing a hash share a GroupId in the report.
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

        var rows = FindDupsJobDTO.FindDupsMode == MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot
            ? BuildWholeLotRows(DubFinderDB.GetAllScanItems())
            : BuildOnlyDupsRows(DubFinderDB.FindDupsInSameDB());

        LogSummary(rows);
        DupReportWriter.Write(FindDupsJobDTO.ReportPath + FindDupsJobDTO.JobName + " dupReport.csv", rows);
        _logger.LogInformation($"Duplicate report successfully created: {FindDupsJobDTO.ReportPath + FindDupsJobDTO.JobName} dupReport.csv");

        DubFinderDB.Dispose();
    }

    private static List<DupReportRow> BuildOnlyDupsRows(List<ScanItemDto>? items)
    {
        if (items is null || items.Count == 0)
        {
            return new List<DupReportRow>();
        }
        var gen = new GroupIdGenerator();
        var hashToGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<DupReportRow>(items.Count);
        foreach (var item in items)
        {
            var hash = item.FileSha512Hash ?? string.Empty;
            if (!hashToGroup.TryGetValue(hash, out var groupId))
            {
                groupId = gen.Next();
                hashToGroup[hash] = groupId;
            }
            rows.Add(new DupReportRow(item, DupReportCategory.Duplicate, DupReportSource.Base, groupId));
        }
        return rows;
    }

    private static List<DupReportRow> BuildWholeLotRows(List<ScanItemDto>? items)
    {
        if (items is null || items.Count == 0)
        {
            return new List<DupReportRow>();
        }
        var hashCounts = items
            .GroupBy(i => i.FileSha512Hash ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var gen = new GroupIdGenerator();
        var hashToGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<DupReportRow>(items.Count);
        foreach (var item in items)
        {
            var hash = item.FileSha512Hash ?? string.Empty;
            var isDup = hashCounts.TryGetValue(hash, out var count) && count > 1;
            int groupId;
            if (isDup)
            {
                if (!hashToGroup.TryGetValue(hash, out groupId))
                {
                    groupId = gen.Next();
                    hashToGroup[hash] = groupId;
                }
            }
            else
            {
                groupId = gen.Next();
            }
            rows.Add(new DupReportRow(
                item,
                isDup ? DupReportCategory.Duplicate : DupReportCategory.Unique,
                DupReportSource.Base,
                groupId));
        }
        return rows;
    }

    private void LogSummary(List<DupReportRow> rows)
    {
        long totalBytesDuplicate = 0;
        var dupGroups = rows
            .Where(r => r.Category == DupReportCategory.Duplicate)
            .GroupBy(r => r.GroupId);
        foreach (var g in dupGroups)
        {
            // possible savings = (copies - 1) * size
            var list = g.ToList();
            if (list.Count > 1)
            {
                totalBytesDuplicate += (list.Count - 1) * list[0].Item.FileSize;
            }
        }
        _logger.LogInformation($"Possible Savings in bytes: {totalBytesDuplicate:N0} which is {totalBytesDuplicate / 1024 / 1024:N0} MB");

        if (FindDupsJobDTO.FindDupsMode == MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot)
        {
            var dupCount = rows.Count(r => r.Category == DupReportCategory.Duplicate);
            var uniqueCount = rows.Count(r => r.Category == DupReportCategory.Unique);
            _logger.LogInformation($"FindDupsTheWholeLot: {rows.Count} total ({dupCount} Duplicate, {uniqueCount} Unique)");
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
            //_findDupsItemCollection.Dispose();
        }

        // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
        // TODO: set large fields to null.
        _disposed = true;

        // Call base class implementation.
        base.Dispose(disposing);
    }


}
