// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Generic;

namespace RogerBriggen.MyDupFinderData;

public class MyDupFinderFindDupsJobDTO
{
    public enum EFindDupsMode
    {
        FindOnlyDups,  //finds only duplicate files
        FindDupsTheWholeLot    //reports everything: duplicates, missing files, new files, changed files
    }

    private const string ReportFileSuffix = " dupReport.csv";

    public string JobName { get; set; } = string.Empty;

    public string DatabaseFileBase { get; set; } = string.Empty;

    public string DatabaseFile { get; set; } = string.Empty;

    public EFindDupsMode FindDupsMode { get; set; }

    public string ReportPath { get; set; } = string.Empty;

    public static void CheckSanity(MyDupFinderFindDupsJobDTO dto, List<string> scanDBs)
    {
        //Rule: BasePath, DatabaseFile, scanName, OriginComputer and JobName may not be null
        if (string.IsNullOrWhiteSpace(dto.JobName))
        {
            throw new ParameterException("Param JobName may not be null or empty");
        }
        if (string.IsNullOrWhiteSpace(dto.DatabaseFileBase))
        {
            throw new ParameterException("Param DatabaseFileBase may not be null or empty");
        }
        if (string.IsNullOrWhiteSpace(dto.ReportPath))
        {
            throw new ParameterException("Param ReportPath may not be null or empty");
        }


        //Rule: DatabaseFileBase must exist or it must be created with the scanJob...
        if (!scanDBs.Contains(dto.DatabaseFileBase))
        {
            //It is not created with the scanJob... so it must already exist...
            if (!System.IO.File.Exists(dto.DatabaseFileBase))
            {
                throw new ParameterException($"DatabaseFileBase must exist! {dto.DatabaseFileBase}");
            }


        }
        //Rule: DatabaseFile must be "", null or must exist
        if (!string.IsNullOrEmpty(dto.DatabaseFile))
        {
            //OK, it is not null or empty... 
            // ... check if created by our scanJob
            if (!scanDBs.Contains(dto.DatabaseFile))
            {
                //It is not created by our scanJob... so it must be existing...
                if (!System.IO.File.Exists(dto.DatabaseFile))
                {
                    throw new ParameterException($"DatabaseFile must exist! {dto.DatabaseFile}");
                }
            }
        }

        // Rule: Make sure the ReportPath is not a file
        if (System.IO.File.Exists(dto.ReportPath))
        {
            throw new ParameterException($"ReportPath is a file! {dto.ReportPath}");
        }

        // Rule: Make sure the Report is not yet existing (CheckSanity runs before FixDto, so the path is built the same way the runners build it)
        string reportFile = BuildReportFilePath(dto.ReportPath, dto.JobName);
        if (System.IO.File.Exists(reportFile))
        {
            throw new ParameterException($"Report already existing! {reportFile}");
        }
    }

    /// <summary>
    /// The one place that builds the report file name: <c>&lt;ReportPath&gt;&lt;JobName&gt; dupReport.csv</c>.
    /// ReportPath with or without a trailing directory separator gives the same result.
    /// </summary>
    public static string BuildReportFilePath(string reportPath, string jobName) =>
        FileHelper.AddDirectoryDelimiter(reportPath) + jobName + ReportFileSuffix;

    /// <summary>
    /// The file the find-duplicates job writes its report to (see <see cref="BuildReportFilePath"/>).
    /// </summary>
    public string GetReportFilePath() => BuildReportFilePath(ReportPath, JobName);

    public static void FixDto(MyDupFinderFindDupsJobDTO dto) =>
        // Rule: Make sure the ReportPath path has a delimiter at the end
        dto.ReportPath = FileHelper.AddDirectoryDelimiter(dto.ReportPath);
}
