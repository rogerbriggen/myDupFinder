// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Serilog;

namespace RogerBriggen.MyDupFinder;

class Program
{
    /// <summary>
    /// Exit codes: 0 = everything finished, 1 = a job failed, or the arguments, the project file or the sanity check were wrong, or an exception reached Main, 2 = a job was cancelled by the user.
    /// </summary>
    static int Main(string[] args)
    {
        System.Threading.Thread.CurrentThread.Name = "MainThread";

        // Last resort: whatever is unhandled on any thread ends up in the log before the process ends
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            return Run(args);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            LogFatal(ex);
            return JobChain.ExitCodeFailed;
        }
        finally
        {
            // Flush the log in every path (the ProcessExit handler does it too, doing it twice is harmless)
            Log.CloseAndFlush();
        }
    }

    private static int Run(string[] args)
    {
        // Setup log with DI
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var logger = serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<Program>>();
        if (logger == null)
        {
            throw new Exception("Could not get a logger");
        }
        var assembly = typeof(Program).Assembly;
        logger.LogInformation($"Application with version {assembly.GetName().Version}  ({ThisAssembly.AssemblyInformationalVersion}) started...");

        int exitCode = JobChain.ExitCodeFinished;

        //Do stuff
        //Scan commandline
        if (args.Length == 2)
        {
            CultureInfo ci = new CultureInfo("");
            if (args[0].ToLower(ci) == "exampleproject")
            {
                try
                {
                    MyDupFinderProjectDTO dto;
                    MyDupFinderProject.getExampleDTO(out dto);
                    MyDupFinderProject.WriteConfigurationToFile(dto, args[1]);
                    logger.LogInformation($"Example project file succesfully written! {args[1]}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Example project file could not be written! {args[1]}");
                    exitCode = JobChain.ExitCodeFailed;
                }

            }
            else if (args[0].ToLower(ci) == "dryrun")
            {
                try
                {
                    MyDupFinderProjectDTO? dto;
                    MyDupFinderProject.ReadConfigurationFromFile(args[1], out dto);
                    logger.LogInformation($"Example project file succesfully read! {args[1]}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Example project file could not be read! {args[1]}");
                    exitCode = JobChain.ExitCodeFailed;
                }

            }
            else if (args[0].ToLower(ci) == "run")
            {
                exitCode = RunProject(logger, serviceProvider, args[1]);
            }
            else if (args[0].ToLower(ci) == "applycheck")
            {
                try
                {
                    var applier = serviceProvider.GetService<CheckReportApplier>();
                    if (applier is null)
                    {
                        logger.LogError("No CheckReportApplier registered!");
                        exitCode = JobChain.ExitCodeFailed;
                    }
                    else
                    {
                        applier.Apply(args[1]);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Exception during applyCheck...! {args[1]}");
                    exitCode = JobChain.ExitCodeFailed;
                }
            }
            else
            {
                ShowHelp();
                exitCode = JobChain.ExitCodeFailed;
            }
        }
        else
        {
            ShowHelp();
            exitCode = JobChain.ExitCodeFailed;
        }

        // Finish log
        logger.LogInformation("Application closing... (exit code {ExitCode})", exitCode);

        // Use for Microsoft logger.... for serilog, we do it with AppDomain.CurrentDomain.ProcessExit
        // serviceProvider.Dispose();
        return exitCode;
    }

    /// <summary>
    /// Runs the jobs of the project file in order and returns the exit code (see <see cref="JobChain"/>): a job that fails or is cancelled stops the project.
    /// </summary>
    private static int RunProject(ILogger<Program> logger, IServiceProvider serviceProvider, string projectFile)
    {
        try
        {
            MyDupFinderProjectDTO? dto;
            MyDupFinderProject.ReadConfigurationFromFile(projectFile, out dto);
            if (dto is null)
            {
                logger.LogError("Could not read Project file");
                return JobChain.ExitCodeFailed;
            }

            //Check and fix config
            MyDupFinderProjectDTO.CheckSanity(dto);
            MyDupFinderProjectDTO.FixDto(dto);

            //Jobs in execution order: scan, refresh (they change databases), then find dups, check (they only read them)
            var jobs = new List<ChainedJob>();
            foreach (ProjectJob job in dto.GetJobsInExecutionOrder())
            {
                switch (job.Dto)
                {
                    case MyDupFinderScanJobDTO scanDto:
                        jobs.Add(new ChainedJob(scanDto.JobName, () => RunJob<ScanService>(logger, serviceProvider, scanDto.JobName, "ScanService", s => s.StartScan(scanDto))));
                        break;

                    case MyDupFinderRefreshJobDTO refreshDto:
                        jobs.Add(new ChainedJob(refreshDto.JobName, () => RunJob<RefreshService>(logger, serviceProvider, refreshDto.JobName, "RefreshService", s => s.StartRefresh(refreshDto))));
                        break;

                    case MyDupFinderFindDupsJobDTO findDupsDto:
                        jobs.Add(new ChainedJob(findDupsDto.JobName, () => RunJob<FindDupsService>(logger, serviceProvider, findDupsDto.JobName, "FindDupsService", s => s.Start(findDupsDto))));
                        break;

                    case MyDupFinderCheckJobDTO checkDto:
                        jobs.Add(new ChainedJob(checkDto.ScanJobDTO.JobName, () => RunJob<CheckService>(logger, serviceProvider, checkDto.ScanJobDTO.JobName, "CheckService", s => s.StartCheck(checkDto))));
                        break;
                }
            }

            return JobChain.Run(jobs, logger).ExitCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Exception during run of the job...! {projectFile}");
            return JobChain.ExitCodeFailed;
        }
    }

    /// <summary>
    /// Runs one job on its own thread and waits for it (or for Enter, which cancels it). Returns the final state of the service; a service that does not exist or
    /// ends in any other state than finished or cancelled is failed. The worker thread is always joined before the service is disposed.
    /// </summary>
    private static IService.EServiceState RunJob<TService>(ILogger<Program> logger, IServiceProvider serviceProvider, string jobName, string threadName, Action<TService> start)
        where TService : BasicService<TService>
    {
        using var service = serviceProvider.GetService<TService>();
        if (service is null)
        {
            logger.LogError("No {Service} registered!", typeof(TService).Name);
            return IService.EServiceState.failed;
        }
        logger.LogInformation("Running Job {JobName}...", jobName);
        var t = new Thread(() =>
        {
            try
            {
                start(service);
            }
            catch (Exception ex)
            {
                // The services handle their errors themselves; this is the last line before the process would be terminated by an unhandled exception on this thread
                logger.LogError(ex, "Job {JobName} ended with an unexpected exception.", jobName);
            }
        });
        t.Name = threadName;
        t.Start();
        try
        {
            //Wait for t or for a key press
            WaitForThreadOrKeyPress(logger, service, t);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Waiting for Job {JobName} failed. The job is stopped.", jobName);
            service.Stop();
        }
        finally
        {
            // Whatever happened above: the service must not be disposed while its thread still works with it
            t.Join();
        }

        return service.ServiceState switch
        {
            IService.EServiceState.finished => IService.EServiceState.finished,
            IService.EServiceState.cancelled => IService.EServiceState.cancelled,
            _ => IService.EServiceState.failed,
        };
    }

    private static bool s_keyboardNoticeLogged;

    private static void WaitForThreadOrKeyPress(ILogger<Program> logger, IService service, Thread t)
    {
        // With redirected input (a pipe, a service, a scheduled task) there is no keyboard to look at: Console.KeyAvailable throws there
        bool keyboard = !Console.IsInputRedirected;
        if (!keyboard && !s_keyboardNoticeLogged)
        {
            s_keyboardNoticeLogged = true;
            logger.LogInformation("The input is redirected: cancelling with Enter is not available.");
        }
        //Wait for the thread; look at the keyboard in between
        while (!t.Join(100))
        {
            if (!keyboard)
            {
                continue;
            }
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey();
                    if (key.Key == ConsoleKey.Enter)
                    {
                        //Cancel...
                        service.Stop();
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                // No console to read from: go on without the keyboard, the job keeps running
                keyboard = false;
                logger.LogInformation(ex, "The keyboard cannot be read: cancelling with Enter is not available.");
            }
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogFatal(e.ExceptionObject);
        Log.CloseAndFlush();
    }

    private static void LogFatal(object exceptionObject)
    {
        try
        {
            if (exceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled exception, the application ends.");
            }
            else
            {
                Log.Fatal("Unhandled exception, the application ends: {Exception}", exceptionObject);
            }
        }
        catch (Exception)
        {
            // The log itself is broken: nothing more to do, the console is the last resort below
        }
        Console.Error.WriteLine($"Unhandled exception, the application ends: {exceptionObject}");
    }

    private static void ShowHelp()
    {
        System.Console.WriteLine("exampleproject [projectfile.xml]");
        System.Console.WriteLine("run [projectfile.xml]");
        System.Console.WriteLine("dryrun [projectfile.xml]");
        System.Console.WriteLine("applyCheck [reportfile.csv]");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // we will configure logging here
        // Microsoft logger
        /*
        services.AddLogging(configure => configure.AddConsole(consoleLoggerOptions => consoleLoggerOptions.TimestampFormat = "[dd.MM.yyyy HH:mm:ss.fff]")).Configure<LoggerFilterOptions>(cfg => cfg.MinLevel = LogLevel.Debug).AddTransient<Program>();
        services.AddLogging(configure => configure.AddDebug()).Configure<LoggerFilterOptions>(cfg => cfg.MinLevel = LogLevel.Debug).AddTransient<Program>();
        */

        // Setup Serilog
        if (File.Exists("appsettings.json"))
        {
            // Serilog logger from file
            var configuration = new ConfigurationBuilder()
                                    .AddJsonFile("appsettings.json")
                                    .Build();
            services.AddSerilogServices(new LoggerConfiguration()
                                         .ReadFrom.Configuration(configuration));

            services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

            // Get application stuff
            // var RemoteIncrediBuildSec = configuration.GetSection("RemoteIncrediBuild");
            /*
            if (RemoteIncrediBuildSec != null)
            {
                bool? bIsServer = RemoteIncrediBuildSec.GetValue<bool?>("IsServer");
                if (bIsServer == true)
                {
                    ConfigFileAppSettings.Action = AppSettings.EAction.Server;
                }
                else if (bIsServer == false)
                {
                    ConfigFileAppSettings.Action = AppSettings.EAction.Client;
                }

            }
            */
        }
        else
        {
            // Serilog logger from code
            string outputTemplate = "[{Timestamp:dd.MM.yyyy HH:mm:ss.ffff} {Level:u3}] {Message:lj} {Properties}[{ThreadId} {ThreadName}]{NewLine}{Exception}";
            services.AddSerilogServices(new LoggerConfiguration()
                                             .Enrich.WithThreadId()
                                             .Enrich.WithThreadName()
                                             .MinimumLevel.Debug()
                                             .WriteTo.Console(outputTemplate: outputTemplate, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
                                             .WriteTo.Async(w => w.File("MyLog.log", rollingInterval: RollingInterval.Day, outputTemplate: outputTemplate))
                                             .WriteTo.Debug(outputTemplate: outputTemplate));

            services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));
        }
        services.AddTransient<ScanService>();
        services.AddTransient<FindDupsService>();
        services.AddTransient<RefreshService>();
        services.AddTransient<CheckService>();
        services.AddTransient<CheckReportApplier>();


        Log.Information("**** Opening log... ****");
    }
}

#pragma warning disable SA1402 // File may only contain a single type
public static class MyExtensions
#pragma warning restore SA1402 // File may only contain a single type
{
    public static IServiceCollection AddSerilogServices(this IServiceCollection services, LoggerConfiguration configuration)
    {
        Contract.Requires(configuration != null);
        Log.Logger = configuration!.CreateLogger();
        AppDomain.CurrentDomain.ProcessExit += (s, e) => { Log.Information("**** Closing log... ****"); Log.CloseAndFlush(); };
        return services.AddSingleton(Log.Logger);
    }
}
