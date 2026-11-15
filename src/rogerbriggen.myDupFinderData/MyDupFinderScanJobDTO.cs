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
    /// false (the default, also used when the element is missing in the project file): for every file the job reads size, creation time and last write time
    /// from the file itself (one call per file) and decides and stores with these values. This is always correct.
    /// true: the job uses the size and timestamps from the directory listing and makes no call per file. That is faster on network shares and large hard disk archives,
    /// but NTFS can leave the directory entry of a hard link stale when the file was changed through another link (also after writing has finished).
    /// A change made that way can then be missed by refresh (the file counts as unchanged, the old hash stays) and by check without hash check (reported Ok).
    /// Only recommended for trees without hard links.
    /// Hashing: the check that detects a file that changes while it is read always uses size and last write time of the open file. The difference is what is stored with a stable hash:
    /// with false the size and last write time of the open file (they belong to the hash), with true the values of the directory listing (so that the next refresh,
    /// which compares the listing with the database, sees the file as unchanged), even if the file changed between the listing and the hashing.
    /// </summary>
    public bool TrustDirectoryMetadata { get; set; }

    /// <summary>
    /// Number of files that are hashed at the same time by a scan job (1 - 64).
    /// 1 (the default, also used when the element is missing in the project file): the scan reads and hashes one file after the other, with no extra threads.
    /// That is the right value for a single hard disk, which reads slower when several files are read in parallel.
    /// More than 1: the scan runs as a pipeline of one producer (directory listing, metadata, resume filter), this many hash workers and one database writer,
    /// connected by bounded queues (memory stays flat for any tree size). Raise it for SSD or NVMe storage, and possibly for network shares, where SHA-512 on one core
    /// or the latency of single reads is the limit. The gain depends on the storage and must be measured on it; it ends when the database writer or the disk is the limit.
    /// The stored rows are the same for every value, only the order in which they reach the database can differ. Only scan jobs use it (refresh and check do not).
    /// </summary>
    public int HashWorkers { get; set; } = 1;

    /// <summary>
    /// Highest accepted value for <see cref="HashWorkers"/>.
    /// </summary>
    public const int MaxHashWorkers = 64;

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

        //Rule: HashWorkers must be 1 - 64
        if (dto.HashWorkers < 1 || dto.HashWorkers > MaxHashWorkers)
        {
            throw new ParameterException($"Param HashWorkers must be between 1 and {MaxHashWorkers}! {dto.HashWorkers}");
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
