// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The job setting HashWorkers (default 1, valid 1 to 64), the same pattern as ThrottlePercent.
/// </summary>
public class HashWorkersSettingTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _basePath;
    private readonly string _dbFile;

    public HashWorkersSettingTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HashWorkersSettingTest_" + Guid.NewGuid().ToString("N"));
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

    private MyDupFinderScanJobDTO CreateScanJob(int hashWorkers = 1) => new MyDupFinderScanJobDTO
    {
        JobName = "ScanJob",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _basePath,
        DatabaseFile = _dbFile,
        ReportPath = _tempDir,
        HashWorkers = hashWorkers,
    };

    private string WriteProjectXml(string content)
    {
        string file = Path.Combine(_tempDir, "project_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void HashWorkers_DefaultsToOne()
    {
        Assert.Equal(1, new MyDupFinderScanJobDTO().HashWorkers);
        Assert.Equal(1, new MyDupFinderCheckJobDTO().ScanJobDTO.HashWorkers);
    }

    [Fact]
    public void GetExampleDTO_UsesOneWorker()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);

        Assert.Equal(1, dto.MyDupFinderScanJobDTOs[0].HashWorkers);
        Assert.Equal(1, dto.MyDupFinderCheckJobDTOs[0].ScanJobDTO.HashWorkers);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public void CheckSanity_ScanJob_AcceptsValuesFrom1To64(int value) => MyDupFinderScanJobDTO.CheckSanity(CreateScanJob(value));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void CheckSanity_ScanJob_RejectsValuesOutside1To64(int value)
    {
        var ex = Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(CreateScanJob(value)));
        Assert.Contains("HashWorkers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckSanity_CheckJob_ValidatesHashWorkersOfItsScanJob()
    {
        MyDupFinderCheckJobDTO.CheckSanity(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob(64) });

        var ex = Assert.Throws<ParameterException>(() => MyDupFinderCheckJobDTO.CheckSanity(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob(65) }));
        Assert.Contains("HashWorkers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_RoundTrip_KeepsHashWorkers()
    {
        var project = new MyDupFinderProjectDTO();
        project.MyDupFinderScanJobDTOs.Add(CreateScanJob(8));
        string file = Path.Combine(_tempDir, "roundtrip.xml");

        MyDupFinderProject.WriteConfigurationToFile(project, file);
        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.NotNull(read);
        Assert.Equal(8, Assert.Single(read!.MyDupFinderScanJobDTOs).HashWorkers);
    }

    [Fact]
    public void Xml_WithoutHashWorkersElement_LoadsWithOne()
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
                  </ScanJobDTO>
                </MyDupFinderCheckJobDTO>
              </MyDupFinderCheckJobDTOs>
            </MyDupFinderProjectDTO>
            """);

        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.NotNull(read);
        Assert.Equal(1, Assert.Single(read!.MyDupFinderScanJobDTOs).HashWorkers);
        Assert.Equal(1, Assert.Single(read.MyDupFinderCheckJobDTOs).ScanJobDTO.HashWorkers);
    }

    [Fact]
    public void Xml_WithHashWorkersElement_LoadsTheValue()
    {
        string file = WriteProjectXml(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <MyDupFinderProjectDTO>
              <MyDupFinderScanJobDTOs>
                <MyDupFinderScanJobDTO>
                  <JobName>Scan</JobName>
                  <HashWorkers>16</HashWorkers>
                </MyDupFinderScanJobDTO>
              </MyDupFinderScanJobDTOs>
            </MyDupFinderProjectDTO>
            """);

        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.Equal(16, Assert.Single(read!.MyDupFinderScanJobDTOs).HashWorkers);
    }
}
