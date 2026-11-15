// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderData;

public enum EProjectJobType
{
    Scan,
    Refresh,
    FindDups,
    Check
}

/// <summary>
/// One job of a project in execution order, see <see cref="MyDupFinderProjectDTO.GetJobsInExecutionOrder"/>.
/// <see cref="Dto"/> is the <see cref="MyDupFinderScanJobDTO"/>, <see cref="MyDupFinderRefreshJobDTO"/>,
/// <see cref="MyDupFinderFindDupsJobDTO"/> or <see cref="MyDupFinderCheckJobDTO"/> that belongs to <see cref="JobType"/>.
/// </summary>
public sealed record ProjectJob(EProjectJobType JobType, string JobName, object Dto);
