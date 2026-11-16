// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The SQLite settings a benchmark runs with, from the environment variable MYDUPFINDER_BENCHMARK_SQLITE (see docs/benchmark.md): a comma separated list of
/// <c>normal</c> (DatabaseSynchronous=Normal), <c>cache=n</c> (PRAGMA cache_size, n in MB) and <c>temp=memory</c> (PRAGMA temp_store=MEMORY). Not set: the defaults.
/// The pragmas are applied through <see cref="MeasurementPragmas"/>, which is process wide: benchmarks are opt-in and run alone, and every benchmark class
/// resets it in Dispose.
/// </summary>
internal static class BenchmarkSqliteSettings
{
    public const string EnvironmentVariable = "MYDUPFINDER_BENCHMARK_SQLITE";

    private static IReadOnlyList<string> Tokens => (Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.ToLowerInvariant())
        .ToList();

    /// <summary>The value for the DatabaseSynchronous job setting (the real production path for scan, refresh and check).</summary>
    public static DatabaseSynchronous Synchronous => Tokens.Contains("normal") ? DatabaseSynchronous.Normal : DatabaseSynchronous.Full;

    /// <summary>The text for the Mode line of a benchmark.</summary>
    public static string Description => Tokens.Count == 0 ? "SQLite defaults" : "SQLite settings: " + string.Join(", ", Tokens);

    /// <summary>
    /// Sets the pragmas for the contexts that are created from now on. <paramref name="includeSynchronous"/> adds <c>PRAGMA synchronous=NORMAL</c> as a plain pragma
    /// (for code that has no job setting, applyCheck: this is a measurement only, the code itself always uses Full).
    /// </summary>
    public static void Apply(bool includeSynchronous = false)
    {
        var pragmas = new List<string>();
        foreach (string token in Tokens)
        {
            if (token == "normal")
            {
                if (includeSynchronous)
                {
                    pragmas.Add("PRAGMA synchronous=NORMAL;");
                }
            }
            else if (token.StartsWith("cache=", StringComparison.Ordinal) && int.TryParse(token.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int megabytes))
            {
                // Negative: KiB
                pragmas.Add(string.Create(CultureInfo.InvariantCulture, $"PRAGMA cache_size=-{megabytes * 1024};"));
            }
            else if (token == "temp=memory")
            {
                pragmas.Add("PRAGMA temp_store=MEMORY;");
            }
            else
            {
                throw new ArgumentException($"Unknown {EnvironmentVariable} entry '{token}', expected normal, cache=<MB> or temp=memory");
            }
        }
        MeasurementPragmas.Current = pragmas;
    }

    public static void Reset() => MeasurementPragmas.Current = Array.Empty<string>();
}
