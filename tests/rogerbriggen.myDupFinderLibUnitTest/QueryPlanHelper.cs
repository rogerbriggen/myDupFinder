// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>A SELECT that EF Core really sent to the database, with its parameters.</summary>
internal sealed record CapturedCommand(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);

/// <summary>
/// Remembers the SELECT commands a context sends, so the exact SQL of the production code can be looked at.
/// </summary>
internal sealed class SqlCaptureInterceptor : DbCommandInterceptor
{
    private readonly List<CapturedCommand> _commands = new List<CapturedCommand>();

    public IReadOnlyList<CapturedCommand> Commands => _commands;

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Capture(command);
        return result;
    }

    private void Capture(DbCommand command)
    {
        if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var parameters = command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, (object?)p.Value)).ToList();
        _commands.Add(new CapturedCommand(command.CommandText, parameters));
    }
}

/// <summary>
/// Runs production queries on a database and returns the SQLite query plan (EXPLAIN QUERY PLAN) of the SQL EF Core generated for them.
/// </summary>
internal static class QueryPlanHelper
{
    /// <summary>Creates a context that records the SELECT commands it sends into <paramref name="capture"/>.</summary>
    public static DubFinderContext CreateCapturingContext(string databaseFile, SqlCaptureInterceptor capture)
    {
        var options = new DbContextOptionsBuilder<DubFinderContext>()
            .UseSqlite($"Data Source={databaseFile}")
            .AddInterceptors(capture)
            .Options;
        return new DubFinderContext(options);
    }

    /// <summary>
    /// Runs <paramref name="query"/> (the real production query, against a capturing context) and returns the plan of the
    /// last SELECT it sent to the database whose SQL contains <paramref name="sqlMustContain"/>.
    /// </summary>
    public static string Explain(string databaseFile, Action<DubFinderContext> query, string? sqlMustContain = null)
    {
        var capture = new SqlCaptureInterceptor();
        using (var context = CreateCapturingContext(databaseFile, capture))
        {
            query(context);
        }
        var command = capture.Commands.LastOrDefault(c => sqlMustContain is null || c.Sql.Contains(sqlMustContain, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The query sent no matching SELECT command.");
        return ExplainSql(databaseFile, command);
    }

    /// <summary>Same as <see cref="Explain"/>, but for a query that works with two databases: both contexts record into the same list. The plan is of the last matching SELECT.</summary>
    public static string ExplainTwoDatabases(string databaseFile, string secondDatabaseFile, Action<DubFinderContext, DubFinderContext> query, string? sqlMustContain = null)
    {
        var capture = new SqlCaptureInterceptor();
        using (var context = CreateCapturingContext(databaseFile, capture))
        using (var second = CreateCapturingContext(secondDatabaseFile, capture))
        {
            query(context, second);
        }
        var command = capture.Commands.LastOrDefault(c => sqlMustContain is null || c.Sql.Contains(sqlMustContain, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The query sent no matching SELECT command.");
        return ExplainSql(databaseFile, command);
    }

    /// <summary>Returns the SQL (with parameters listed) and the plan, one string, for output.</summary>
    public static string Describe(string databaseFile, Action<DubFinderContext> query, string? sqlMustContain = null)
    {
        var capture = new SqlCaptureInterceptor();
        using (var context = CreateCapturingContext(databaseFile, capture))
        {
            query(context);
        }
        var command = capture.Commands.LastOrDefault(c => sqlMustContain is null || c.Sql.Contains(sqlMustContain, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The query sent no matching SELECT command.");
        return command.Sql.Trim() + Environment.NewLine + ExplainSql(databaseFile, command);
    }

    /// <summary>Runs EXPLAIN QUERY PLAN for the command and returns the plan lines (the detail column), indented by their nesting.</summary>
    public static string ExplainSql(string databaseFile, CapturedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var connection = new SqliteConnection($"Data Source={databaseFile}");
        connection.Open();
        using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.Sql;
        foreach (var (name, value) in command.Parameters)
        {
            explain.Parameters.Add(new SqliteParameter(name, value ?? DBNull.Value));
        }

        var rows = new List<(int Id, int Parent, string Detail)>();
        using (var reader = explain.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(3)));
            }
        }

        var depth = new Dictionary<int, int> { [0] = -1 };
        var text = new StringBuilder();
        foreach (var (id, parent, detail) in rows)
        {
            int level = depth.TryGetValue(parent, out int parentLevel) ? parentLevel + 1 : 0;
            depth[id] = level;
            text.Append(' ', level * 2).AppendLine(detail);
        }
        return text.ToString().TrimEnd();
    }
}
