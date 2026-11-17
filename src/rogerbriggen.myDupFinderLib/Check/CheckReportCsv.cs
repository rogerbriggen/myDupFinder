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
/// comment lines so applyCheck is self-contained. The reader streams the file (see <see cref="Read"/>).
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
        writer.WriteLine(FormattableString.Invariant($"# GeneratedUTC={FormatUtc(header.GeneratedUTC)}"));
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

    /// <summary>
    /// Reads the header of a report and returns it together with the rows of the report.
    /// The header and the column header line are read and validated now. The rows are NOT in memory: the returned sequence is lazy, it
    /// opens the file and reads it record by record whenever it is enumerated (every enumeration is a new pass over the file, nothing is cached).
    /// A malformed row (wrong field count, an unterminated quoted field, an unparsable value) throws while the sequence is enumerated, not here.
    /// A quoted field may contain commas, doubled quotes and line breaks (CR, LF, CRLF), they are returned exactly as they are in the file.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a check report (no signature, no or a wrong column header line).</exception>
    public static (CheckReportHeader Header, IEnumerable<CheckReportRow> Rows) Read(string path)
    {
        CheckReportHeader header;
        using (var reader = OpenAndReadHeader(path, out header))
        {
            // Only the header is needed now; the rows are read when the sequence is enumerated
        }
        return (header, EnumerateRows(path, header));
    }

    private static IEnumerable<CheckReportRow> EnumerateRows(string path, CheckReportHeader expectedHeader)
    {
        using var reader = OpenAndReadHeader(path, out var reopened);
        // A report that was replaced by another one since Read must not be read as if it were the first (other database, other version)
        if (!SameHeader(expectedHeader, reopened))
        {
            throw new InvalidDataException("The report changed while it was read (its header is not the one that was read first).");
        }
        var fields = new List<string>(s_columns.Length);
        var sb = new StringBuilder();
        while (TryReadRecord(reader, fields, sb))
        {
            yield return ParseRow(fields);
        }
    }

    private static bool SameHeader(CheckReportHeader a, CheckReportHeader b) =>
        a.SchemaVersion == b.SchemaVersion
        && string.Equals(a.DatabaseFile, b.DatabaseFile, StringComparison.Ordinal)
        && string.Equals(a.BasePath, b.BasePath, StringComparison.Ordinal)
        && string.Equals(a.JobName, b.JobName, StringComparison.Ordinal)
        && string.Equals(a.OriginComputer, b.OriginComputer, StringComparison.Ordinal)
        && string.Equals(a.ScanName, b.ScanName, StringComparison.Ordinal)
        && a.SkipHashCheck == b.SkipHashCheck
        && a.IgnoreBasePath == b.IgnoreBasePath
        && a.GeneratedUTC.Ticks == b.GeneratedUTC.Ticks;

    /// <summary>
    /// Opens the report and reads the comment lines and the column header line. The reader is positioned at the first record.
    /// </summary>
    private static StreamReader OpenAndReadHeader(string path, out CheckReportHeader header)
    {
        var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        try
        {
            header = new CheckReportHeader();
            bool signatureSeen = false;
            string? line;
            while ((line = reader.ReadLine()) is not null && line.StartsWith("#", StringComparison.Ordinal))
            {
                ParseHeaderLine(line, header, ref signatureSeen);
            }
            if (!signatureSeen)
            {
                throw new InvalidDataException("Not a myDupFinder check report (missing signature line).");
            }
            if (line is null)
            {
                throw new InvalidDataException("Check report has no column-header line.");
            }
            ValidateColumnHeader(line);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the next record: a state machine over the characters. Outside quotes a comma ends a field and CR, LF or CRLF end the record; inside a quoted field
    /// every character (also CR, LF and commas) belongs to the field and a doubled quote is one quote. Blank lines between records are skipped.
    /// </summary>
    /// <returns>False at the end of the file.</returns>
    /// <exception cref="InvalidDataException">The file ends inside a quoted field.</exception>
    private static bool TryReadRecord(TextReader reader, List<string> fields, StringBuilder sb)
    {
        while (true)
        {
            fields.Clear();
            sb.Clear();
            bool inQuotes = false;
            bool hasContent = false;
            while (true)
            {
                int ch = reader.Read();
                if (ch < 0)
                {
                    if (inQuotes)
                    {
                        throw new InvalidDataException("Check report ends inside a quoted field (unterminated quote).");
                    }
                    if (!hasContent)
                    {
                        return false;
                    }
                    fields.Add(sb.ToString());
                    return true;
                }
                char c = (char)ch;
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            reader.Read();
                            sb.Append('"');
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
                    continue;
                }
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                    }
                    if (!hasContent)
                    {
                        break; // blank line, start the next record
                    }
                    fields.Add(sb.ToString());
                    return true;
                }
                hasContent = true;
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

    private static CheckReportRow ParseRow(List<string> fields)
    {
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

    private static string FormatDateTime(DateTime? d) => d.HasValue ? FormatUtc(d.Value) : string.Empty;

    /// <summary>
    /// Round-trip text of a UTC time. A value of kind Utc is written as it is, a Local one is converted, and an Unspecified one is taken as UTC: that is what
    /// values read back from SQLite are (the database stores UTC times without a kind), and <see cref="DateTime.ToUniversalTime"/> would treat them as local
    /// and shift them by the offset of the local time zone.
    /// </summary>
    internal static string FormatUtc(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        return utc.ToString("o", CultureInfo.InvariantCulture);
    }

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
