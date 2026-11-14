// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class ThrottlePercentTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _basePath;
    private readonly string _dbFile;

    public ThrottlePercentTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ThrottlePercentTest_" + Guid.NewGuid().ToString("N"));
        _basePath = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(_basePath);
        _dbFile = Path.Combine(_tempDir, "test.db");
        File.WriteAllText(_dbFile, "dummy");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private MyDupFinderScanJobDTO CreateScanJob(uint throttlePercent = 0) => new MyDupFinderScanJobDTO
    {
        JobName = "ScanJob",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _basePath,
        DatabaseFile = _dbFile,
        ReportPath = _tempDir,
        ThrottlePercent = throttlePercent,
    };

    private MyDupFinderRefreshJobDTO CreateRefreshJob(uint throttlePercent = 0) => new MyDupFinderRefreshJobDTO
    {
        JobName = "RefreshJob",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _basePath,
        DatabaseFile = _dbFile,
        ReportPath = _tempDir,
        ThrottlePercent = throttlePercent,
    };

    private string WriteProjectXml(string content)
    {
        string file = Path.Combine(_tempDir, "project_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, content);
        return file;
    }

    // ---- Defaults ----

    [Fact]
    public void ThrottlePercent_DefaultsToZero()
    {
        Assert.Equal(0u, new MyDupFinderScanJobDTO().ThrottlePercent);
        Assert.Equal(0u, new MyDupFinderRefreshJobDTO().ThrottlePercent);
        Assert.Equal(0u, new MyDupFinderCheckJobDTO().ScanJobDTO.ThrottlePercent);
    }

    [Fact]
    public void GetExampleDTO_ThrottlingIsOff()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);

        Assert.Equal(0u, dto.MyDupFinderScanJobDTOs[0].ThrottlePercent);
        Assert.Equal(0u, dto.MyDupFinderRefreshJobDTOs[0].ThrottlePercent);
        Assert.Equal(0u, dto.MyDupFinderCheckJobDTOs[0].ScanJobDTO.ThrottlePercent);
    }

    // ---- Validation ----

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(70u)]
    public void CheckSanity_ScanJob_AcceptsValuesUpTo70(uint value) => MyDupFinderScanJobDTO.CheckSanity(CreateScanJob(value));

    [Theory]
    [InlineData(71u)]
    [InlineData(100u)]
    [InlineData(uint.MaxValue)]
    public void CheckSanity_ScanJob_RejectsValuesAbove70(uint value)
    {
        var ex = Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(CreateScanJob(value)));
        Assert.Contains("ThrottlePercent", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(70u)]
    public void CheckSanity_RefreshJob_AcceptsValuesUpTo70(uint value) => MyDupFinderRefreshJobDTO.CheckSanity(CreateRefreshJob(value));

    [Theory]
    [InlineData(71u)]
    [InlineData(uint.MaxValue)]
    public void CheckSanity_RefreshJob_RejectsValuesAbove70(uint value)
    {
        var ex = Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(CreateRefreshJob(value)));
        Assert.Contains("ThrottlePercent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSanity_CheckJob_ValidatesThrottlePercentOfItsScanJob()
    {
        MyDupFinderCheckJobDTO.CheckSanity(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob(70) });

        var ex = Assert.Throws<ParameterException>(() => MyDupFinderCheckJobDTO.CheckSanity(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob(71) }));
        Assert.Contains("ThrottlePercent", ex.Message, StringComparison.Ordinal);
    }

    // ---- XML ----

    [Fact]
    public void Xml_RoundTrip_KeepsThrottlePercent()
    {
        var project = new MyDupFinderProjectDTO();
        project.MyDupFinderScanJobDTOs.Add(CreateScanJob(25));
        project.MyDupFinderRefreshJobDTOs.Add(CreateRefreshJob(40));
        project.MyDupFinderCheckJobDTOs.Add(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob(55) });
        string file = Path.Combine(_tempDir, "roundtrip.xml");

        MyDupFinderProject.WriteConfigurationToFile(project, file);
        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.NotNull(read);
        Assert.Equal(25u, Assert.Single(read!.MyDupFinderScanJobDTOs).ThrottlePercent);
        Assert.Equal(40u, Assert.Single(read.MyDupFinderRefreshJobDTOs).ThrottlePercent);
        Assert.Equal(55u, Assert.Single(read.MyDupFinderCheckJobDTOs).ScanJobDTO.ThrottlePercent);
    }

    [Fact]
    public void Xml_WithoutThrottlePercentElement_LoadsWithZero()
    {
        // A project file written before the setting existed
        string file = WriteProjectXml(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <MyDupFinderProjectDTO>
              <MyDupFinderScanJobDTOs>
                <MyDupFinderScanJobDTO>
                  <JobName>Old scan</JobName>
                  <BasePath>m:\Test</BasePath>
                  <OriginComputer>E6600</OriginComputer>
                  <ScanName>Backup</ScanName>
                  <DatabaseFile>m:\finddupdb\base.db</DatabaseFile>
                  <ReportPath>m:\finddupdb\</ReportPath>
                </MyDupFinderScanJobDTO>
              </MyDupFinderScanJobDTOs>
              <MyDupFinderCheckJobDTOs>
                <MyDupFinderCheckJobDTO>
                  <ScanJobDTO>
                    <JobName>Old check</JobName>
                    <BasePath>m:\Test</BasePath>
                    <OriginComputer>E6600</OriginComputer>
                    <ScanName>Backup</ScanName>
                    <DatabaseFile>m:\finddupdb\base.db</DatabaseFile>
                    <ReportPath>m:\finddupdb\</ReportPath>
                  </ScanJobDTO>
                  <IgnoreBasePath>false</IgnoreBasePath>
                  <SkipHashCheck>true</SkipHashCheck>
                </MyDupFinderCheckJobDTO>
              </MyDupFinderCheckJobDTOs>
              <MyDupFinderRefreshJobDTOs>
                <MyDupFinderRefreshJobDTO>
                  <JobName>Old refresh</JobName>
                  <BasePath>m:\Test</BasePath>
                  <OriginComputer>E6600</OriginComputer>
                  <ScanName>Backup</ScanName>
                  <DatabaseFile>m:\finddupdb\base.db</DatabaseFile>
                  <ReportPath>m:\finddupdb\</ReportPath>
                </MyDupFinderRefreshJobDTO>
              </MyDupFinderRefreshJobDTOs>
            </MyDupFinderProjectDTO>
            """);

        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.NotNull(read);
        var scan = Assert.Single(read!.MyDupFinderScanJobDTOs);
        Assert.Equal("Old scan", scan.JobName);
        Assert.Equal(0u, scan.ThrottlePercent);
        Assert.Equal(0u, Assert.Single(read.MyDupFinderRefreshJobDTOs).ThrottlePercent);
        Assert.Equal(0u, Assert.Single(read.MyDupFinderCheckJobDTOs).ScanJobDTO.ThrottlePercent);
    }

    [Fact]
    public void Xml_WithThrottlePercentElement_LoadsTheValue()
    {
        string file = WriteProjectXml(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <MyDupFinderProjectDTO>
              <MyDupFinderScanJobDTOs>
                <MyDupFinderScanJobDTO>
                  <JobName>Scan</JobName>
                  <ThrottlePercent>30</ThrottlePercent>
                </MyDupFinderScanJobDTO>
              </MyDupFinderScanJobDTOs>
            </MyDupFinderProjectDTO>
            """);

        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.Equal(30u, Assert.Single(read!.MyDupFinderScanJobDTOs).ThrottlePercent);
    }
}
