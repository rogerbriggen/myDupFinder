// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class MyDupFinderScanJobDTOTest
{
    private static MyDupFinderScanJobDTO CreateValidDto(string basePath)
    {
        return new MyDupFinderScanJobDTO
        {
            JobName = "TestJob",
            OriginComputer = "TestComputer",
            ScanName = "TestScan",
            BasePath = basePath,
            DatabaseFile = Path.Combine(Path.GetTempPath(), "testdb_" + Guid.NewGuid() + ".db"),
            ReportPath = Path.Combine(Path.GetTempPath(), "reports_" + Guid.NewGuid())
        };
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenJobNameIsEmpty()
    {
        var dto = CreateValidDto("somepath");
        dto.JobName = string.Empty;
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenJobNameIsWhitespace()
    {
        var dto = CreateValidDto("somepath");
        dto.JobName = "   ";
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenOriginComputerIsEmpty()
    {
        var dto = CreateValidDto("somepath");
        dto.OriginComputer = string.Empty;
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenScanNameIsEmpty()
    {
        var dto = CreateValidDto("somepath");
        dto.ScanName = string.Empty;
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenBasePathIsEmpty()
    {
        var dto = CreateValidDto(string.Empty);
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenDatabaseFileIsEmpty()
    {
        var dto = CreateValidDto("somepath");
        dto.DatabaseFile = string.Empty;
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenBasePathDoesNotExist()
    {
        string nonExistentPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid());
        var dto = CreateValidDto(nonExistentPath);
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenDatabaseFileIsBelowBasePath()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var dto = CreateValidDto(tmpDir);
            dto.DatabaseFile = Path.Combine(tmpDir, "db.db");
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void CheckSanity_ShouldThrow_WhenReportPathIsBelowBasePath()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var dto = CreateValidDto(tmpDir);
            dto.ReportPath = Path.Combine(tmpDir, "reports");
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FixDto_ShouldAddDelimiterToBasePath_WhenNotPresent()
    {
        string path = Path.Combine("some", "base", "path");
        var dto = new MyDupFinderScanJobDTO { BasePath = path, ReportPath = path };
        MyDupFinderScanJobDTO.FixDto(dto);
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.BasePath));
    }

    [Fact]
    public void FixDto_ShouldAddDelimiterToReportPath_WhenNotPresent()
    {
        string path = Path.Combine("some", "report", "path");
        var dto = new MyDupFinderScanJobDTO { BasePath = path, ReportPath = path };
        MyDupFinderScanJobDTO.FixDto(dto);
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.ReportPath));
    }

    [Fact]
    public void FixDto_ShouldNotChangeBasePath_WhenAlreadyHasDelimiter()
    {
        string path = Path.Combine("some", "base", "path") + Path.DirectorySeparatorChar;
        var dto = new MyDupFinderScanJobDTO { BasePath = path, ReportPath = path };
        MyDupFinderScanJobDTO.FixDto(dto);
        Assert.Equal(path, dto.BasePath);
    }

    [Fact]
    public void FixDto_ShouldNotChangeReportPath_WhenAlreadyHasDelimiter()
    {
        string path = Path.Combine("some", "report", "path") + Path.DirectorySeparatorChar;
        var dto = new MyDupFinderScanJobDTO { BasePath = path, ReportPath = path };
        MyDupFinderScanJobDTO.FixDto(dto);
        Assert.Equal(path, dto.ReportPath);
    }
}
