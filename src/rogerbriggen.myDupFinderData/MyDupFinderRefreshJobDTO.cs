// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderData;

public class MyDupFinderRefreshJobDTO
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
    /// false (the default, also used when the element is missing in the project file): for every file the job reads size, creation time and last write time
    /// from the file itself (one call per file) and decides and stores with these values. This is always correct.
    /// true: the job uses the size and timestamps from the directory listing and makes no call per file. That is faster on network shares and large hard disk archives,
    /// but NTFS can leave the directory entry of a hard link stale when the file was changed through another link (also after writing has finished).
    /// A change made that way can then be missed by refresh (the file counts as unchanged, the old hash stays) and by check without hash check (reported Ok).
    /// Only recommended for trees without hard links.
    /// </summary>
    public bool TrustDirectoryMetadata { get; set; }

    /// <summary>
    /// See <see cref="MyDupFinderScanJobDTO.DatabaseSynchronous"/>. <see cref="DatabaseSynchronous.Full"/> is the default, also used when the element is missing in the project file.
    /// <see cref="DatabaseSynchronous.Normal"/> is only applied to a database in WAL mode (not on a network location) and can lose the most recent committed batches after a power failure
    /// or an operating system crash; otherwise it is ignored with a Warning.
    /// </summary>
    public DatabaseSynchronous DatabaseSynchronous { get; set; } = DatabaseSynchronous.Full;

    public static void CheckSanity(MyDupFinderRefreshJobDTO dto)
    {
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
        if (dto.ThrottlePercent > MyDupFinderScanJobDTO.MaxThrottlePercent)
        {
            throw new ParameterException($"Param ThrottlePercent must be between 0 and {MyDupFinderScanJobDTO.MaxThrottlePercent}! {dto.ThrottlePercent}");
        }

        //Rule: DatabaseSynchronous must be a defined value
        if (!System.Enum.IsDefined(dto.DatabaseSynchronous))
        {
            throw new ParameterException($"Param DatabaseSynchronous must be Full or Normal! {dto.DatabaseSynchronous}");
        }

        //Rule: BasePath must exist
        if (!System.IO.Directory.Exists(dto.BasePath))
        {
            throw new ParameterException($"BasePath must exist! {dto.BasePath}");
        }

        //Rule: DatabaseFile must exist (we are refreshing an existing database)
        if (!System.IO.File.Exists(dto.DatabaseFile))
        {
            throw new ParameterException($"DatabaseFile must exist for refresh! {dto.DatabaseFile}");
        }

        //Rule: DatabaseFile and ReportPath are not allowed below the BasePath
        string basePath = FileHelper.AddDirectoryDelimiter(dto.BasePath);
        if (dto.DatabaseFile.StartsWith(basePath))
        {
            throw new ParameterException("Param DatabaseFile may not be a subdirectory of BasePath!");
        }
        // CheckSanity runs before FixDto: a ReportPath that is the BasePath itself, without trailing separator, must be caught too
        if (!string.IsNullOrWhiteSpace(dto.ReportPath) && FileHelper.AddDirectoryDelimiter(dto.ReportPath).StartsWith(basePath))
        {
            throw new ParameterException("Param ReportPath may not be a subdirectory of BasePath!");
        }
    }

    public static void FixDto(MyDupFinderRefreshJobDTO dto)
    {
        // Rule: Make sure the base path has a delimiter at the end
        dto.BasePath = FileHelper.AddDirectoryDelimiter(dto.BasePath);

        // Rule: Make sure the ReportPath path has a delimiter at the end
        if (!string.IsNullOrWhiteSpace(dto.ReportPath))
        {
            dto.ReportPath = FileHelper.AddDirectoryDelimiter(dto.ReportPath);
        }
    }
}
