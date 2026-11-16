// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>The choice of the primary row of a path, the resume decision, and the text of the time columns of the check report.</summary>
public class PrimaryRowChooserTest
{
    private static readonly DateTime s_t0 = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567);

    private static ScanItemDto R(long size, DateTime mtime, DateTime lastScan, string id) =>
        RowsPerPathSupport.Row("/p/a.bin", "/p", "H", size, DateTime.SpecifyKind(mtime, DateTimeKind.Unspecified), lastScan, id: Guid.Parse(id));

    private const string Id1 = "00000000-0000-0000-0000-000000000001";
    private const string Id2 = "00000000-0000-0000-0000-000000000002";

    [Fact]
    public void MatchingRowWinsOverALaterScannedRow()
    {
        var match = R(10, s_t0, s_t0, Id1);
        var later = R(11, s_t0, s_t0.AddHours(1), Id2);
        Assert.Same(match, PrimaryRowChooser.Choose(new[] { later, match }, 10, s_t0));
    }

    [Fact]
    public void WithoutAMatch_TheLatestScanDateWins_ThenTheHighestIdText()
    {
        var older = R(11, s_t0, s_t0, Id2);
        var newer = R(12, s_t0, s_t0.AddHours(1), Id1);
        Assert.Same(newer, PrimaryRowChooser.Choose(new[] { older, newer }, 10, s_t0));
        var tieLow = R(11, s_t0, s_t0, Id1);
        var tieHigh = R(12, s_t0, s_t0, Id2);
        Assert.Same(tieHigh, PrimaryRowChooser.Choose(new[] { tieLow, tieHigh }, 10, s_t0));
        Assert.Same(tieHigh, PrimaryRowChooser.Choose(new[] { tieHigh, tieLow }, 10, s_t0));
    }

    [Fact]
    public void DecideResume_CoversTheThreeKinds_AndListsTheSurplusRows()
    {
        Assert.Equal(ResumeLookupKind.NotInDatabase, ScanJobDBInserts.DecideResume(new List<ScanItemDto>(), 10, s_t0, "TestPC", "TestScan").Kind);
        var match = R(10, s_t0, s_t0, Id1);
        var stale = R(11, s_t0, s_t0.AddHours(1), Id2);
        var same = ScanJobDBInserts.DecideResume(new[] { stale, match }, 10, DateTime.SpecifyKind(s_t0, DateTimeKind.Utc), "TestPC", "TestScan");
        Assert.Equal(ResumeLookupKind.AlreadyScanned, same.Kind);
        Assert.Equal(match.Id, same.PrimaryId);
        Assert.Equal(new[] { stale.Id }, same.SurplusIds);
        var changed = ScanJobDBInserts.DecideResume(new[] { match, stale }, 99, s_t0, "TestPC", "TestScan");
        Assert.Equal(ResumeLookupKind.Changed, changed.Kind);
        Assert.Equal(stale.Id, changed.PrimaryId);
        Assert.Equal(new[] { match.Id }, changed.SurplusIds);
        // Only the size or only the time differing is a change as well
        Assert.Equal(ResumeLookupKind.Changed, ScanJobDBInserts.DecideResume(new[] { match }, 10, s_t0.AddTicks(1), "TestPC", "TestScan").Kind);
        Assert.Equal(ResumeLookupKind.Changed, ScanJobDBInserts.DecideResume(new[] { match }, 11, s_t0, "TestPC", "TestScan").Kind);
    }

    [Fact]
    public void DecideResume_RowsOfAnotherIdentity_AreNeverPrimaryOrSurplus()
    {
        var own = R(10, s_t0, s_t0, Id1);
        var foreignStale = RowsPerPathSupport.ForeignRow("/p/a.bin", "/p", "H", 11, s_t0, s_t0.AddHours(5));
        var foreignMatch = RowsPerPathSupport.ForeignRow("/p/a.bin", "/p", "H", 99, s_t0, s_t0.AddHours(5));
        // Own row matches: the foreign row is no surplus
        var same = ScanJobDBInserts.DecideResume(new[] { foreignStale, own }, 10, s_t0, "TestPC", "TestScan");
        Assert.Equal(ResumeLookupKind.AlreadyScanned, same.Kind);
        Assert.Empty(same.SurplusIds);
        // Changed file: the own row is updated, the foreign row is no surplus
        var changed = ScanJobDBInserts.DecideResume(new[] { foreignStale, own }, 50, s_t0, "TestPC", "TestScan");
        Assert.Equal(ResumeLookupKind.Changed, changed.Kind);
        Assert.Equal(own.Id, changed.PrimaryId);
        Assert.Empty(changed.SurplusIds);
        // Only foreign rows, none matches: insert
        Assert.Equal(ResumeLookupKind.NotInDatabase, ScanJobDBInserts.DecideResume(new[] { foreignStale }, 50, s_t0, "TestPC", "TestScan").Kind);
        // Only a foreign row matches: already scanned (today's rule), nothing to remove, also not the stale own row
        var skip = ScanJobDBInserts.DecideResume(new[] { own, foreignMatch }, 99, s_t0, "TestPC", "TestScan");
        Assert.Equal(ResumeLookupKind.AlreadyScanned, skip.Kind);
        Assert.Empty(skip.SurplusIds);
        Assert.Equal(ResumeLookupKind.AlreadyScanned, ScanJobDBInserts.DecideResume(new[] { foreignMatch }, 99, s_t0, "TestPC", "TestScan").Kind);
        // Same OriginComputer but another ScanName is another identity, and the comparison is exact (case matters)
        Assert.Equal(ResumeLookupKind.NotInDatabase, ScanJobDBInserts.DecideResume(new[] { own }, 50, s_t0, "TestPC", "testscan").Kind);
    }

    [Fact]
    public void FormatUtc_Unspecified_IsTakenAsUtc_AndNeverShifted()
    {
        var unspecified = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Unspecified).AddTicks(1234567);
        Assert.Equal("2024-03-04T05:06:07.1234567Z", CheckReportCsv.FormatUtc(unspecified));
        Assert.Equal("2024-03-04T05:06:07.1234567Z", CheckReportCsv.FormatUtc(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)));
    }

    [Fact]
    public void FormatUtc_Local_IsConvertedToUtc()
    {
        var local = new DateTime(2024, 7, 4, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), CheckReportCsv.FormatUtc(local));
    }
}
