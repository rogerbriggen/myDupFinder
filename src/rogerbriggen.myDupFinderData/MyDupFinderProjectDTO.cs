// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Generic;
using System.Linq;

namespace RogerBriggen.MyDupFinderData;

public class MyDupFinderProjectDTO
{
    public MyDupFinderProjectDTO()
    {
        MyDupFinderScanJobDTOs = new List<MyDupFinderScanJobDTO>();
        MyDupFinderCheckJobDTOs = new List<MyDupFinderCheckJobDTO>();
        MyDupFinderFindDupsJobDTOs = new List<MyDupFinderFindDupsJobDTO>();
        MyDupFinderRefreshJobDTOs = new List<MyDupFinderRefreshJobDTO>();
    }
    public List<MyDupFinderScanJobDTO> MyDupFinderScanJobDTOs { get; }
    public List<MyDupFinderCheckJobDTO> MyDupFinderCheckJobDTOs { get; }
    public List<MyDupFinderFindDupsJobDTO> MyDupFinderFindDupsJobDTOs { get; }
    public List<MyDupFinderRefreshJobDTO> MyDupFinderRefreshJobDTOs { get; }


    /// <summary>
    /// The jobs of the project in the order in which they run: first the jobs that change databases (all scan jobs, then all refresh jobs),
    /// then the jobs that only read them (all find-duplicates jobs, then all check jobs). Within one type the order of the project file is kept.
    /// So a find-duplicates report always sees the database after the scan and refresh jobs of the same project.
    /// </summary>
    public IReadOnlyList<ProjectJob> GetJobsInExecutionOrder()
    {
        var jobs = new List<ProjectJob>();
        jobs.AddRange(MyDupFinderScanJobDTOs.Select(j => new ProjectJob(EProjectJobType.Scan, j.JobName, j)));
        jobs.AddRange(MyDupFinderRefreshJobDTOs.Select(j => new ProjectJob(EProjectJobType.Refresh, j.JobName, j)));
        jobs.AddRange(MyDupFinderFindDupsJobDTOs.Select(j => new ProjectJob(EProjectJobType.FindDups, j.JobName, j)));
        jobs.AddRange(MyDupFinderCheckJobDTOs.Select(j => new ProjectJob(EProjectJobType.Check, j.ScanJobDTO.JobName, j)));
        return jobs;
    }

    public static void CheckSanity(MyDupFinderProjectDTO dto)
    {
        // Same order as the execution order. Databases that a scan job creates are the only ones a find-duplicates job may name without
        // the file existing yet: refresh and check jobs require an existing database, so they never create one.
        var scanDBs = new List<string>();
        foreach (MyDupFinderScanJobDTO scanJobDto in dto.MyDupFinderScanJobDTOs)
        {
            MyDupFinderScanJobDTO.CheckSanity(scanJobDto);
            scanDBs.Add(scanJobDto.DatabaseFile);
        }
        foreach (MyDupFinderRefreshJobDTO refreshJobDto in dto.MyDupFinderRefreshJobDTOs)
        {
            MyDupFinderRefreshJobDTO.CheckSanity(refreshJobDto);
        }
        foreach (MyDupFinderFindDupsJobDTO findDupsJobDto in dto.MyDupFinderFindDupsJobDTOs)
        {
            MyDupFinderFindDupsJobDTO.CheckSanity(findDupsJobDto, scanDBs);
        }
        foreach (MyDupFinderCheckJobDTO checkJobDto in dto.MyDupFinderCheckJobDTOs)
        {
            MyDupFinderCheckJobDTO.CheckSanity(checkJobDto);
        }
    }


    public static void FixDto(MyDupFinderProjectDTO dto)
    {
        foreach (MyDupFinderScanJobDTO scanJobDto in dto.MyDupFinderScanJobDTOs)
        {
            MyDupFinderScanJobDTO.FixDto(scanJobDto);
        }
        foreach (MyDupFinderRefreshJobDTO refreshJobDto in dto.MyDupFinderRefreshJobDTOs)
        {
            MyDupFinderRefreshJobDTO.FixDto(refreshJobDto);
        }
        foreach (MyDupFinderFindDupsJobDTO findDupsJobDto in dto.MyDupFinderFindDupsJobDTOs)
        {
            MyDupFinderFindDupsJobDTO.FixDto(findDupsJobDto);
        }
        foreach (MyDupFinderCheckJobDTO checkJobDto in dto.MyDupFinderCheckJobDTOs)
        {
            MyDupFinderCheckJobDTO.FixDto(checkJobDto);
        }
    }
}
