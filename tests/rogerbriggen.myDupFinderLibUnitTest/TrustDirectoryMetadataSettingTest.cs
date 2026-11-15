// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The job setting TrustDirectoryMetadata (default false, same pattern as ThrottlePercent) and the helper that reads the metadata of a file.
/// </summary>
public class TrustDirectoryMetadataSettingTest : IDisposable
{
    private readonly string _tempDir;

    public TrustDirectoryMetadataSettingTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TrustMetaSettingTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string WriteProjectXml(string content)
    {
        string file = Path.Combine(_tempDir, "project_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void TrustDirectoryMetadata_DefaultsToFalse()
    {
        Assert.False(new MyDupFinderScanJobDTO().TrustDirectoryMetadata);
        Assert.False(new MyDupFinderRefreshJobDTO().TrustDirectoryMetadata);
        Assert.False(new MyDupFinderCheckJobDTO().ScanJobDTO.TrustDirectoryMetadata);
    }

    [Fact]
    public void GetExampleDTO_DoesNotTrustDirectoryMetadata()
    {
        MyDupFinderProject.getExampleDTO(out MyDupFinderProjectDTO dto);

        Assert.False(dto.MyDupFinderScanJobDTOs[0].TrustDirectoryMetadata);
        Assert.False(dto.MyDupFinderRefreshJobDTOs[0].TrustDirectoryMetadata);
        Assert.False(dto.MyDupFinderCheckJobDTOs[0].ScanJobDTO.TrustDirectoryMetadata);
    }

    [Fact]
    public void Xml_RoundTrip_KeepsTrustDirectoryMetadata()
    {
        var project = new MyDupFinderProjectDTO();
        project.MyDupFinderScanJobDTOs.Add(new MyDupFinderScanJobDTO { JobName = "Scan", TrustDirectoryMetadata = true });
        project.MyDupFinderRefreshJobDTOs.Add(new MyDupFinderRefreshJobDTO { JobName = "Refresh", TrustDirectoryMetadata = true });
        project.MyDupFinderCheckJobDTOs.Add(new MyDupFinderCheckJobDTO { ScanJobDTO = new MyDupFinderScanJobDTO { JobName = "Check", TrustDirectoryMetadata = true } });
        string file = Path.Combine(_tempDir, "roundtrip.xml");

        MyDupFinderProject.WriteConfigurationToFile(project, file);
        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.NotNull(read);
        Assert.True(Assert.Single(read!.MyDupFinderScanJobDTOs).TrustDirectoryMetadata);
        Assert.True(Assert.Single(read.MyDupFinderRefreshJobDTOs).TrustDirectoryMetadata);
        Assert.True(Assert.Single(read.MyDupFinderCheckJobDTOs).ScanJobDTO.TrustDirectoryMetadata);
    }

    [Fact]
    public void Xml_RoundTrip_KeepsFalse()
    {
        var project = new MyDupFinderProjectDTO();
        project.MyDupFinderScanJobDTOs.Add(new MyDupFinderScanJobDTO { JobName = "Scan" });
        project.MyDupFinderRefreshJobDTOs.Add(new MyDupFinderRefreshJobDTO { JobName = "Refresh" });
        string file = Path.Combine(_tempDir, "roundtrip_false.xml");

        MyDupFinderProject.WriteConfigurationToFile(project, file);
        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.False(Assert.Single(read!.MyDupFinderScanJobDTOs).TrustDirectoryMetadata);
        Assert.False(Assert.Single(read.MyDupFinderRefreshJobDTOs).TrustDirectoryMetadata);
    }

    [Fact]
    public void Xml_WithoutTheElement_LoadsAsFalse()
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
                  <ThrottlePercent>10</ThrottlePercent>
                </MyDupFinderScanJobDTO>
              </MyDupFinderScanJobDTOs>
              <MyDupFinderCheckJobDTOs>
                <MyDupFinderCheckJobDTO>
                  <ScanJobDTO>
                    <JobName>Old check</JobName>
                  </ScanJobDTO>
                  <IgnoreBasePath>false</IgnoreBasePath>
                  <SkipHashCheck>true</SkipHashCheck>
                </MyDupFinderCheckJobDTO>
              </MyDupFinderCheckJobDTOs>
              <MyDupFinderRefreshJobDTOs>
                <MyDupFinderRefreshJobDTO>
                  <JobName>Old refresh</JobName>
                </MyDupFinderRefreshJobDTO>
              </MyDupFinderRefreshJobDTOs>
            </MyDupFinderProjectDTO>
            """);

        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.NotNull(read);
        Assert.False(Assert.Single(read!.MyDupFinderScanJobDTOs).TrustDirectoryMetadata);
        Assert.False(Assert.Single(read.MyDupFinderRefreshJobDTOs).TrustDirectoryMetadata);
        Assert.False(Assert.Single(read.MyDupFinderCheckJobDTOs).ScanJobDTO.TrustDirectoryMetadata);
    }

    [Fact]
    public void Xml_WithTheElement_LoadsTheValue()
    {
        string file = WriteProjectXml(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <MyDupFinderProjectDTO>
              <MyDupFinderScanJobDTOs>
                <MyDupFinderScanJobDTO>
                  <JobName>Scan</JobName>
                  <TrustDirectoryMetadata>true</TrustDirectoryMetadata>
                </MyDupFinderScanJobDTO>
              </MyDupFinderScanJobDTOs>
            </MyDupFinderProjectDTO>
            """);

        MyDupFinderProject.ReadConfigurationFromFile(file, out MyDupFinderProjectDTO? read);

        Assert.True(Assert.Single(read!.MyDupFinderScanJobDTOs).TrustDirectoryMetadata);
    }

    // ---- FileMetadata: one call per file, same values as the three single calls of the code before step 4 ----

    [Fact]
    public void FileMetadataRead_ReturnsTheSameValuesAsTheSingleFileApis()
    {
        var times = new List<DateTime>
        {
            new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567),
            new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1),
            new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(10_000),
            new DateTime(2200, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(5_000_000),
            new DateTime(2023, 7, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(7654321),
            new DateTime(2023, 1, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(7654321),
            new DateTime(2023, 3, 26, 1, 30, 0, DateTimeKind.Utc).AddTicks(500),
            new DateTime(2023, 10, 29, 0, 30, 0, DateTimeKind.Utc).AddTicks(500),
        };
        for (int i = 0; i < times.Count; i++)
        {
            string path = Path.Combine(_tempDir, $"file{i}.bin");
            File.WriteAllBytes(path, new byte[i * 101]);
            File.SetCreationTimeUtc(path, times[i]);
            File.SetLastWriteTimeUtc(path, times[(i + 2) % times.Count]);

            FileEntryInfo read = FileMetadata.Read(path);

            Assert.Equal(path, read.FullPath);
            Assert.Equal(new FileInfo(path).Length, read.Length);
            Assert.Equal(DateTimeKind.Utc, read.CreationTimeUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, read.LastWriteTimeUtc.Kind);
            Assert.Equal(File.GetCreationTimeUtc(path).Ticks, read.CreationTimeUtc.Ticks);
            Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, read.LastWriteTimeUtc.Ticks);
            Assert.Equal(times[i].Ticks, read.CreationTimeUtc.Ticks);
            Assert.Equal(times[(i + 2) % times.Count].Ticks, read.LastWriteTimeUtc.Ticks);
        }
    }

    [Fact]
    public void FileMetadataRead_AgreesWithTheDirectoryListing_ForTheSameFiles()
    {
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_tempDir, $"f{i}.txt"), new string('x', i));
        }
        using var enumeration = new FileTreeEnumeration(_tempDir);
        int count = 0;
        while (enumeration.MoveNext())
        {
            Assert.Equal(enumeration.Current, FileMetadata.Read(enumeration.Current.FullPath));
            count++;
        }
        Assert.Equal(5, count);
    }

    [Fact]
    public void FileMetadataRead_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => FileMetadata.Read(Path.Combine(_tempDir, "missing.bin")));
    }

    [Fact]
    public void FileMetadataResolve_TrustedUsesTheListedValuesWithoutTouchingTheFile()
    {
        var listed = new FileEntryInfo(Path.Combine(_tempDir, "does-not-exist.bin"), 5, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(listed, FileMetadata.Resolve(listed, trustDirectoryMetadata: true));
        Assert.Throws<FileNotFoundException>(() => FileMetadata.Resolve(listed, trustDirectoryMetadata: false));
    }
}
