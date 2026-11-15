// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
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
/// <para>
/// Grouping and pairing rules. The database returns the rows of each side in one total order: hash, then path
/// (<c>FilenameAndPath</c>), then primary key (binary comparison of the text). The GroupIds are handed out in the order
/// described below, starting with 1. The report writer sorts afterwards (category, GroupId, source, path) and does not change
/// the GroupIds. The report does not depend on the order in which the rows were inserted.
/// </para>
/// <list type="bullet">
/// <item><description><c>FindOnlyDups</c>: the base rows whose (non-empty) hash exists in the second database, in the order above.
/// One GroupId per hash, handed out at the first row of the hash: the smallest reported hash gets 1. Only the base side is reported.</description></item>
/// <item><description><c>FindDupsTheWholeLot</c>, the relative path of a row is its path without <c>PathBase</c>, compared ignoring case.
/// Phase 1 (pairing by relative path): the rows of the second database are put in a dictionary by relative path; if several rows
/// have the same relative path, <b>the last one in the order above wins</b> (the one with the largest hash, then path, then key), the
/// others cannot be paired by path. Then the base rows are visited in the order above: <b>the first base row of a relative path
/// takes the second row</b> (so for several base rows with the same relative path it is the one with the smallest hash, then path,
/// then key), later base rows of the same relative path find the second row taken. Each pair gets the next GroupId and is
/// <c>Duplicate</c> if the hashes are equal (rows without a hash count as equal) and <c>Changed</c> otherwise.
/// Phase 2 (by hash): the base rows and second rows that are left over and have a non-empty hash are grouped by hash. For
/// every hash that occurs on both sides, in the order of the first base row of the hash, one GroupId is used for all base and
/// all second rows of the hash: <c>Moved</c>. Phase 3: the base rows left over get the next GroupId each (in the order above):
/// <c>Missing</c>. Phase 4: the second rows left over, likewise: <c>New</c>.</description></item>
/// </list>
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

        string reportFile = FindDupsJobDTO.GetReportFilePath();
        try
        {
            //Setup base DB
            DubFinderDB.SetupDB(FindDupsJobDTO.DatabaseFileBase);

            var rows = FindDupsJobDTO.FindDupsMode == MyDupFinderFindDupsJobDTO.EFindDupsMode.FindDupsTheWholeLot
                ? BuildWholeLotRows(DubFinderDB.GetAllScanItems(), DubFinderDB.GetAllScanItemsFromOtherDB(FindDupsJobDTO.DatabaseFile))
                : BuildOnlyDupsRows(DubFinderDB.FindDupsInDifferentDBs(FindDupsJobDTO.DatabaseFile));

            LogSummary(rows);
            DupReportWriter.Write(reportFile, rows);
            _logger.LogInformation("Duplicate report successfully created: {report}", reportFile);
        }
        catch (ReportAlreadyExistsException ex)
        {
            // The writer never overwrites: the existing report is untouched and no report was written
            _logger.LogError(ex, "The duplicate report {report} already exists and is not overwritten. No report was written. Remove or rename it and run the job again.", reportFile);
        }
        catch (ReportWriteFailedException ex)
        {
            _logger.LogError(ex, "Error in FindDups! Writing the duplicate report failed after the file was created, a partial report file may have been left: {report}. It is not deleted automatically.", reportFile);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            _logger.LogError(ex, "Error in FindDups!");
        }
        finally
        {
            DubFinderDB.Dispose();
        }
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

        // DupReportWriter applies the canonical sort.
        return rows;
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
