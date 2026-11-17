// Roger Briggen license this file to you under the MIT license.
//

using System;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>What a job does after the volume identity of its base path was compared with the recorded one.</summary>
internal enum VolumeIdentityDecision
{
    /// <summary>Go on, record nothing (the identity cannot be determined and nothing was recorded before).</summary>
    Proceed,

    /// <summary>Go on and record the current identity (and the verification time).</summary>
    ProceedRecord,

    /// <summary>Do not run: the volume is not the recorded one, or cannot be verified. The job fails and writes nothing.</summary>
    Refuse,
}

/// <summary>
/// The policy that scan, refresh and check share to make sure the base path still lies on the volume that was scanned: an unmounted volume (the mount point is an empty directory)
/// or another disk at the same drive letter or mount point must not look like "all files are gone" or "all files are new".
/// The base path is the job's BasePath after FixDto (with trailing delimiter), the same string the rows use as PathBase.
/// </summary>
internal static class VolumeIdentityGuard
{
    /// <summary>
    /// Compares the identity of the volume of <paramref name="basePath"/> with the recorded one and logs the outcome.
    /// With <paramref name="readOnly"/> (check) nothing is recorded or updated. Returns false when the job has to refuse to run.
    /// </summary>
    public static bool Verify(IVolumeIdentityProvider provider, ScanJobDBInserts db, string originComputer, string scanName, string basePath, bool acceptVolumeChange, bool readOnly, ILogger logger)
    {
        VolumeIdentityResult current = provider.GetIdentity(basePath);
        var stored = db.GetScanVolume(originComputer, scanName, basePath);
        VolumeIdentityDecision decision = Decide(current, stored?.VolumeIdentity, acceptVolumeChange, basePath, logger, () => db.HasRowsForBasePath(basePath), readOnly);
        if (decision == VolumeIdentityDecision.ProceedRecord && !readOnly)
        {
            db.SaveScanVolume(originComputer, scanName, basePath, current.Identity!, DateTime.UtcNow);
        }
        return decision != VolumeIdentityDecision.Refuse;
    }

    /// <summary>
    /// The policy itself, without the database: what to do for the current result and the stored identity (null: none recorded). Logs the reason.
    /// </summary>
    internal static VolumeIdentityDecision Decide(VolumeIdentityResult current, string? storedIdentity, bool acceptVolumeChange, string basePath, ILogger logger, Func<bool> hasRowsForBasePath, bool readOnly = false)
    {
        string storedText = storedIdentity ?? "none recorded";
        switch (current.Status)
        {
            case VolumeIdentityStatus.PathUnavailable:
                logger.LogError("Volume identity check: the base path {basePath} is not available ({detail}); the volume may not be mounted. Recorded identity: {stored}. The job was not run and nothing was changed.", basePath, current.Detail, storedText);
                return VolumeIdentityDecision.Refuse;

            case VolumeIdentityStatus.NotSupported:
                if (storedIdentity is null)
                {
                    logger.LogWarning("Volume identity check: the identity of the volume of base path {basePath} cannot be determined on this system ({detail}); the volume is not verified and nothing is recorded.", basePath, current.Detail);
                    return VolumeIdentityDecision.Proceed;
                }
                if (acceptVolumeChange)
                {
                    logger.LogWarning("Volume identity check: the identity of the volume of base path {basePath} cannot be determined ({detail}); recorded identity: {stored}. AcceptVolumeChange is set, so the job runs unverified.", basePath, current.Detail, storedIdentity);
                    return VolumeIdentityDecision.Proceed;
                }
                logger.LogError("Volume identity check: the identity of the volume of base path {basePath} cannot be determined ({detail}), but {stored} was recorded, so the volume cannot be verified. The job was not run and nothing was changed. If this is intended, set AcceptVolumeChange to true on the job for one run.", basePath, current.Detail, storedIdentity);
                return VolumeIdentityDecision.Refuse;

            default:
                break;
        }

        string identity = current.Identity ?? string.Empty;
        if (storedIdentity is null)
        {
            if (hasRowsForBasePath())
            {
                logger.LogWarning("Volume identity check: the volume identity of base path {basePath} was not recorded before (an old database or a first run); {current} {action}. This run could not be verified.", basePath, identity, readOnly ? "was found (not recorded by a check)" : "is recorded now");
            }
            else
            {
                logger.LogInformation("Volume identity check: base path {basePath} is on volume {current}{action}", basePath, identity, readOnly ? "." : ", recorded.");
            }
            return VolumeIdentityDecision.ProceedRecord;
        }
        if (string.Equals(storedIdentity, identity, StringComparison.Ordinal))
        {
            logger.LogInformation("Volume identity check: base path {basePath} is on the recorded volume {current}.", basePath, identity);
            return VolumeIdentityDecision.ProceedRecord;
        }
        if (acceptVolumeChange)
        {
            logger.LogWarning("Volume identity check: the volume of base path {basePath} changed from {stored} to {current}. AcceptVolumeChange is set, so the new identity is accepted{action}.", basePath, storedIdentity, identity, readOnly ? " for this check (not recorded)" : " and recorded");
            return VolumeIdentityDecision.ProceedRecord;
        }
        logger.LogError("Volume identity check: the volume of base path {basePath} is not the one that was scanned. Recorded: {stored}, now: {current}. The job was not run and nothing was changed. If the volume really changed on purpose (disk cloned, replaced or reformatted), set AcceptVolumeChange to true on the job (scan or refresh) for one run.", basePath, storedIdentity, identity);
        return VolumeIdentityDecision.Refuse;
    }
}
