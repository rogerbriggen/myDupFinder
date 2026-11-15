// Roger Briggen license this file to you under the MIT license.
//

using System.Linq;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class ProjectJobOrderTest
{
    private static MyDupFinderCheckJobDTO Check(string name)
    {
        var dto = new MyDupFinderCheckJobDTO();
        dto.ScanJobDTO.JobName = name;
        return dto;
    }

    [Fact]
    public void GetJobsInExecutionOrder_EmptyProject_IsEmpty()
    {
        Assert.Empty(new MyDupFinderProjectDTO().GetJobsInExecutionOrder());
    }

    [Fact]
    public void GetJobsInExecutionOrder_RunsScanRefreshFindDupsCheck_AndKeepsTheOrderWithinAType()
    {
        var dto = new MyDupFinderProjectDTO();
        // added in the "wrong" order on purpose
        dto.MyDupFinderCheckJobDTOs.Add(Check("check1"));
        dto.MyDupFinderFindDupsJobDTOs.Add(new MyDupFinderFindDupsJobDTO { JobName = "dups1" });
        dto.MyDupFinderRefreshJobDTOs.Add(new MyDupFinderRefreshJobDTO { JobName = "refresh1" });
        dto.MyDupFinderScanJobDTOs.Add(new MyDupFinderScanJobDTO { JobName = "scan1" });
        dto.MyDupFinderScanJobDTOs.Add(new MyDupFinderScanJobDTO { JobName = "scan2" });
        dto.MyDupFinderFindDupsJobDTOs.Add(new MyDupFinderFindDupsJobDTO { JobName = "dups2" });
        dto.MyDupFinderRefreshJobDTOs.Add(new MyDupFinderRefreshJobDTO { JobName = "refresh2" });
        dto.MyDupFinderCheckJobDTOs.Add(Check("check2"));

        var jobs = dto.GetJobsInExecutionOrder();

        Assert.Equal(
            new[] { "scan1", "scan2", "refresh1", "refresh2", "dups1", "dups2", "check1", "check2" },
            jobs.Select(j => j.JobName));
        Assert.Equal(
            new[]
            {
                EProjectJobType.Scan, EProjectJobType.Scan, EProjectJobType.Refresh, EProjectJobType.Refresh,
                EProjectJobType.FindDups, EProjectJobType.FindDups, EProjectJobType.Check, EProjectJobType.Check,
            },
            jobs.Select(j => j.JobType));
    }

    [Fact]
    public void GetJobsInExecutionOrder_RefreshRunsBeforeTheFindDupsJobOfTheSameDatabase()
    {
        var dto = new MyDupFinderProjectDTO();
        dto.MyDupFinderFindDupsJobDTOs.Add(new MyDupFinderFindDupsJobDTO { JobName = "report", DatabaseFileBase = "db.db" });
        dto.MyDupFinderRefreshJobDTOs.Add(new MyDupFinderRefreshJobDTO { JobName = "refresh", DatabaseFile = "db.db" });

        var types = dto.GetJobsInExecutionOrder().Select(j => j.JobType).ToList();

        Assert.True(types.IndexOf(EProjectJobType.Refresh) < types.IndexOf(EProjectJobType.FindDups));
    }

    [Fact]
    public void GetJobsInExecutionOrder_DtoIsTheJobOfTheType()
    {
        var scan = new MyDupFinderScanJobDTO { JobName = "s" };
        var refresh = new MyDupFinderRefreshJobDTO { JobName = "r" };
        var dups = new MyDupFinderFindDupsJobDTO { JobName = "d" };
        var check = Check("c");
        var dto = new MyDupFinderProjectDTO();
        dto.MyDupFinderScanJobDTOs.Add(scan);
        dto.MyDupFinderRefreshJobDTOs.Add(refresh);
        dto.MyDupFinderFindDupsJobDTOs.Add(dups);
        dto.MyDupFinderCheckJobDTOs.Add(check);

        var jobs = dto.GetJobsInExecutionOrder();

        Assert.Same(scan, jobs[0].Dto);
        Assert.Same(refresh, jobs[1].Dto);
        Assert.Same(dups, jobs[2].Dto);
        Assert.Same(check, jobs[3].Dto);
    }
}
