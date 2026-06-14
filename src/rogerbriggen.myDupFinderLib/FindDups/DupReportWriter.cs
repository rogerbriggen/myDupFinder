// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Generic;
using System.IO;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib;

internal enum DupReportCategory
{
    Duplicate,
    Moved,
    Unique,
    Missing,
    New,
    Changed,
}

internal enum DupReportSource
{
    Base,
    Second,
}

/// <summary>
/// One row in the duplicate report.
/// <para>
/// Rows that describe the same logical file across DBs (the two sides of a
/// Duplicate / Changed / Moved pair, or every copy in a same-DB duplicate group)
/// share the same <see cref="GroupId"/> so a UI can collapse, expand, or
/// otherwise relate them. Singleton categories (Missing, New, Unique) each get
/// their own GroupId.
/// </para>
/// </summary>
internal sealed record DupReportRow(ScanItemDto Item, DupReportCategory Category, DupReportSource Source, int GroupId);

internal static class DupReportWriter
{
    public const string HeaderLine = "FilenameAndPath;FileSize;FileSha512Hash;Category;Source;GroupId";

    public static void Write(string reportPathAndName, IEnumerable<DupReportRow> rows)
    {
        using var fs = new StreamWriter(reportPathAndName);
        fs.WriteLine(HeaderLine);
        foreach (var row in rows)
        {
            fs.Write('"');
            fs.Write(EscapeForCsvQuoted(row.Item.FilenameAndPath));
            fs.Write("\";");
            fs.Write(row.Item.FileSize);
            fs.Write(';');
            fs.Write(row.Item.FileSha512Hash);
            fs.Write(';');
            fs.Write(row.Category);
            fs.Write(';');
            fs.Write(row.Source);
            fs.Write(';');
            fs.WriteLine(row.GroupId);
        }
    }

    /// <summary>
    /// Returns the file path relative to the item's PathBase. Used to align
    /// the same logical file across two databases when their roots differ
    /// (e.g. C:\backup vs D:\original).
    /// </summary>
    public static string GetRelativePath(ScanItemDto item)
    {
        var fullPath = item.FilenameAndPath ?? string.Empty;
        var basePath = item.PathBase ?? string.Empty;
        if (basePath.Length > 0 && fullPath.StartsWith(basePath, System.StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.Substring(basePath.Length).TrimStart('/', '\\');
        }
        return fullPath;
    }

    private static string EscapeForCsvQuoted(string? value) =>
        value is null ? string.Empty : value.Replace("\"", "\"\"");
}

/// <summary>
/// Hands out monotonically increasing GroupIds so paired rows can share one
/// and every singleton (Missing / New / Unique) gets a fresh one.
/// </summary>
internal sealed class GroupIdGenerator
{
    private int _next;
    public int Next() => ++_next;
}
