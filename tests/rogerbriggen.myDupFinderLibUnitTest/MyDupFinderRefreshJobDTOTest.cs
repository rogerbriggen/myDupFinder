// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class MyDupFinderRefreshJobDTOTest
{
    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RefreshDTOTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void CheckSanity_ValidDto_NoException()
    {
        var tempDir = CreateTempDir();
        var dbFile = Path.Combine(Path.GetTempPath(), "RefreshDTOTest_" + Guid.NewGuid().ToString("N") + ".db");
        // Create a dummy DB file
        File.WriteAllText(dbFile, "dummy");
        try
        {
            var dto = new MyDupFinderRefreshJobDTO
            {
                JobName = "TestJob",
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = tempDir,
                DatabaseFile = dbFile,
                ReportPath = Path.GetTempPath()
            };
            MyDupFinderRefreshJobDTO.CheckSanity(dto);
        }
        finally
        {
            Directory.Delete(tempDir, true);
            if (File.Exists(dbFile))
            {
                File.Delete(dbFile);
            }
        }
    }

    [Fact]
    public void CheckSanity_EmptyJobName_ThrowsParameterException()
    {
        var dto = new MyDupFinderRefreshJobDTO { JobName = "" };
        Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_EmptyOriginComputer_ThrowsParameterException()
    {
        var dto = new MyDupFinderRefreshJobDTO { JobName = "Test", OriginComputer = "" };
        Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_EmptyScanName_ThrowsParameterException()
    {
        var dto = new MyDupFinderRefreshJobDTO { JobName = "Test", OriginComputer = "PC", ScanName = "" };
        Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_EmptyBasePath_ThrowsParameterException()
    {
        var dto = new MyDupFinderRefreshJobDTO { JobName = "Test", OriginComputer = "PC", ScanName = "Scan", BasePath = "" };
        Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_EmptyDatabaseFile_ThrowsParameterException()
    {
        var tempDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderRefreshJobDTO { JobName = "Test", OriginComputer = "PC", ScanName = "Scan", BasePath = tempDir, DatabaseFile = "" };
            Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CheckSanity_NonExistentBasePath_ThrowsParameterException()
    {
        var dto = new MyDupFinderRefreshJobDTO { JobName = "Test", OriginComputer = "PC", ScanName = "Scan", BasePath = "/nonexistent/path/xyz", DatabaseFile = "test.db" };
        Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_NonExistentDatabaseFile_ThrowsParameterException()
    {
        var tempDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderRefreshJobDTO { JobName = "Test", OriginComputer = "PC", ScanName = "Scan", BasePath = tempDir, DatabaseFile = "/nonexistent/db.db" };
            Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FixDto_AddsTrailingDelimiterToBasePath()
    {
        var dto = new MyDupFinderRefreshJobDTO { BasePath = "/some/path", ReportPath = "/report/path" };
        MyDupFinderRefreshJobDTO.FixDto(dto);
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), dto.BasePath);
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), dto.ReportPath);
    }

    [Fact]
    public void FixDto_EmptyReportPath_NoException()
    {
        var dto = new MyDupFinderRefreshJobDTO { BasePath = "/some/path", ReportPath = "" };
        MyDupFinderRefreshJobDTO.FixDto(dto);
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), dto.BasePath);
        Assert.Equal("", dto.ReportPath);
    }
}
