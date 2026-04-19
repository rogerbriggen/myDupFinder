// Roger Briggen license this file to you under the MIT license.
//

using System.Collections.Generic;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class MyDupFinderFindDupsJobDTOTest
{
    private static MyDupFinderFindDupsJobDTO CreateValidDto(List<string> scanDBs)
    {
        var dbFile = Path.Combine(Path.GetTempPath(), "base.db");
        scanDBs.Add(dbFile);
        return new MyDupFinderFindDupsJobDTO
        {
            JobName = "TestFindDupsJob",
            DatabaseFileBase = dbFile,
            DatabaseFile = string.Empty,
            ReportPath = Path.Combine(Path.GetTempPath(), "reports")
        };
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenJobNameIsEmpty()
    {
        var scanDBs = new List<string>();
        var dto = CreateValidDto(scanDBs);
        dto.JobName = string.Empty;
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenJobNameIsWhitespace()
    {
        var scanDBs = new List<string>();
        var dto = CreateValidDto(scanDBs);
        dto.JobName = "   ";
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenDatabaseFileBaseIsEmpty()
    {
        var scanDBs = new List<string>();
        var dto = CreateValidDto(scanDBs);
        dto.DatabaseFileBase = string.Empty;
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenReportPathIsEmpty()
    {
        var scanDBs = new List<string>();
        var dto = CreateValidDto(scanDBs);
        dto.ReportPath = string.Empty;
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenDatabaseFileBaseNotInScanDBsAndDoesNotExist()
    {
        var scanDBs = new List<string>();
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "TestJob",
            DatabaseFileBase = Path.Combine(Path.GetTempPath(), "nonexistent_base.db"),
            DatabaseFile = string.Empty,
            ReportPath = Path.Combine(Path.GetTempPath(), "reports")
        };
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs));
    }

    [Fact]
    public void CheckSanity_ShouldNotThrow_WhenDatabaseFileBaseIsInScanDBs()
    {
        var scanDBs = new List<string>();
        var dto = CreateValidDto(scanDBs);
        // Should not throw because DatabaseFileBase is in scanDBs
        MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs);
    }

    [Fact]
    public void FixDto_ShouldAddDelimiterToReportPath_WhenNotPresent()
    {
        var dto = new MyDupFinderFindDupsJobDTO
        {
            ReportPath = Path.Combine("some", "report", "path")
        };
        MyDupFinderFindDupsJobDTO.FixDto(dto);
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.ReportPath));
    }

    [Fact]
    public void FixDto_ShouldNotChangeReportPath_WhenAlreadyHasDelimiter()
    {
        string path = Path.Combine("some", "report", "path") + Path.DirectorySeparatorChar;
        var dto = new MyDupFinderFindDupsJobDTO { ReportPath = path };
        MyDupFinderFindDupsJobDTO.FixDto(dto);
        Assert.Equal(path, dto.ReportPath);
    }

    [Fact]
    public void CheckSanity_ShouldNotThrow_WhenDatabaseFileIsInScanDBs()
    {
        var scanDBs = new List<string>();
        var dto = CreateValidDto(scanDBs);
        var secondDbFile = Path.Combine(Path.GetTempPath(), "second.db");
        scanDBs.Add(secondDbFile);
        dto.DatabaseFile = secondDbFile;
        // Should not throw because both DatabaseFileBase and DatabaseFile are in scanDBs
        MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs);
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenDatabaseFileNotInScanDBsAndDoesNotExist()
    {
        var scanDBs = new List<string>();
        var dto = CreateValidDto(scanDBs);
        dto.DatabaseFile = Path.Combine(Path.GetTempPath(), "nonexistent_second.db");
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDBs));
    }
}
