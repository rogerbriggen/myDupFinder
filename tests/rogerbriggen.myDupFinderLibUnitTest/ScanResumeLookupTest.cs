// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RogerBriggen.MyDupFinderData;
using RogerBriggen.MyDupFinderDB;
using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// The resume filter (one IsAlreadyInDB query per file) must only run when the database already has rows of this base path.
/// Observed with the counter ResumeLookupCount of ScanJobDBInserts, which the test passes to the runner.
/// </summary>
public class ScanResumeLookupTest : IDisposable
{
    private const int FileCount = 15;

    private readonly string _tempDir;
    private readonly string _dbPath;

    public ScanResumeLookupTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ScanResumeLookup_" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDir, "db", "scan.db");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string MakeTree(string name)
    {
        string root = Path.Combine(_tempDir, name);
        TestTreeGenerator.Generate(root, new TestTreeOptions
        {
            FileCount = FileCount,
            DuplicateShare = 0,
            DirectoryCount = 2,
            SizeBuckets = new[] { new SizeBucket(1.0, 64, 300) },
        });
        return root;
    }

    /// <summary>Runs a scan of the base path and returns the number of resume lookups it made.</summary>
    private int Scan(string basePath, int hashWorkers = 1)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var serviceProvider = services.BuildServiceProvider();
        var dbInserts = new ScanJobDBInserts(serviceProvider.GetService<ILogger<ScanJobDBInserts>>());
        var job = new MyDupFinderScanJobDTO
        {
            JobName = "ResumeLookup",
            OriginComputer = "TestPC",
            ScanName = "TestScan",
            BasePath = basePath,
            DatabaseFile = _dbPath,
            ReportPath = _tempDir,
            HashWorkers = hashWorkers,
        };
        var runner = new ScanRunner(job, serviceProvider.GetService<ILogger<ScanRunner>>(), serviceProvider, dbInserts);
        if (hashWorkers > 1)
        {
            // The pipeline: on its own thread and guarded by a timeout, so that a deadlock fails the test
            PipelineTestSupport.StartGuarded(runner, CancellationToken.None);
        }
        else
        {
            runner.Start(CancellationToken.None);
        }
        Assert.Equal(IService.EServiceState.finished, runner.RunnerState);
        SqliteConnection.ClearAllPools();
        return dbInserts.ResumeLookupCount;
    }

    private int RowCount()
    {
        using var context = DubFinderContextFactory.CreateDubFinderContext(_dbPath);
        return context.ScanItems!.AsNoTracking().Count();
    }

    [Fact]
    public void FirstScanIntoAnEmptyDatabase_DoesNoResumeLookup()
    {
        string tree = MakeTree("treeA");

        int lookups = Scan(tree);

        Assert.Equal(0, lookups);
        Assert.Equal(FileCount, RowCount());
    }

    [Fact]
    public void ScanOfABasePathThatIsNotInTheDatabaseYet_DoesNoResumeLookup()
    {
        string treeA = MakeTree("treeA");
        string treeB = MakeTree("treeB");
        Assert.Equal(0, Scan(treeA));

        int lookups = Scan(treeB);

        Assert.Equal(0, lookups);
        Assert.Equal(2 * FileCount, RowCount());
    }

    [Fact]
    public void SecondScanOfTheSameBasePath_DoesOneResumeLookupPerFile_AndAddsNothing()
    {
        string tree = MakeTree("treeA");
        Assert.Equal(0, Scan(tree));

        int lookups = Scan(tree);

        Assert.Equal(FileCount, lookups);
        Assert.Equal(FileCount, RowCount());
    }

    // ---- the same rules with the pipeline (4 hash workers) ----

    [Fact]
    public void Pipeline_FirstScanIntoAnEmptyDatabase_DoesNoResumeLookup()
    {
        string tree = MakeTree("treeA");

        int lookups = Scan(tree, hashWorkers: 4);

        Assert.Equal(0, lookups);
        Assert.Equal(FileCount, RowCount());
    }

    [Fact]
    public void Pipeline_ScanOfABasePathThatIsNotInTheDatabaseYet_DoesNoResumeLookup()
    {
        string treeA = MakeTree("treeA");
        string treeB = MakeTree("treeB");
        Assert.Equal(0, Scan(treeA, hashWorkers: 4));

        int lookups = Scan(treeB, hashWorkers: 4);

        Assert.Equal(0, lookups);
        Assert.Equal(2 * FileCount, RowCount());
    }

    [Fact]
    public void Pipeline_SecondScanOfTheSameBasePath_DoesOneResumeLookupPerFile_AndAddsNothing()
    {
        string tree = MakeTree("treeA");
        Assert.Equal(0, Scan(tree, hashWorkers: 4));

        int lookups = Scan(tree, hashWorkers: 4);

        Assert.Equal(FileCount, lookups);
        Assert.Equal(FileCount, RowCount());
    }

    // ---- changed files: still one lookup per file, and the rows are updated, not added ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void SecondScanAfterFilesChanged_DoesOneResumeLookupPerFile_AndKeepsOneRowPerFile(int hashWorkers)
    {
        string tree = MakeTree("treeA");
        Assert.Equal(0, Scan(tree, hashWorkers));
        var files = Directory.GetFiles(tree, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        // Three files get another size, three keep their size but get new content and a new modification time
        for (int i = 0; i < 3; i++)
        {
            File.AppendAllText(files[i], "more");
            File.WriteAllBytes(files[3 + i], new byte[new FileInfo(files[3 + i]).Length]);
            File.SetLastWriteTimeUtc(files[3 + i], DateTime.UtcNow.AddDays(1 + i));
        }

        int lookups = Scan(tree, hashWorkers);

        Assert.Equal(FileCount, lookups);
        Assert.Equal(FileCount, RowCount());
    }

    [Fact]
    public void ScanWithTheSequentialPathFirstAndThePipelineSecond_AddsNothing()
    {
        string tree = MakeTree("treeA");
        Assert.Equal(0, Scan(tree, hashWorkers: 1));

        int lookups = Scan(tree, hashWorkers: 4);

        Assert.Equal(FileCount, lookups);
        Assert.Equal(FileCount, RowCount());
    }
}
