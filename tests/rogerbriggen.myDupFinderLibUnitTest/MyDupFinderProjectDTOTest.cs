// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class MyDupFinderProjectDTOTest
{
    private string CreateTempDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MyDupFinderDTOTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    [Fact]
    public void Constructor_InitializesEmptyLists()
    {
        var dto = new MyDupFinderProjectDTO();
        Assert.NotNull(dto.MyDupFinderScanJobDTOs);
        Assert.NotNull(dto.MyDupFinderCheckJobDTOs);
        Assert.NotNull(dto.MyDupFinderFindDupsJobDTOs);
        Assert.Empty(dto.MyDupFinderScanJobDTOs);
        Assert.Empty(dto.MyDupFinderCheckJobDTOs);
        Assert.Empty(dto.MyDupFinderFindDupsJobDTOs);
    }

    [Fact]
    public void CheckSanity_EmptyDto_NoException()
    {
        var dto = new MyDupFinderProjectDTO();
        // Empty DTO should pass sanity check (nothing to check)
        MyDupFinderProjectDTO.CheckSanity(dto);
    }

    [Fact]
    public void CheckSanity_InvalidScanJob_ThrowsParameterException()
    {
        var dto = new MyDupFinderProjectDTO();
        dto.MyDupFinderScanJobDTOs.Add(new MyDupFinderScanJobDTO
        {
            JobName = string.Empty, // Invalid
            OriginComputer = "PC",
            ScanName = "Scan",
            BasePath = Path.GetTempPath(),
            DatabaseFile = Path.Combine(Path.GetTempPath(), "test.db")
        });
        Assert.Throws<ParameterException>(() => MyDupFinderProjectDTO.CheckSanity(dto));
    }

    [Fact]
    public void FixDto_EmptyDto_NoException()
    {
        var dto = new MyDupFinderProjectDTO();
        // Empty DTO should fix without issues
        MyDupFinderProjectDTO.FixDto(dto);
    }

    [Fact]
    public void FixDto_ScanJobsAndFindDupsJobs_AddsDelimiters()
    {
        string baseDir = CreateTempDir();
        try
        {
            // Remove trailing separator
            string baseDirNoTrail = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var dto = new MyDupFinderProjectDTO();
            dto.MyDupFinderScanJobDTOs.Add(new MyDupFinderScanJobDTO
            {
                BasePath = baseDirNoTrail,
                ReportPath = baseDirNoTrail
            });
            dto.MyDupFinderFindDupsJobDTOs.Add(new MyDupFinderFindDupsJobDTO
            {
                ReportPath = baseDirNoTrail
            });

            MyDupFinderProjectDTO.FixDto(dto);

            Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.MyDupFinderScanJobDTOs[0].BasePath));
            Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.MyDupFinderScanJobDTOs[0].ReportPath));
            Assert.True(FileHelper.EndsWithDirectoryDelimiter(dto.MyDupFinderFindDupsJobDTOs[0].ReportPath));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }
}
