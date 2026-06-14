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
/// Finds duplicates across two different databases by comparing SHA-512 hashes
/// (FindOnlyDups) or fully categorizes Duplicate / Moved / Changed / Missing / New
/// (FindDupsTheWholeLot). Paired rows (both sides of a Duplicate, Changed, or
/// Moved relationship) share a GroupId so a UI can collapse them together.
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

        var rows = FindDupsJobDTO.FindDupsMode == MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot
            ? BuildWholeLotRows(DubFinderDB.GetAllScanItems(), DubFinderDB.GetAllScanItemsFromOtherDB(FindDupsJobDTO.DatabaseFile))
            : BuildOnlyDupsRows(DubFinderDB.FindDupsInDifferentDBs(FindDupsJobDTO.DatabaseFile));

        LogSummary(rows);
        DupReportWriter.Write(FindDupsJobDTO.ReportPath + FindDupsJobDTO.JobName + " dupReport.csv", rows);
        _logger.LogInformation($"Duplicate report successfully created: {FindDupsJobDTO.ReportPath + FindDupsJobDTO.JobName} dupReport.csv");

        DubFinderDB.Dispose();
    }

    /// <summary>
    /// FindOnlyDups path: every returned item is a base-side row in some hash group.
    /// All items sharing a hash get the same GroupId.
    /// </summary>
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

    /// <summary>
    /// FindDupsTheWholeLot: categorize every file in base + second DB.
    /// Phase 1 pairs files by relative path (Duplicate or Changed).
    /// Phase 2 pairs leftovers by hash (Moved).
    /// Phase 3 + 4 emit Missing / New singletons.
    /// </summary>
    private static List<DupReportRow> BuildWholeLotRows(List<ScanItemDto>? baseItems, List<ScanItemDto>? secondItems)
    {
        baseItems ??= new List<ScanItemDto>();
        secondItems ??= new List<ScanItemDto>();

        var gen = new GroupIdGenerator();
        var rows = new List<DupReportRow>();

        var secondByRelPath = new Dictionary<string, ScanItemDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in secondItems)
        {
            // last write wins for duplicate keys, same as base map
            secondByRelPath[DupReportWriter.GetRelativePath(s)] = s;
        }
        var consumedSecond = new HashSet<ScanItemDto>();

        var baseLeftover = new List<ScanItemDto>();

        // Phase 1: path-aligned pairs.
        foreach (var b in baseItems)
        {
            var rel = DupReportWriter.GetRelativePath(b);
            if (secondByRelPath.TryGetValue(rel, out var matched) && !consumedSecond.Contains(matched))
            {
                var groupId = gen.Next();
                var category = string.Equals(b.FileSha512Hash, matched.FileSha512Hash, StringComparison.Ordinal)
                    ? DupReportCategory.Duplicate
                    : DupReportCategory.Changed;
                rows.Add(new DupReportRow(b, category, DupReportSource.Base, groupId));
                rows.Add(new DupReportRow(matched, category, DupReportSource.Second, groupId));
                consumedSecond.Add(matched);
            }
            else
            {
                baseLeftover.Add(b);
            }
        }

        var secondLeftover = secondItems.Where(s => !consumedSecond.Contains(s)).ToList();

        // Phase 2: hash-aligned remainder = Moved.
        var baseByHash = baseLeftover
            .Where(b => !string.IsNullOrEmpty(b.FileSha512Hash))
            .GroupBy(b => b.FileSha512Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var secondByHash = secondLeftover
            .Where(s => !string.IsNullOrEmpty(s.FileSha512Hash))
            .GroupBy(s => s.FileSha512Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var movedBase = new HashSet<ScanItemDto>();
        var movedSecond = new HashSet<ScanItemDto>();
        foreach (var hash in baseByHash.Keys.Intersect(secondByHash.Keys, StringComparer.Ordinal))
        {
            var groupId = gen.Next();
            foreach (var b in baseByHash[hash])
            {
                rows.Add(new DupReportRow(b, DupReportCategory.Moved, DupReportSource.Base, groupId));
                movedBase.Add(b);
            }
            foreach (var s in secondByHash[hash])
            {
                rows.Add(new DupReportRow(s, DupReportCategory.Moved, DupReportSource.Second, groupId));
                movedSecond.Add(s);
            }
        }

        // Phase 3: Missing — base leftovers with no hash partner in second.
        foreach (var b in baseLeftover)
        {
            if (movedBase.Contains(b))
            {
                continue;
            }
            rows.Add(new DupReportRow(b, DupReportCategory.Missing, DupReportSource.Base, gen.Next()));
        }

        // Phase 4: New — second leftovers with no hash partner in base.
        foreach (var s in secondLeftover)
        {
            if (movedSecond.Contains(s))
            {
                continue;
            }
            rows.Add(new DupReportRow(s, DupReportCategory.New, DupReportSource.Second, gen.Next()));
        }

        return rows
            .OrderBy(r => r.Category)
            .ThenBy(r => r.GroupId)
            .ThenBy(r => r.Source)
            .ThenBy(r => r.Item.FilenameAndPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LogSummary(List<DupReportRow> rows)
    {
        long totalBytesDuplicate = rows
            .Where(r => r.Category == DupReportCategory.Duplicate || r.Category == DupReportCategory.Moved)
            .Sum(r => r.Item.FileSize);
        var dupCount = rows.Count(r => r.Category == DupReportCategory.Duplicate);
        var movedCount = rows.Count(r => r.Category == DupReportCategory.Moved);
        _logger.LogInformation($"Found {dupCount} duplicate and {movedCount} moved file rows. Total bytes: {totalBytesDuplicate:N0} which is {totalBytesDuplicate / 1024 / 1024:N0} MB");

        if (FindDupsJobDTO.FindDupsMode == MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot)
        {
            var changed = rows.Count(r => r.Category == DupReportCategory.Changed);
            var missing = rows.Count(r => r.Category == DupReportCategory.Missing);
            var newCount = rows.Count(r => r.Category == DupReportCategory.New);
            _logger.LogInformation($"FindDupsTheWholeLot: {rows.Count} total ({dupCount} Duplicate, {movedCount} Moved, {changed} Changed, {missing} Missing, {newCount} New)");
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
