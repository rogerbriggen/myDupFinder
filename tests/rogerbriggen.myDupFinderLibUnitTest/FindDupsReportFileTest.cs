// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The report file of a find-duplicates job: one place builds the path, the check and the writer never overwrite an existing report.
/// </summary>
public class FindDupsReportFileTest : IDisposable
{
    private const string ExistingContent = "do not touch\r\n";

    private readonly string _tempDir;
    private readonly string _baseDbPath;
    private readonly string _secondDbPath;

    public FindDupsReportFileTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FindDupsReportFileTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _baseDbPath = Path.Combine(_tempDir, "base.db");
        _secondDbPath = Path.Combine(_tempDir, "second.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private static string WithSeparator(string path) => path + Path.DirectorySeparatorChar;

    private static ScanItemDto Item(string fullPath, string hash) => new ScanItemDto
    {
        Id = Guid.NewGuid(),
        FilenameAndPath = fullPath,
        PathBase = Path.GetDirectoryName(fullPath) ?? string.Empty,
        ScanExecutionComputer = "TestPC",
        OriginComputer = "TestPC",
        ScanName = "TestScan",
        FileSize = 10,
        FileSha512Hash = hash,
        FileCreationUTC = DateTime.UtcNow,
        FileLastModificationUTC = DateTime.UtcNow,
        FirstScanDateUTC = DateTime.UtcNow,
        LastScanDateUTC = DateTime.UtcNow,
        LastSha512ScanDateUTC = DateTime.UtcNow,
    };

    private static void PopulateDatabase(string dbPath, params ScanItemDto[] items)
    {
        using var dbInserts = new ScanJobDBInserts(null);
        dbInserts.SetupDB(dbPath);
        foreach (var item in items)
        {
            dbInserts.Enqueue(item);
        }
        dbInserts.WriteChanges();
    }

    private string ExistingReport(string jobName)
    {
        string report = Path.Combine(_tempDir, jobName + " dupReport.csv");
        File.WriteAllText(report, ExistingContent);
        return report;
    }

    // ----- path building and validation -----

    [Fact]
    public void BuildReportFilePath_WithAndWithoutTrailingSeparator_GivesTheSameName()
    {
        string expected = Path.Combine(_tempDir, "job dupReport.csv");
        Assert.Equal(expected, MyDupFinderFindDupsJobDTO.BuildReportFilePath(_tempDir, "job"));
        Assert.Equal(expected, MyDupFinderFindDupsJobDTO.BuildReportFilePath(WithSeparator(_tempDir), "job"));
        Assert.Equal(expected, MyDupFinderFindDupsJobDTO.BuildReportFilePath(_tempDir + Path.AltDirectorySeparatorChar, "job").Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
    }

    [Fact]
    public void GetReportFilePath_IsReportPathPlusJobNamePlusSuffix_AfterFixDto()
    {
        var dto = new MyDupFinderFindDupsJobDTO { JobName = "job", ReportPath = _tempDir };
        string before = dto.GetReportFilePath();
        MyDupFinderFindDupsJobDTO.FixDto(dto);

        Assert.Equal(before, dto.GetReportFilePath());
        Assert.Equal(dto.ReportPath + "job dupReport.csv", dto.GetReportFilePath());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckSanity_ShouldThrow_WhenTheReportExists_WithAndWithoutTrailingSeparator(bool trailingSeparator)
    {
        File.WriteAllText(_baseDbPath, "dummy");
        ExistingReport("job");
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "job",
            DatabaseFileBase = _baseDbPath,
            ReportPath = trailingSeparator ? WithSeparator(_tempDir) : _tempDir,
        };

        var ex = Assert.Throws<ParameterException>(() => MyDupFinderFindDupsJobDTO.CheckSanity(dto, new List<string>()));

        Assert.Contains("Report already existing", ex.Message);
        Assert.Contains(Path.Combine(_tempDir, "job dupReport.csv"), ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckSanity_ShouldNotThrow_WhenAnotherReportExists(bool trailingSeparator)
    {
        File.WriteAllText(_baseDbPath, "dummy");
        ExistingReport("otherjob");
        var dto = new MyDupFinderFindDupsJobDTO
        {
            JobName = "job",
            DatabaseFileBase = _baseDbPath,
            ReportPath = trailingSeparator ? WithSeparator(_tempDir) : _tempDir,
        };

        MyDupFinderFindDupsJobDTO.CheckSanity(dto, new List<string>());
    }

    // ----- writer -----

    [Fact]
    public void DupReportWriter_RefusesToOverwrite_AndLeavesTheExistingFileUnchanged()
    {
        string report = ExistingReport("job");
        var rows = new List<DupReportRow> { new DupReportRow(Item("/r/a.txt", "H"), DupReportCategory.Duplicate, DupReportSource.Base, 1) };

        var ex = Assert.Throws<ReportAlreadyExistsException>(() => DupReportWriter.Write(report, rows));

        Assert.Equal(report, ex.ReportFile);
        Assert.Equal(ExistingContent, File.ReadAllText(report));
    }

    [Fact]
    public void IsFileExists_RecognisesTheIOExceptionOfACreateNewOpenOfAnExistingFile()
    {
        string report = ExistingReport("job");

        var ex = Assert.Throws<IOException>(() => new FileStream(report, FileMode.CreateNew, FileAccess.Write).Dispose());

        Assert.True(DupReportWriter.IsFileExists(ex), $"HResult 0x{ex.HResult:X8}");
        // another IOException (directory does not exist) is not "already exists"
        var other = Assert.ThrowsAny<IOException>(() => new FileStream(Path.Combine(_tempDir, "missing", "x.csv"), FileMode.CreateNew, FileAccess.Write).Dispose());
        Assert.False(DupReportWriter.IsFileExists(other), $"HResult 0x{other.HResult:X8}");
    }

    private static IEnumerable<DupReportRow> RowsThatFailWhileEnumerated(DupReportRow first)
    {
        yield return first;
        throw new IOException("simulated write failure");
    }

    [Fact]
    public void DupReportWriter_FailureAfterTheFileWasCreated_IsNotReportedAsAlreadyExisting()
    {
        string report = Path.Combine(_tempDir, "partial.csv");
        var first = new DupReportRow(Item("/r/a.txt", "H"), DupReportCategory.Duplicate, DupReportSource.Base, 1);

        var ex = Assert.Throws<ReportWriteFailedException>(() => DupReportWriter.Write(report, RowsThatFailWhileEnumerated(first)));

        Assert.IsNotType<ReportAlreadyExistsException>(ex);
        Assert.Equal(report, ex.ReportFile);
        Assert.Equal("simulated write failure", ex.InnerException!.Message);
        Assert.True(File.Exists(report)); // the partial file is left, not deleted
    }

    [Fact]
    public void DupReportWriter_WritesANewFile_WithHeaderAndNoBom()
    {
        string report = Path.Combine(_tempDir, "new.csv");
        var rows = new List<DupReportRow> { new DupReportRow(Item("/r/a.txt", "H"), DupReportCategory.Duplicate, DupReportSource.Base, 1) };

        DupReportWriter.Write(report, rows);

        byte[] bytes = File.ReadAllBytes(report);
        Assert.Equal((byte)'F', bytes[0]);
        Assert.Equal(new[] { DupReportWriter.HeaderLine, "\"/r/a.txt\";10;H;Duplicate;Base;1" }, File.ReadAllLines(report));
    }

    // ----- runners -----

    private static (T Runner, ListLoggerProvider Log) CreateRunner<T>(Func<ILogger<T>, T> create)
    {
        var provider = new ListLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        return (create(factory.CreateLogger<T>()), provider);
    }

    private MyDupFinderFindDupsJobDTO CreateJob(string reportPath, bool crossDb)
    {
        PopulateDatabase(_baseDbPath, Item("/base/a.txt", "HASH_A"), Item("/base/b.txt", "HASH_A"));
        if (crossDb)
        {
            PopulateDatabase(_secondDbPath, Item("/second/a.txt", "HASH_A"));
        }
        return new MyDupFinderFindDupsJobDTO
        {
            JobName = "job",
            DatabaseFileBase = _baseDbPath,
            DatabaseFile = crossDb ? _secondDbPath : string.Empty,
            ReportPath = reportPath,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameDbRunner_ExistingReport_EndsWithAnErrorLog_AndLeavesTheFileUnchanged(bool trailingSeparator)
    {
        var dto = CreateJob(trailingSeparator ? WithSeparator(_tempDir) : _tempDir, crossDb: false);
        string report = ExistingReport("job");
        var (runner, log) = CreateRunner<FindDupsInSameDB>(l => new FindDupsInSameDB(dto, l, new NullServiceProvider()));

        using (runner)
        {
            runner.Start(CancellationToken.None); // does not throw: the error is logged like the other runners do
        }

        Assert.Equal(ExistingContent, File.ReadAllText(report));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("already exists and is not overwritten") && e.Message.Contains(report));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("successfully created"));
        Assert.Single(Directory.GetFiles(_tempDir, "*.csv"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentDbsRunner_ExistingReport_EndsWithAnErrorLog_AndLeavesTheFileUnchanged(bool trailingSeparator)
    {
        var dto = CreateJob(trailingSeparator ? WithSeparator(_tempDir) : _tempDir, crossDb: true);
        string report = ExistingReport("job");
        var (runner, log) = CreateRunner<FindDupsInDifferentDBs>(l => new FindDupsInDifferentDBs(dto, l, new NullServiceProvider()));

        using (runner)
        {
            runner.Start(CancellationToken.None);
        }

        Assert.Equal(ExistingContent, File.ReadAllText(report));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("already exists and is not overwritten") && e.Message.Contains(report));
        Assert.Single(Directory.GetFiles(_tempDir, "*.csv"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Runners_NormalRun_WriteTheSameFileNameAsBefore(bool trailingSeparator, bool crossDb)
    {
        var dto = CreateJob(trailingSeparator ? WithSeparator(_tempDir) : _tempDir, crossDb);
        var provider = new ListLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        if (crossDb)
        {
            using var runner = new FindDupsInDifferentDBs(dto, factory.CreateLogger<FindDupsInDifferentDBs>(), new NullServiceProvider());
            runner.Start(CancellationToken.None);
        }
        else
        {
            using var runner = new FindDupsInSameDB(dto, factory.CreateLogger<FindDupsInSameDB>(), new NullServiceProvider());
            runner.Start(CancellationToken.None);
        }

        // <ReportPath><JobName> dupReport.csv
        string expected = Path.Combine(_tempDir, "job dupReport.csv");
        Assert.True(File.Exists(expected));
        Assert.Equal(new[] { expected }, Directory.GetFiles(_tempDir, "*.csv"));
        Assert.Equal(DupReportWriter.HeaderLine, File.ReadLines(expected).First());
        Assert.DoesNotContain(provider.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Runner_AfterAFailedRun_ReleasesTheDatabase()
    {
        var dto = CreateJob(_tempDir, crossDb: false);
        ExistingReport("job");
        var (runner, _) = CreateRunner<FindDupsInSameDB>(l => new FindDupsInSameDB(dto, l, new NullServiceProvider()));
        using (runner)
        {
            runner.Start(CancellationToken.None);
        }

        SqliteConnection.ClearAllPools();
        File.Delete(_baseDbPath); // would fail if the runner still held the database
        Assert.False(File.Exists(_baseDbPath));
    }
}
