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

    [Fact]
    public void CheckSanity_ShouldThrow_WhenDatabaseFileIsBelowBaseGivenWithDotSegments()
    {
        string db = _basePath + Path.DirectorySeparatorChar + "sub" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "x.db";
        var scan = Scan(string.Empty);
        scan.DatabaseFile = db;
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(scan));
        File.WriteAllText(Path.Combine(_basePath, "x.db"), "dummy");
        var refresh = Refresh(string.Empty);
        refresh.DatabaseFile = db;
        Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(refresh));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenPathsAreBelowBaseGivenRelativeToTheCurrentDirectory()
    {
        // On another drive than the current directory the relative path is the absolute one: still a valid case
        string relDb = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_basePath, "x.db"));
        string relReport = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_basePath, "reports"));
        var scan = Scan(string.Empty);
        scan.DatabaseFile = relDb;
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(scan));
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(Scan(relReport)));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenPathsDifferOnlyInCaseOnWindows()
    {
        string upper = _basePath.ToUpperInvariant();
        string reportPath = Path.Combine(upper, "reports");
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(Scan(reportPath)));
            Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(Refresh(reportPath)));
        }
        else
        {
            MyDupFinderScanJobDTO.CheckSanity(Scan(reportPath));
        }
    }

    [Fact]
    public void CheckSanity_ShouldNotThrow_WhenDatabaseFileIsInASiblingWithTheSamePrefix()
    {
        string siblingDir = _basePath + "2";
        Directory.CreateDirectory(siblingDir);
        string db = Path.Combine(siblingDir, "x.db");
        File.WriteAllText(db, "dummy");
        var scan = Scan(string.Empty);
        scan.DatabaseFile = db;
        MyDupFinderScanJobDTO.CheckSanity(scan);
        var refresh = Refresh(string.Empty);
        refresh.DatabaseFile = db;
        MyDupFinderRefreshJobDTO.CheckSanity(refresh);
    }

    [Fact]
    public void CheckSanity_ShouldThrowParameterException_WhenAPathIsNotValid()
    {
        // A NUL character is rejected by Path.GetFullPath
        var scan = Scan("bad" + '\0' + "path");
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(scan));
    }
}
