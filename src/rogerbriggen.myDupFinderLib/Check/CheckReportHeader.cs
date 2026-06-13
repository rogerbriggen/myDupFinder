// Roger Briggen license this file to you under the MIT license.
//

using System;

namespace RogerBriggen.MyDupFinderLib;

/// <summary>
/// The self-contained metadata that lets applyCheck run from the CSV alone,
/// without re-reading the project XML.
/// Written at the top of the CSV as `# Key=Value` comment lines.
/// </summary>
public sealed class CheckReportHeader
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string DatabaseFile { get; set; } = string.Empty;
    public string BasePath { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public string OriginComputer { get; set; } = string.Empty;
    public string ScanName { get; set; } = string.Empty;
    public bool SkipHashCheck { get; set; }
    public bool IgnoreBasePath { get; set; }
    public DateTime GeneratedUTC { get; set; } = DateTime.UtcNow;
}
