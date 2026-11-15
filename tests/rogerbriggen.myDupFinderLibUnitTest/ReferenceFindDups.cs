// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The cross-database only-dups query as it was before step 5, kept as the reference for the equivalence tests and the benchmark:
/// all distinct non-empty hashes of the second database are loaded into a list and passed back to the base database as a filter.
/// It fails with "too many SQL variables" above about 32766 distinct hashes. Do not use it in production code.
/// </summary>
internal static class ReferenceFindDups
{
    public static List<ScanItemDto>? FindDupsInDifferentDBs(DubFinderContext context, DubFinderContext secondContext)
    {
        // Load hashes from the second database
        var secondDbHashes = secondContext.Set<ScanItemDto>()
            .Where(s => s.FileSha512Hash != null && s.FileSha512Hash != string.Empty)
            .Select(s => s.FileSha512Hash)
            .Distinct()
            .ToList();

        if (secondDbHashes.Count == 0)
        {
            return null;
        }

        // Find items in the base database that match hashes from the second database
        return context.Set<ScanItemDto>()
            .Where(s => secondDbHashes.Contains(s.FileSha512Hash))
            .OrderBy(s => s.FileSha512Hash)
            .ToList();
    }
}
