// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderData;

public class MyDupFinderScanJobDTO
{
    public string JobName { get; set; } = string.Empty;

    public string BasePath { get; set; } = string.Empty;

    public string OriginComputer { get; set; } = string.Empty;

    public string ScanName { get; set; } = string.Empty;

    public string DatabaseFile { get; set; } = string.Empty;

    public string ReportPath { get; set; } = string.Empty;

    /// <summary>
    /// Share of the working time the job spends sleeping to spare the disk (0 - 70).
    /// 0 (the default, also used when the element is missing in the project file) means no throttling.
    /// </summary>
    public uint ThrottlePercent { get; set; }

    /// <summary>
    /// Highest accepted value for <see cref="ThrottlePercent"/>.
    /// </summary>
    public const uint MaxThrottlePercent = 70;

    public static void CheckSanity(MyDupFinderScanJobDTO dto)
    {
        //Rule: BasePath, DatabaseFile, scanName, OriginComputer and JobName may not be null
        if (string.IsNullOrWhiteSpace(dto.JobName))
        {
            throw new ParameterException("Param JobName may not be null or empty");
        }
        if (string.IsNullOrWhiteSpace(dto.OriginComputer))
        {
            throw new ParameterException("Param OriginComputer may not be null or empty");
        }
        if (string.IsNullOrWhiteSpace(dto.ScanName))
        {
            throw new ParameterException("Param ScanName may not be null or empty");
        }
        if (string.IsNullOrWhiteSpace(dto.BasePath))
        {
            throw new ParameterException("Param BasePath may not be null or empty");
        }
        if (string.IsNullOrWhiteSpace(dto.DatabaseFile))
        {
            throw new ParameterException("Param DatabaseFile may not be null or empty");
        }

        //Rule: ThrottlePercent must be 0 - 70
        if (dto.ThrottlePercent > MaxThrottlePercent)
        {
            throw new ParameterException($"Param ThrottlePercent must be between 0 and {MaxThrottlePercent}! {dto.ThrottlePercent}");
        }

        //Rule: BasePath must exist
        if (!System.IO.Directory.Exists(dto.BasePath))
        {
            throw new ParameterException($"BasePath must exist! {dto.BasePath}");
        }

        //Rule: DatabaseFile and ReportPath are not allowed below the BasePath
        string basePath = FileHelper.AddDirectoryDelimiter(dto.BasePath);
        if (dto.DatabaseFile.StartsWith(basePath))
        {
            throw new ParameterException("Param DatabaseFile may not be a subdirectory of BasePath!");
        }
        if (dto.ReportPath.StartsWith(basePath))
        {
            throw new ParameterException("Param ReportPath may not be a subdirectory of BasePath!");
        }
    }

    public static void FixDto(MyDupFinderScanJobDTO dto)
    {
        // Rule: Make sure the base path has a delimiter at the end
        dto.BasePath = FileHelper.AddDirectoryDelimiter(dto.BasePath);

        // Rule: Make sure the ReportPath path has a delimiter at the end
        dto.ReportPath = FileHelper.AddDirectoryDelimiter(dto.ReportPath);
    }
}
