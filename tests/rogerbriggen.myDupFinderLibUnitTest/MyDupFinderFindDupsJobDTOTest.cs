// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class MyDupFinderFindDupsJobDTOTest
{
    [Fact]
    public void CheckSanity_EmptyJobName_ThrowsParameterException()
    {
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = string.Empty,
            DatabaseFileBase = "somedb.db",
            ReportPath = Path.GetTempPath()
        };
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, new List<string>()));
    }

    [Fact]
    public void CheckSanity_EmptyDatabaseFileBase_ThrowsParameterException()
    {
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "TestJob",
            DatabaseFileBase = string.Empty,
            ReportPath = Path.GetTempPath()
        };
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, new List<string>()));
    }

    [Fact]
    public void CheckSanity_EmptyReportPath_ThrowsParameterException()
    {
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "TestJob",
            DatabaseFileBase = "somedb.db",
            ReportPath = string.Empty
        };
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, new List<string>()));
    }

    [Fact]
    public void CheckSanity_DatabaseFileBaseInScanDbList_NoException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TestFindDups_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string dbFile = Path.Combine(tempDir, "base.db");
            var scanDbs = new List<string> { dbFile };
            var dto = new MyDupFinderFindDupsJobDTO
            {
                JobName = "TestJob",
                DatabaseFileBase = dbFile,
                ReportPath = tempDir
            };
            MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDbs);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CheckSanity_DatabaseFileBaseNotInScanDbsAndNotExisting_ThrowsParameterException()
    {
        string nonExistingFile = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N") + ".db");
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "TestJob",
            DatabaseFileBase = nonExistingFile,
            ReportPath = Path.GetTempPath()
        };
        Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, new List<string>()));
    }

    [Fact]
    public void CheckSanity_ReportPathIsFile_ThrowsParameterException()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            string dbFile = tempFile; // Use as base db so it "exists"
            var scanDbs = new List<string> { dbFile };
            var dto = new MyDupFinderFindDupsJobDTO
            {
                JobName = "TestJob",
                DatabaseFileBase = dbFile,
                ReportPath = tempFile // This is a file, not a directory
            };
            Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDbs));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CheckSanity_ReportAlreadyExists_ThrowsParameterException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TestFindDups_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string dbFile = Path.Combine(tempDir, "base.db");
            var scanDbs = new List<string> { dbFile };
            string jobName = "TestJob";
            // Create the report file so that the sanity check detects it exists
            string reportFile = Path.Combine(tempDir, jobName + " dupReport.csv");
            File.WriteAllText(reportFile, "dummy");

            var dto = new MyDupFinderFindDupsJobDTO
            {
                JobName = jobName,
                DatabaseFileBase = dbFile,
                ReportPath = tempDir + Path.DirectorySeparatorChar
            };
            Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, scanDbs));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FixDto_ReportPathWithoutDelimiter_AddsDelimiter()
    {
        string path = Path.Combine(Path.GetTempPath(), "reports");
        var dto = new MyDupFinderFindDupsJobDTO
        {
            ReportPath = path
        };
        MyDupFinderFindDupsJobDTO.FixDto(dto);
        Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.ReportPath));
    }

    [Fact]
    public void FixDto_ReportPathAlreadyHasDelimiter_NoChange()
    {
        string path = Path.Combine(Path.GetTempPath(), "reports") + Path.DirectorySeparatorChar;
        var dto = new MyDupFinderFindDupsJobDTO
        {
            ReportPath = path
        };
        MyDupFinderFindDupsJobDTO.FixDto(dto);
        Assert.Equal(path, dto.ReportPath);
    }
}
