// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// RFC 4180 CSV writer/reader for the check report. Header is written as `# Key=Value`
/// comment lines so applyCheck is self-contained.
/// </summary>
internal static class CheckReportCsv
{
    internal const string FileSignature = "myDupFinder check report";

    private static readonly string[] s_columns =
    {
        "Category",
        "PathMoved",
        "ScanItemId",
        "FilenameAndPath_DB",
        "FilenameAndPath_Disk",
        "PathBase_DB",
        "PathBase_Disk",
        "FileSize_DB",
        "FileSize_Disk",
        "MTime_DB",
        "MTime_Disk",
        "Hash_DB",
        "Hash_Disk",
        "CreationTime_Disk",
    };

    public static void WriteHeader(TextWriter writer, CheckReportHeader header)
    {
        writer.WriteLine(FormattableString.Invariant($"# {FileSignature} v{header.SchemaVersion}"));
        writer.WriteLine(FormattableString.Invariant($"# SchemaVersion={header.SchemaVersion}"));
        writer.WriteLine(FormattableString.Invariant($"# DatabaseFile={header.DatabaseFile}"));
        writer.WriteLine(FormattableString.Invariant($"# BasePath={header.BasePath}"));
        writer.WriteLine(FormattableString.Invariant($"# JobName={header.JobName}"));
        writer.WriteLine(FormattableString.Invariant($"# OriginComputer={header.OriginComputer}"));
        writer.WriteLine(FormattableString.Invariant($"# ScanName={header.ScanName}"));
        writer.WriteLine(FormattableString.Invariant($"# SkipHashCheck={(header.SkipHashCheck ? "true" : "false")}"));
        writer.WriteLine(FormattableString.Invariant($"# IgnoreBasePath={(header.IgnoreBasePath ? "true" : "false")}"));
        writer.WriteLine(FormattableString.Invariant($"# GeneratedUTC={header.GeneratedUTC.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)}"));
        writer.WriteLine(string.Join(",", s_columns));
    }

    public static void WriteRow(TextWriter writer, CheckReportRow row)
    {
        var sb = new StringBuilder();
        Append(sb, row.Category.ToString());
        Append(sb, row.PathMoved ? "true" : "false");
        Append(sb, row.ScanItemId == Guid.Empty ? string.Empty : row.ScanItemId.ToString("D", CultureInfo.InvariantCulture));
        Append(sb, row.FilenameAndPathDB);
        Append(sb, row.FilenameAndPathDisk);
        Append(sb, row.PathBaseDB);
        Append(sb, row.PathBaseDisk);
        Append(sb, row.FileSizeDB?.ToString(CultureInfo.InvariantCulture));
        Append(sb, row.FileSizeDisk?.ToString(CultureInfo.InvariantCulture));
        Append(sb, FormatDateTime(row.MTimeDB));
        Append(sb, FormatDateTime(row.MTimeDisk));
        Append(sb, row.HashDB);
        Append(sb, row.HashDisk);
        Append(sb, FormatDateTime(row.CreationTimeDisk), last: true);
        writer.WriteLine(sb.ToString());
    }

    public static (CheckReportHeader Header, IEnumerable<CheckReportRow> Rows) Read(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = new CheckReportHeader();
        int i = 0;
        bool signatureSeen = false;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                ParseHeaderLine(line, header, ref signatureSeen);
                continue;
            }
            break;
        }
        if (!signatureSeen)
        {
            throw new InvalidDataException("Not a myDupFinder check report (missing signature line).");
        }
        if (i >= lines.Length)
        {
            throw new InvalidDataException("Check report has no column-header line.");
        }
        // i now points at the column-header line
        ValidateColumnHeader(lines[i]);
        i++;
        var rows = new List<CheckReportRow>(Math.Max(0, lines.Length - i));
        for (; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i]))
            {
                continue;
            }
            rows.Add(ParseRow(lines[i]));
        }
        return (header, rows);
    }

    private static void ParseHeaderLine(string line, CheckReportHeader header, ref bool signatureSeen)
    {
        // Strip the leading "# " (or "#")
        var body = line.Length > 1 && line[1] == ' ' ? line.Substring(2) : line.Substring(1);
        if (body.StartsWith(FileSignature, StringComparison.Ordinal))
        {
            signatureSeen = true;
            return;
        }
        int eq = body.IndexOf('=');
        if (eq < 0)
        {
            return;
        }
        var key = body.Substring(0, eq);
        var value = body.Substring(eq + 1);
        switch (key)
        {
            case "SchemaVersion":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    header.SchemaVersion = v;
                }
                break;
            case "DatabaseFile":
                header.DatabaseFile = value;
                break;
            case "BasePath":
                header.BasePath = value;
                break;
            case "JobName":
                header.JobName = value;
                break;
            case "OriginComputer":
                header.OriginComputer = value;
                break;
            case "ScanName":
                header.ScanName = value;
                break;
            case "SkipHashCheck":
                header.SkipHashCheck = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                break;
            case "IgnoreBasePath":
                header.IgnoreBasePath = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                break;
            case "GeneratedUTC":
                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
                {
                    header.GeneratedUTC = d;
                }
                break;
            default:
                break;
        }
    }

    private static void ValidateColumnHeader(string line)
    {
        var expected = string.Join(",", s_columns);
        if (line != expected)
        {
            throw new InvalidDataException("Check report column header does not match the expected schema.");
        }
    }

    private static CheckReportRow ParseRow(string line)
    {
        var fields = ParseFields(line);
        if (fields.Count != s_columns.Length)
        {
            throw new InvalidDataException($"Check report row has {fields.Count} fields, expected {s_columns.Length}.");
        }
        var row = new CheckReportRow
        {
            Category = (CheckCategory)Enum.Parse(typeof(CheckCategory), fields[0]),
            PathMoved = string.Equals(fields[1], "true", StringComparison.OrdinalIgnoreCase),
            ScanItemId = string.IsNullOrEmpty(fields[2]) ? Guid.Empty : Guid.Parse(fields[2]),
            FilenameAndPathDB = NullIfEmpty(fields[3]),
            FilenameAndPathDisk = NullIfEmpty(fields[4]),
            PathBaseDB = NullIfEmpty(fields[5]),
            PathBaseDisk = NullIfEmpty(fields[6]),
            FileSizeDB = ParseLong(fields[7]),
            FileSizeDisk = ParseLong(fields[8]),
            MTimeDB = ParseDateTime(fields[9]),
            MTimeDisk = ParseDateTime(fields[10]),
            HashDB = NullIfEmpty(fields[11]),
            HashDisk = NullIfEmpty(fields[12]),
            CreationTimeDisk = ParseDateTime(fields[13]),
        };
        return row;
    }

    private static List<string> ParseFields(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"' && sb.Length == 0)
                {
                    inQuotes = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static void Append(StringBuilder sb, string? value, bool last = false)
    {
        sb.Append(Escape(value));
        if (!last)
        {
            sb.Append(',');
        }
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!mustQuote)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatDateTime(DateTime? d) =>
        d.HasValue ? d.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : string.Empty;

    private static DateTime? ParseDateTime(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static long? ParseLong(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }
        return long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
