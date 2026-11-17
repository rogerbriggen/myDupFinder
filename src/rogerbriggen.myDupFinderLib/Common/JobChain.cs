// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// One job of a project, for <see cref="JobChain.Run"/>.
/// </summary>
/// <param name="JobName">The name of the job, for the log.</param>
/// <param name="Run">Runs the job (to its end) and returns its final state. Any state other than finished and cancelled counts as failed.</param>
public sealed record ChainedJob(string JobName, Func<IService.EServiceState> Run);

/// <summary>
/// The result of <see cref="JobChain.Run"/>.
/// </summary>
/// <param name="ExitCode">See <see cref="JobChain.ExitCodeFinished"/>, <see cref="JobChain.ExitCodeFailed"/> and <see cref="JobChain.ExitCodeCancelled"/>.</param>
/// <param name="JobsStarted">How many jobs were started (the one that stopped the chain included).</param>
/// <param name="JobsNotRun">How many jobs were not started because an earlier one did not finish.</param>
/// <param name="StoppedByJob">The name of the job that did not finish, null when all jobs finished.</param>
public sealed record JobChainResult(int ExitCode, int JobsStarted, int JobsNotRun, string? StoppedByJob);

/// <summary>
/// Runs the jobs of a project one after the other and stops after the first job that does not finish. The later jobs of a project work on the result of the earlier
/// ones (a scan, then a find-duplicates run over the database it filled), so going on after a failed or cancelled job would produce results that look complete and are not.
/// </summary>
public static class JobChain
{
    /// <summary>Every job finished.</summary>
    public const int ExitCodeFinished = 0;

    /// <summary>A job failed (or something else went wrong: the arguments, the project file, an exception).</summary>
    public const int ExitCodeFailed = 1;

    /// <summary>The user cancelled a job.</summary>
    public const int ExitCodeCancelled = 2;

    /// <summary>
    /// Runs the jobs in the given order. A job that throws counts as failed (nothing escapes from here).
    /// </summary>
    public static JobChainResult Run(IReadOnlyList<ChainedJob> jobs, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(logger);

        for (int i = 0; i < jobs.Count; i++)
        {
            ChainedJob job = jobs[i];
            IService.EServiceState state;
            try
            {
                state = job.Run();
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                logger.LogError(ex, "Job {JobName} ended with an exception.", job.JobName);
                state = IService.EServiceState.failed;
            }

            switch (state)
            {
                case IService.EServiceState.finished:
                    logger.LogInformation("Job {JobName} finished!", job.JobName);
                    continue;
                case IService.EServiceState.cancelled:
                    logger.LogInformation("Job {JobName} cancelled!", job.JobName);
                    return Stopped(jobs, i, ExitCodeCancelled, logger);
                default:
                    logger.LogError("Job {JobName} failed!", job.JobName);
                    return Stopped(jobs, i, ExitCodeFailed, logger);
            }
        }

        logger.LogInformation("All Jobs finished");
        return new JobChainResult(ExitCodeFinished, jobs.Count, 0, null);
    }

    private static JobChainResult Stopped(IReadOnlyList<ChainedJob> jobs, int index, int exitCode, ILogger logger)
    {
        int notRun = jobs.Count - index - 1;
        logger.LogWarning("The project was stopped by job {JobName}. {NotRun} of {Total} jobs were not run.", jobs[index].JobName, notRun, jobs.Count);
        return new JobChainResult(exitCode, index + 1, notRun, jobs[index].JobName);
    }
}
