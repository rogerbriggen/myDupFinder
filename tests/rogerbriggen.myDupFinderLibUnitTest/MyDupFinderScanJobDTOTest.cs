// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class MyDupFinderScanJobDTOTest
{
    private string CreateTempDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MyDupFinderTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    [Fact]
    public void CheckSanity_ValidDto_NoException()
    {
        string baseDir = CreateTempDir();
        string dbDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                JobName = "TestJob",
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = baseDir,
                DatabaseFile = Path.Combine(dbDir, "test.db"),
                ReportPath = dbDir
            };
            MyDupFinderScanJobDTO.CheckSanity(dto);
        }
        finally
        {
            Directory.Delete(baseDir, true);
            Directory.Delete(dbDir, true);
        }
    }

    [Fact]
    public void CheckSanity_EmptyJobName_ThrowsParameterException()
    {
        string baseDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                JobName = string.Empty,
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = baseDir,
                DatabaseFile = Path.Combine(Path.GetTempPath(), "test.db"),
                ReportPath = Path.GetTempPath()
            };
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void CheckSanity_EmptyOriginComputer_ThrowsParameterException()
    {
        string baseDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                JobName = "TestJob",
                OriginComputer = string.Empty,
                ScanName = "TestScan",
                BasePath = baseDir,
                DatabaseFile = Path.Combine(Path.GetTempPath(), "test.db"),
                ReportPath = Path.GetTempPath()
            };
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void CheckSanity_EmptyScanName_ThrowsParameterException()
    {
        string baseDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                JobName = "TestJob",
                OriginComputer = "TestPC",
                ScanName = string.Empty,
                BasePath = baseDir,
                DatabaseFile = Path.Combine(Path.GetTempPath(), "test.db"),
                ReportPath = Path.GetTempPath()
            };
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void CheckSanity_EmptyBasePath_ThrowsParameterException()
    {
        var dto = new MyDupFinderScanJobDTO
        {
            JobName = "TestJob",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = string.Empty,
            DatabaseFile = Path.Combine(Path.GetTempPath(), "test.db"),
            ReportPath = Path.GetTempPath()
        };
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_EmptyDatabaseFile_ThrowsParameterException()
    {
        string baseDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                JobName = "TestJob",
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = baseDir,
                DatabaseFile = string.Empty,
                ReportPath = Path.GetTempPath()
            };
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void CheckSanity_NonExistingBasePath_ThrowsParameterException()
    {
        var dto = new MyDupFinderScanJobDTO
        {
            JobName = "TestJob",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = Path.Combine(Path.GetTempPath(), "NonExistingDir_" + Guid.NewGuid().ToString("N")),
            DatabaseFile = Path.Combine(Path.GetTempPath(), "test.db"),
            ReportPath = Path.GetTempPath()
        };
        Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
    }

    [Fact]
    public void CheckSanity_DatabaseFileBelowBasePath_ThrowsParameterException()
    {
        string baseDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                JobName = "TestJob",
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = baseDir,
                DatabaseFile = Path.Combine(baseDir, "test.db"),
                ReportPath = Path.GetTempPath()
            };
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void CheckSanity_ReportPathBelowBasePath_ThrowsParameterException()
    {
        string baseDir = CreateTempDir();
        string dbDir = CreateTempDir();
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                JobName = "TestJob",
                OriginComputer = "TestPC",
                ScanName = "TestScan",
                BasePath = baseDir,
                DatabaseFile = Path.Combine(dbDir, "test.db"),
                ReportPath = Path.Combine(baseDir, "reports")
            };
            Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(dto));
        }
        finally
        {
            Directory.Delete(baseDir, true);
            Directory.Delete(dbDir, true);
        }
    }

    [Fact]
    public void FixDto_BasePathWithoutDelimiter_AddsDelimiter()
    {
        string baseDir = CreateTempDir();
        // Remove trailing separator if any
        string baseDirNoTrail = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            var dto = new MyDupFinderScanJobDTO
            {
                BasePath = baseDirNoTrail,
                ReportPath = baseDirNoTrail
            };
            MyDupFinderScanJobDTO.FixDto(dto);
            Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.BasePath));
            Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.ReportPath));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void FixDto_BasePathAlreadyHasDelimiter_NoChange()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "testpath") + Path.DirectorySeparatorChar;
        var dto = new MyDupFinderScanJobDTO
        {
            BasePath = baseDir,
            ReportPath = baseDir
        };
        MyDupFinderScanJobDTO.FixDto(dto);
        Assert.Equal(baseDir, dto.BasePath);
        Assert.Equal(baseDir, dto.ReportPath);
    }
}
