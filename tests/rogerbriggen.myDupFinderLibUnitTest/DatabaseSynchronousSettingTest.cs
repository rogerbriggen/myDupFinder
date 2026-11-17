// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The job setting DatabaseSynchronous (Full by default, Full or Normal), the same pattern as HashWorkers. What the values do to the database is tested in
/// <see cref="DatabaseSynchronousTest"/>.
/// </summary>
public class DatabaseSynchronousSettingTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _basePath;
    private readonly string _dbFile;

    public DatabaseSynchronousSettingTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DatabaseSynchronousSettingTest_" + Guid.NewGuid().ToString("N"));
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

    private MyDupFinderScanJobDTO CreateScanJob(DatabaseSynchronous value = DatabaseSynchronous.Full) => new MyDupFinderScanJobDTO
    {
        JobName = "ScanJob",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _basePath,
        DatabaseFile = _dbFile,
        ReportPath = _tempDir,
        DatabaseSynchronous = value,
    };

    private MyDupFinderRefreshJobDTO CreateRefreshJob(DatabaseSynchronous value = DatabaseSynchronous.Full) => new MyDupFinderRefreshJobDTO
    {
        JobName = "RefreshJob",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        BasePath = _basePath,
        DatabaseFile = _dbFile,
        ReportPath = _tempDir,
        DatabaseSynchronous = value,
    };

    private string WriteProjectXml(string content)
    {
        string file = Path.Combine(_tempDir, "project_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void DatabaseSynchronous_DefaultsToFull()
    {
        Assert.Equal(DatabaseSynchronous.Full, new MyDupFinderScanJobDTO().DatabaseSynchronous);
        Assert.Equal(DatabaseSynchronous.Full, new MyDupFinderRefreshJobDTO().DatabaseSynchronous);
        Assert.Equal(DatabaseSynchronous.Full, new MyDupFinderCheckJobDTO().ScanJobDTO.DatabaseSynchronous);
    }

    [Fact]
    public void GetExampleDTO_UsesFull()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);

        Assert.Equal(DatabaseSynchronous.Full, dto.MyDupFinderScanJobDTOs[0].DatabaseSynchronous);
        Assert.Equal(DatabaseSynchronous.Full, dto.MyDupFinderCheckJobDTOs[0].ScanJobDTO.DatabaseSynchronous);
        Assert.All(dto.MyDupFinderRefreshJobDTOs, r => Assert.Equal(DatabaseSynchronous.Full, r.DatabaseSynchronous));
    }

    [Theory]
    [InlineData(DatabaseSynchronous.Full)]
    [InlineData(DatabaseSynchronous.Normal)]
    public void CheckSanity_AcceptsTheDefinedValues(DatabaseSynchronous value)
    {
        MyDupFinderScanJobDTO.CheckSanity(CreateScanJob(value));
        MyDupFinderRefreshJobDTO.CheckSanity(CreateRefreshJob(value));
        MyDupFinderCheckJobDTO.CheckSanity(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob(value) });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void CheckSanity_RejectsUndefinedValues(int value)
    {
        var scan = Assert.Throws<ParameterException>(() => MyDupFinderScanJobDTO.CheckSanity(CreateScanJob((DatabaseSynchronous)value)));
        Assert.Contains("DatabaseSynchronous", scan.Message, StringComparison.Ordinal);

        var refresh = Assert.Throws<ParameterException>(() => MyDupFinderRefreshJobDTO.CheckSanity(CreateRefreshJob((DatabaseSynchronous)value)));
        Assert.Contains("DatabaseSynchronous", refresh.Message, StringComparison.Ordinal);

        var check = Assert.Throws<ParameterException>(() => MyDupFinderCheckJobDTO.CheckSanity(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob((DatabaseSynchronous)value) }));
        Assert.Contains("DatabaseSynchronous", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_RoundTrip_KeepsDatabaseSynchronous()
    {
        var project = new MyDupFinderProjectDTO();
        project.MyDupFinderScanJobDTOs.Add(CreateScanJob(DatabaseSynchronous.Normal));
        project.MyDupFinderRefreshJobDTOs.Add(CreateRefreshJob(DatabaseSynchronous.Normal));
        project.MyDupFinderCheckJobDTOs.Add(new MyDupFinderCheckJobDTO { ScanJobDTO = CreateScanJob(DatabaseSynchronous.Normal) });
        string file = Path.Combine(_tempDir, "roundtrip.xml");

        MyDupFinderProject.WriteConfigurationToFile(project, file);
        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.NotNull(read);
        Assert.Equal(DatabaseSynchronous.Normal, Assert.Single(read!.MyDupFinderScanJobDTOs).DatabaseSynchronous);
        Assert.Equal(DatabaseSynchronous.Normal, Assert.Single(read.MyDupFinderRefreshJobDTOs).DatabaseSynchronous);
        Assert.Equal(DatabaseSynchronous.Normal, Assert.Single(read.MyDupFinderCheckJobDTOs).ScanJobDTO.DatabaseSynchronous);
        Assert.Contains("<DatabaseSynchronous>Normal</DatabaseSynchronous>", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_WithoutDatabaseSynchronousElement_LoadsAsFull()
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
              <MyDupFinderRefreshJobDTOs>
                <MyDupFinderRefreshJobDTO>
                  <JobName>Old refresh</JobName>
                </MyDupFinderRefreshJobDTO>
              </MyDupFinderRefreshJobDTOs>
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
        Assert.Equal(DatabaseSynchronous.Full, Assert.Single(read!.MyDupFinderScanJobDTOs).DatabaseSynchronous);
        Assert.Equal(DatabaseSynchronous.Full, Assert.Single(read.MyDupFinderRefreshJobDTOs).DatabaseSynchronous);
        Assert.Equal(DatabaseSynchronous.Full, Assert.Single(read.MyDupFinderCheckJobDTOs).ScanJobDTO.DatabaseSynchronous);
    }

    [Fact]
    public void Xml_WithDatabaseSynchronousElement_LoadsTheValue()
    {
        string file = WriteProjectXml(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <MyDupFinderProjectDTO>
              <MyDupFinderScanJobDTOs>
                <MyDupFinderScanJobDTO>
                  <JobName>Scan</JobName>
                  <DatabaseSynchronous>Normal</DatabaseSynchronous>
                </MyDupFinderScanJobDTO>
              </MyDupFinderScanJobDTOs>
            </MyDupFinderProjectDTO>
            """);

        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.Equal(DatabaseSynchronous.Normal, Assert.Single(read!.MyDupFinderScanJobDTOs).DatabaseSynchronous);
    }
}
