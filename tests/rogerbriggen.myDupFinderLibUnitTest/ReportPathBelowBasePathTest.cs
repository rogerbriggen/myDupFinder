// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// CheckSanity runs before FixDto, so the path rules must not depend on the trailing separator the user typed.
/// </summary>
public class ReportPathBelowBasePathTest : IDisposable
{
    private readonly string _root;
    private readonly string _basePath;
    private readonly string _dbFile;

    public ReportPathBelowBasePathTest()
    {
        _root = Path.Combine(Path.GetTempPath(), "ReportPathBelowBasePathTest_" + Guid.NewGuid().ToString("N"));
        _basePath = Path.Combine(_root, "base");
        Directory.CreateDirectory(_basePath);
        _dbFile = Path.Combine(_root, "outside.db");
        File.WriteAllText(_dbFile, "dummy");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private MyDupFinderScanJobDTO Scan(string reportPath) => new MyDupFinderScanJobDTO
    {
        JobName = "job",
        OriginComputer = "pc",
        ScanName = "scan",
        BasePath = _basePath,
        DatabaseFile = _dbFile,
        ReportPath = reportPath,
    };

    private MyDupFinderRefreshJobDTO Refresh(string reportPath) => new MyDupFinderRefreshJobDTO
    {
        JobName = "job",
        OriginComputer = "pc",
        ScanName = "scan",
        BasePath = _basePath,
        DatabaseFile = _dbFile,
        ReportPath = reportPath,
    };

    public static TheoryData<string> BelowOrEqualToBase => new TheoryData<string>
    {
        "BASE",
        "BASE" + Path.DirectorySeparatorChar,
        "BASE" + Path.DirectorySeparatorChar + "sub",
        "BASE" + Path.DirectorySeparatorChar + "sub" + Path.DirectorySeparatorChar,
    };

    private string Resolve(string template) => template.Replace("BASE", _basePath, StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(BelowOrEqualToBase))]
    public void Scan_CheckSanity_ShouldThrow_WhenReportPathIsTheBasePathOrBelow(string reportPath)
    {
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(Scan(Resolve(reportPath))));
    }

    [Theory]
    [MemberData(nameof(BelowOrEqualToBase))]
    public void Refresh_CheckSanity_ShouldThrow_WhenReportPathIsTheBasePathOrBelow(string reportPath)
    {
        Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(Refresh(Resolve(reportPath))));
    }

    [Fact]
    public void CheckSanity_ShouldNotThrow_WhenReportPathIsASiblingWithTheSamePrefix()
    {
        string sibling = _basePath + "2";
        MyDupFinderScanJobDTO.CheckSanity(Scan(sibling));
        MyDupFinderRefreshJobDTO.CheckSanity(Refresh(sibling));
    }

    [Fact]
    public void CheckSanity_ShouldNotThrow_WhenReportPathIsOutsideOrEmpty()
    {
        MyDupFinderScanJobDTO.CheckSanity(Scan(_root));
        MyDupFinderScanJobDTO.CheckSanity(Scan(string.Empty));
        MyDupFinderRefreshJobDTO.CheckSanity(Refresh(_root));
        MyDupFinderRefreshJobDTO.CheckSanity(Refresh(string.Empty));
    }
}
