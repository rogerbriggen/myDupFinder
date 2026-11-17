// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

/// <summary>
/// Writing failed after the report file was created by this run. The partial file was renamed to <see cref="FailedFile"/> (see <see cref="DupReportWriter.FailedSuffix"/>)
/// so that it does not block the next run; <see cref="FailedFile"/> is null when that rename failed too, then the partial file is still at <see cref="ReportFile"/>.
/// </summary>
internal sealed class ReportWriteFailedException : IOException
{
    public ReportWriteFailedException(string reportFile, string? failedFile, Exception inner)
        : base(failedFile is null
            ? $"Writing the report file failed, a partial file was left (it could not be renamed): {reportFile}"
            : $"Writing the report file failed, the partial file was renamed to {failedFile}", inner)
    {
        ReportFile = reportFile;
        FailedFile = failedFile;
    }

    public string ReportFile { get; }

    public string? FailedFile { get; }
}

/// <summary>
/// The run was cancelled while the report was written, after the report file was created by this run. Like <see cref="ReportWriteFailedException"/>: the partial file is at
/// <see cref="FailedFile"/> (null when the rename failed, then it is still at <see cref="ReportFile"/>).
/// </summary>
internal sealed class ReportWriteCancelledException : OperationCanceledException
{
    public ReportWriteCancelledException(string reportFile, string? failedFile, CancellationToken token)
        : base($"The run was cancelled while the report file was written: {reportFile}", token)
    {
        ReportFile = reportFile;
        FailedFile = failedFile;
    }

    public string ReportFile { get; }

    public string? FailedFile { get; }
}

/// <summary>Log text that both find-dups runners use.</summary>
internal static class FindDupsReportLog
{
    /// <summary>Says where the partial report is (renamed to .failed, or still at its name when the rename failed).</summary>
    public static string PartialReportText(string reportFile, string? failedFile) =>
        failedFile is null
            ? $"A partial report file may have been left at {reportFile} (it could not be renamed to {reportFile}{DupReportWriter.FailedSuffix}), remove or rename it before the job is run again."
            : $"The partial report was kept and renamed to {failedFile}.";
}

internal static class DupReportWriter
{
    public const string HeaderLine = "FilenameAndPath;FileSize;FileSha512Hash;Category;Source;GroupId";

    /// <summary>Appended to the report file name when a run fails or is cancelled after the file was created: the partial file is kept under this name.</summary>
    public const string FailedSuffix = ".failed";

    /// <summary>The token is looked at after this many rows.</summary>
    internal const int CancelCheckInterval = 4096;

    /// <param name="token">Looked at every <see cref="CancelCheckInterval"/> rows. A cancel after the file was created throws <see cref="ReportWriteCancelledException"/> (before: <see cref="OperationCanceledException"/>).</param>
    public static void Write(string reportPathAndName, IEnumerable<DupReportRow> rows, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

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

        // From here on the file was created by this call: every failure leaves a partial file, which is kept but renamed (see MoveToFailed)
        try
        {
            using var fs = new StreamWriter(stream);
            fs.WriteLine(HeaderLine);
            int written = 0;
            foreach (var row in ordered)
            {
                if (++written % CancelCheckInterval == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
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
            // A cancel during the sort or after the last periodic check is seen here, still inside the block whose failure keeps the file as .failed
            fs.Flush();
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The stream is closed here (the using ended with the try block)
            throw new ReportWriteCancelledException(reportPathAndName, MoveToFailed(reportPathAndName), token);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            throw new ReportWriteFailedException(reportPathAndName, MoveToFailed(reportPathAndName), ex);
        }
    }

    /// <summary>
    /// Renames the partial report to <c>&lt;report&gt;.failed</c>, replacing an older file of that name. The partial report is kept (it may help to see how far the run came)
    /// but must not block the next run, which refuses to start while the report exists. Returns the new path, or null when the rename failed (the partial file then stays where it is).
    /// </summary>
    internal static string? MoveToFailed(string reportPathAndName)
    {
        string failedPath = reportPathAndName + FailedSuffix;
        try
        {
            File.Move(reportPathAndName, failedPath, overwrite: true);
            return failedPath;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            return null;
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
