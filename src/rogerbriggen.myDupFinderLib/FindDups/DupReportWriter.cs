// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>The report file already exists; it was not touched and nothing was written.</summary>
internal sealed class ReportAlreadyExistsException : IOException
{
    public ReportAlreadyExistsException(string reportFile, Exception inner)
        : base($"The report file already exists: {reportFile}", inner) => ReportFile = reportFile;

    public string ReportFile { get; }
}

/// <summary>Writing failed after the report file was created by this run: a partial report file may have been left.</summary>
internal sealed class ReportWriteFailedException : IOException
{
    public ReportWriteFailedException(string reportFile, Exception inner)
        : base($"Writing the report file failed, a partial file may have been left: {reportFile}", inner) => ReportFile = reportFile;

    public string ReportFile { get; }
}

internal static class DupReportWriter
{
    public const string HeaderLine = "FilenameAndPath;FileSize;FileSha512Hash;Category;Source;GroupId";

    public static void Write(string reportPathAndName, IEnumerable<DupReportRow> rows)
    {
        // Canonical sort order documented in README. Centralized here so every runner
        // produces a report in the same order without each having to remember to sort.
        var ordered = rows
            .OrderBy(r => r.Category)
            .ThenBy(r => r.GroupId)
            .ThenBy(r => r.Source)
            .ThenBy(r => r.Item.FilenameAndPath, System.StringComparer.OrdinalIgnoreCase);

        // CreateNew: an existing report is never overwritten or truncated. The open fails before anything is written.
        // Only the failure of the open itself, with the "file exists" error code, means "already exists" (decided here, at the open, not by looking at the file afterwards).
        FileStream stream;
        try
        {
            stream = new FileStream(reportPathAndName, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException ex) when (IsFileExists(ex))
        {
            throw new ReportAlreadyExistsException(reportPathAndName, ex);
        }

        // From here on the file was created by this call: every failure may leave a partial file (never deleted automatically)
        try
        {
            using var fs = new StreamWriter(stream);
            fs.WriteLine(HeaderLine);
            foreach (var row in ordered)
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
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            throw new ReportWriteFailedException(reportPathAndName, ex);
        }
    }

    /// <summary>
    /// True when the IOException of a <c>FileMode.CreateNew</c> open says that the file exists. On Windows .NET reports the Win32 error as HRESULT
    /// 0x80070050 (ERROR_FILE_EXISTS, 80) or 0x800700B7 (ERROR_ALREADY_EXISTS, 183); on Unix the raw errno EEXIST (17).
    /// </summary>
    internal static bool IsFileExists(IOException ex)
    {
        const int ErrorFileExists = 80;
        const int ErrorAlreadyExists = 183;
        const int Eexist = 17;
        if (OperatingSystem.IsWindows())
        {
            int code = ex.HResult & 0xFFFF;
            return (ex.HResult & 0x7FFF0000) == 0x00070000 && (code == ErrorFileExists || code == ErrorAlreadyExists);
        }
        return ex.HResult == Eexist;
    }

    /// <summary>
    /// Returns the file path relative to the item's PathBase. Used to align
    /// the same logical file across two databases when their roots differ
    /// (e.g. C:\backup vs D:\original).
    /// <para>
    /// The match is anchored at a directory boundary, so PathBase = <c>C:\data</c>
    /// does NOT match FilenameAndPath = <c>C:\database\file.txt</c> (sibling, not
    /// child). Falls back to the full path when the file is not actually under
    /// PathBase or PathBase is empty.
    /// </para>
    /// </summary>
    public static string GetRelativePath(ScanItemDto item)
    {
        var fullPath = item.FilenameAndPath ?? string.Empty;
        var basePath = item.PathBase ?? string.Empty;
        if (basePath.Length == 0)
        {
            return fullPath;
        }
        if (!fullPath.StartsWith(basePath, System.StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }
        // Require a directory boundary either at the end of basePath or at the next char of fullPath.
        var lastBaseChar = basePath[basePath.Length - 1];
        if (lastBaseChar == '/' || lastBaseChar == '\\')
        {
            return fullPath.Substring(basePath.Length).TrimStart('/', '\\');
        }
        if (fullPath.Length == basePath.Length)
        {
            return string.Empty;
        }
        var next = fullPath[basePath.Length];
        if (next == '/' || next == '\\')
        {
            return fullPath.Substring(basePath.Length).TrimStart('/', '\\');
        }
        // Prefix match without a directory boundary -> sibling, not child.
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
