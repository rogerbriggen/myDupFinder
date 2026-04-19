// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class MyDupFinderProjectTest
{
    [Fact]
    public void GetExampleDTO_ShouldReturnNonNullDTO()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotNull(dto);
    }

    [Fact]
    public void GetExampleDTO_ShouldReturnDTOWithScanJobs()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotEmpty(dto.MyDupFinderScanJobDTOs);
    }

    [Fact]
    public void GetExampleDTO_ShouldReturnDTOWithCheckJobs()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotEmpty(dto.MyDupFinderCheckJobDTOs);
    }

    [Fact]
    public void GetExampleDTO_ShouldReturnDTOWithFindDupsJobs()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotEmpty(dto.MyDupFinderFindDupsJobDTOs);
    }

    [Fact]
    public void GetExampleDTO_ScanJobShouldHaveExpectedFields()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        var scanJob = dto.MyDupFinderScanJobDTOs[0];
        Assert.False(string.IsNullOrWhiteSpace(scanJob.JobName));
        Assert.False(string.IsNullOrWhiteSpace(scanJob.OriginComputer));
        Assert.False(string.IsNullOrWhiteSpace(scanJob.ScanName));
        Assert.False(string.IsNullOrWhiteSpace(scanJob.BasePath));
        Assert.False(string.IsNullOrWhiteSpace(scanJob.DatabaseFile));
    }

    [Fact]
    public void GetExampleDTO_FindDupsJobShouldHaveExpectedFields()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        var findDupsJob = dto.MyDupFinderFindDupsJobDTOs[0];
        Assert.False(string.IsNullOrWhiteSpace(findDupsJob.JobName));
        Assert.False(string.IsNullOrWhiteSpace(findDupsJob.DatabaseFileBase));
        Assert.False(string.IsNullOrWhiteSpace(findDupsJob.ReportPath));
    }

    [Fact]
    public void WriteAndReadConfigurationRoundTrip_ShouldProduceSameData()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO originalDto);

        string tempFile = Path.Combine(Path.GetTempPath(), "myDupFinderTest_" + Guid.NewGuid() + ".xml");
        try
        {
            MyDupFinderProject.WriteConfigurationToFile(originalDto, tempFile);

            MyDupFinderProject.ReadConfigurationFromFile(tempFile, out MyDupFinderProjectDTO? readDto);

            Assert.NotNull(readDto);
            Assert.Equal(originalDto.MyDupFinderScanJobDTOs.Count, readDto!.MyDupFinderScanJobDTOs.Count);
            Assert.Equal(originalDto.MyDupFinderCheckJobDTOs.Count, readDto.MyDupFinderCheckJobDTOs.Count);
            Assert.Equal(originalDto.MyDupFinderFindDupsJobDTOs.Count, readDto.MyDupFinderFindDupsJobDTOs.Count);

            var originalScanJob = originalDto.MyDupFinderScanJobDTOs[0];
            var readScanJob = readDto.MyDupFinderScanJobDTOs[0];
            Assert.Equal(originalScanJob.JobName, readScanJob.JobName);
            Assert.Equal(originalScanJob.BasePath, readScanJob.BasePath);
            Assert.Equal(originalScanJob.DatabaseFile, readScanJob.DatabaseFile);

            var originalFindDupsJob = originalDto.MyDupFinderFindDupsJobDTOs[0];
            var readFindDupsJob = readDto.MyDupFinderFindDupsJobDTOs[0];
            Assert.Equal(originalFindDupsJob.JobName, readFindDupsJob.JobName);
            Assert.Equal(originalFindDupsJob.DatabaseFileBase, readFindDupsJob.DatabaseFileBase);
            Assert.Equal(originalFindDupsJob.FindDupsMode, readFindDupsJob.FindDupsMode);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
