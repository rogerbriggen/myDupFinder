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
    public void GetExampleDTO_ReturnsNonNullDTO()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotNull(dto);
    }

    [Fact]
    public void GetExampleDTO_HasScanJob()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotEmpty(dto.MyDupFinderScanJobDTOs);
    }

    [Fact]
    public void GetExampleDTO_HasCheckJob()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotEmpty(dto.MyDupFinderCheckJobDTOs);
    }

    [Fact]
    public void GetExampleDTO_HasFindDupsJob()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        Assert.NotEmpty(dto.MyDupFinderFindDupsJobDTOs);
    }

    [Fact]
    public void GetExampleDTO_ScanJobHasExpectedValues()
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
    public void GetExampleDTO_FindDupsJobHasExpectedValues()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);
        var findDupsJob = dto.MyDupFinderFindDupsJobDTOs[0];
        Assert.False(string.IsNullOrWhiteSpace(findDupsJob.JobName));
        Assert.False(string.IsNullOrWhiteSpace(findDupsJob.DatabaseFileBase));
        Assert.False(string.IsNullOrWhiteSpace(findDupsJob.DatabaseFile));
        Assert.False(string.IsNullOrWhiteSpace(findDupsJob.ReportPath));
    }

    [Fact]
    public void WriteAndReadConfigurationRoundTrip_ProducesEquivalentDTO()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MyDupFinderProjectTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string configFile = Path.Combine(tempDir, "testconfig.xml");
        try
        {
            MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO originalDto);
            MyDupFinderProject.WriteConfigurationToFile(originalDto, configFile);

            Assert.True(File.Exists(configFile));

            MyDupFinderProject.ReadConfigurationFromFile(configFile, out MyDupFinderProjectDTO? readDto);

            Assert.NotNull(readDto);
            Assert.Equal(originalDto.MyDupFinderScanJobDTOs.Count, readDto!.MyDupFinderScanJobDTOs.Count);
            Assert.Equal(originalDto.MyDupFinderCheckJobDTOs.Count, readDto.MyDupFinderCheckJobDTOs.Count);
            Assert.Equal(originalDto.MyDupFinderFindDupsJobDTOs.Count, readDto.MyDupFinderFindDupsJobDTOs.Count);

            var origScan = originalDto.MyDupFinderScanJobDTOs[0];
            var readScan = readDto.MyDupFinderScanJobDTOs[0];
            Assert.Equal(origScan.JobName, readScan.JobName);
            Assert.Equal(origScan.OriginComputer, readScan.OriginComputer);
            Assert.Equal(origScan.ScanName, readScan.ScanName);
            Assert.Equal(origScan.BasePath, readScan.BasePath);
            Assert.Equal(origScan.DatabaseFile, readScan.DatabaseFile);

            var origFindDups = originalDto.MyDupFinderFindDupsJobDTOs[0];
            var readFindDups = readDto.MyDupFinderFindDupsJobDTOs[0];
            Assert.Equal(origFindDups.JobName, readFindDups.JobName);
            Assert.Equal(origFindDups.DatabaseFileBase, readFindDups.DatabaseFileBase);
            Assert.Equal(origFindDups.DatabaseFile, readFindDups.DatabaseFile);
            Assert.Equal(origFindDups.FindDupsMode, readFindDups.FindDupsMode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadConfigurationFromFile_NonExistingFile_ThrowsException()
    {
        string nonExistingFile = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N") + ".xml");
        Assert.ThrowsAny<Exception>(() => MyDupFinderProject.ReadConfigurationFromFile(nonExistingFile, out _));
    }
}
