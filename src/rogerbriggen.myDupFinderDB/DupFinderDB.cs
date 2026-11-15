// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderDB;

public class DubFinderDB : IDisposable
{

    private DubFinderContext? _dbContext;
    private DbContextOptions<DubFinderContext>? _dbContextOptions;

    private readonly object dbContextLock = new object();
    private readonly ILogger<DubFinderDB> _logger;

    private bool _disposedValue;


    public DubFinderDB(ILogger<DubFinderDB>? logger) => _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<DubFinderDB>();
    public void SetupDB(string databaseFile)
    {
        lock (dbContextLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databaseFile) ?? "");
            _dbContextOptions = DubFinderContextFactory.GetDbContextOptions(databaseFile);
            _dbContext = new DubFinderContext(_dbContextOptions);
            DatabaseMigrator.Migrate(_dbContext, _logger, databaseFile);
            _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        }

    }


    //public IList<ScanItemDto> FindDupsInSameDB()
    public List<ScanItemDto>? FindDupsInSameDB()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("FindDupsInSameDB called without SetupDB!");
        }
        lock (dbContextLock)
        {

            //var duplicates = _dbContext.ScanItems?.GroupBy(i => i.FileSha512Hash)
            //     .Where(x => x.Count() > 1)
            //     .ToList

            return FindDupsInSameDB(_dbContext);
        }
    }

    // The queries are static and take the context, so the tests can run them with the real code and look at the SQL and the query plan.

    // All queries that feed the duplicate report return the rows in one total order: hash, path, primary key.
    // The primary key makes the order total (two rows can have the same hash and the same path), so the report does
    // not depend on the order in which the rows were inserted. The GroupIds and the pairing in the report are handed
    // out in this order, see the documentation of the two find-dups runners. The rows are only read, so they are not tracked.

    internal static List<ScanItemDto> FindDupsInSameDB(DubFinderContext context) =>
        context.Set<ScanItemDto>()
            .FromSqlInterpolated($"SELECT * FROM ScanItems WHERE FileSha512Hash IN (SELECT FileSha512Hash FROM ScanItems GROUP BY FileSHA512Hash HAVING COUNT(*) >1) ORDER BY FileSha512Hash, FilenameAndPath, Id")
            .AsNoTracking()
            .ToList();

    internal static List<ScanItemDto> GetAllScanItems(DubFinderContext context) =>
        context.Set<ScanItemDto>()
            .AsNoTracking()
            .OrderBy(s => s.FileSha512Hash)
            .ThenBy(s => s.FilenameAndPath)
            .ThenBy(s => s.Id)
            .ToList();

    /// <summary>
    /// Name under which the second database is attached to the connection of the base database during
    /// <see cref="FindDupsInDifferentDBs(DubFinderContext, string)"/>.
    /// </summary>
    internal const string SecondDatabaseAlias = "dupfinder_second";

    /// <summary>
    /// Returns the rows of the base database whose (non-empty) hash also exists in the second database, ordered by hash, path and
    /// primary key. Returns null if the second database has no non-empty hash at all.
    /// <para>
    /// The second database file is attached to the connection of <paramref name="context"/> (<c>ATTACH DATABASE</c>, the
    /// path is passed as a parameter) and both hash indexes are used by one query, in the form (<see cref="HashMatchForm"/>) that
    /// suits the sizes of the two tables. The second database is only read: it is not
    /// migrated, no pragma is changed and no file is created (a missing file is an error, checked before attaching). The
    /// attachment is removed again in every case, and the connection is left as it was found (closed, if it was closed before).
    /// </para>
    /// <para>
    /// The attachment is not read-only at the SQLite level: that needs a URI (<c>file:...?mode=ro</c>), and the SQLite provider
    /// only accepts URIs in <c>ATTACH</c> if the connection itself was opened with a <c>file:</c> data source.
    /// </para>
    /// </summary>
    /// <exception cref="FileNotFoundException">The second database file does not exist.</exception>
    internal static List<ScanItemDto>? FindDupsInDifferentDBs(DubFinderContext context, string secondDatabaseFile) =>
        FindDupsInDifferentDBs(context, secondDatabaseFile, null);

    /// <summary>
    /// The two SQL forms of the cross-database match. SQLite has no table statistics here and the second database must not be
    /// analyzed, so the planner does not choose between them: <see cref="Exists"/> always walks the whole hash index of the base
    /// database and probes the second one per row (good when the base database is the smaller one or about as big),
    /// <see cref="In"/> walks the hash index of the second database and seeks every hash in the base index (good when the second
    /// database is much smaller). Both return exactly the same rows in the same order.
    /// </summary>
    internal enum HashMatchForm
    {
        Exists,
        In,
    }

    /// <summary>
    /// Measured crossover: with a second database a third of the size of the base database or smaller the <c>IN</c> form is faster, from
    /// about a quarter it is the same, above that the <c>EXISTS</c> form is.
    /// </summary>
    private const int InFormSizeRatio = 3;

    /// <summary>
    /// Picks the form from the size of both tables. The size is estimated with <c>MAX(rowid)</c>, which is one seek to the end of the
    /// table (a <c>COUNT(*)</c> scans an index, which is linear in the number of rows). It is an upper bound of the row count, a table
    /// with many deleted rows looks bigger; a wrong choice only costs time, never changes the result. The connection must have the
    /// second database attached.
    /// </summary>
    private static HashMatchForm ChooseForm(DubFinderContext context)
    {
        long baseSize = context.Database.SqlQuery<long>($"SELECT COALESCE(MAX(rowid), 0) AS Value FROM main.ScanItems").Single();
        long secondSize = context.Database.SqlQuery<long>($"SELECT COALESCE(MAX(rowid), 0) AS Value FROM dupfinder_second.ScanItems").Single();
        return secondSize * InFormSizeRatio < baseSize ? HashMatchForm.In : HashMatchForm.Exists;
    }

    /// <param name="form">The SQL form to use, or null to choose it from the sizes of the two tables (tests force a form).</param>
    internal static List<ScanItemDto>? FindDupsInDifferentDBs(DubFinderContext context, string secondDatabaseFile, HashMatchForm? form)
    {
        ArgumentNullException.ThrowIfNull(context);
        string attachTarget = GetExistingDatabaseFile(secondDatabaseFile);

        // ATTACH belongs to the connection, and EF opens and closes the connection around every command by default.
        // So the connection is opened here and stays open until the attachment is removed again.
        var connection = context.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            context.Database.OpenConnection();
        }
        bool attached = false;
        try
        {
            context.Database.ExecuteSql($"ATTACH DATABASE {attachTarget} AS dupfinder_second");
            attached = true;

            // Rows without a hash never match.
            var chosen = form ?? ChooseForm(context);
            var items = (chosen == HashMatchForm.In
                    ? context.Set<ScanItemDto>().FromSql($"SELECT s.* FROM ScanItems AS s WHERE s.FileSha512Hash IN (SELECT o.FileSha512Hash FROM dupfinder_second.ScanItems AS o WHERE o.FileSha512Hash <> '') ORDER BY s.FileSha512Hash, s.FilenameAndPath, s.Id")
                    : context.Set<ScanItemDto>().FromSql($"SELECT s.* FROM ScanItems AS s WHERE s.FileSha512Hash <> '' AND EXISTS (SELECT 1 FROM dupfinder_second.ScanItems AS o WHERE o.FileSha512Hash = s.FileSha512Hash) ORDER BY s.FileSha512Hash, s.FilenameAndPath, s.Id"))
                .AsNoTracking()
                .ToList();
            if (items.Count > 0)
            {
                return items;
            }

            // No match: null if the second database has no non-empty hash at all, an empty list otherwise
            bool secondHasHashes = context.Database
                .SqlQuery<int>($"SELECT EXISTS (SELECT 1 FROM dupfinder_second.ScanItems WHERE FileSha512Hash <> '') AS Value")
                .Single() != 0;
            return secondHasHashes ? items : null;
        }
        finally
        {
            try
            {
                if (attached)
                {
                    context.Database.ExecuteSql($"DETACH DATABASE dupfinder_second");
                }
            }
            catch (SqliteException)
            {
                // Do not hide the result or the original exception. A connection that is still attached must not go back
                // to the connection pool, where the next user of the pool would find the second database attached.
                if (connection is SqliteConnection sqliteConnection)
                {
                    SqliteConnection.ClearPool(sqliteConnection);
                }
            }
            finally
            {
                if (openedHere)
                {
                    context.Database.CloseConnection();
                }
            }
        }
    }

    /// <summary>
    /// Returns the full path of an existing database file. SQLite creates a missing file when it opens or attaches one, which
    /// must not happen for the second database of a comparison, so a missing file is an error here.
    /// </summary>
    private static string GetExistingDatabaseFile(string databaseFile)
    {
        if (string.IsNullOrWhiteSpace(databaseFile))
        {
            throw new ArgumentException("secondDatabaseFile may not be null or empty", nameof(databaseFile));
        }
        string fullPath = Path.GetFullPath(databaseFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The database file {fullPath} does not exist. It is not created.", fullPath);
        }
        return fullPath;
    }

    /// <summary>
    /// Returns every ScanItem from the current (base) database, ordered by hash, path and primary key.
    /// Used by FindDupsTheWholeLot to categorize each file as Duplicate or Unique.
    /// </summary>
    public List<ScanItemDto>? GetAllScanItems()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("GetAllScanItems called without SetupDB!");
        }
        lock (dbContextLock)
        {
            return GetAllScanItems(_dbContext);
        }
    }

    /// <summary>
    /// Returns every ScanItem from another database file, ordered by hash, path and primary key.
    /// Used by FindDupsTheWholeLot in cross-DB mode.
    /// </summary>
    /// <exception cref="FileNotFoundException">The database file does not exist (it is not created).</exception>
    public List<ScanItemDto>? GetAllScanItemsFromOtherDB(string secondDatabaseFile)
    {
        if (string.IsNullOrWhiteSpace(secondDatabaseFile))
        {
            throw new ArgumentException("secondDatabaseFile may not be null or empty", nameof(secondDatabaseFile));
        }
        GetExistingDatabaseFile(secondDatabaseFile);

        using var secondDbContext = DubFinderContextFactory.CreateDubFinderContext(secondDatabaseFile);
        return secondDbContext.ScanItems?
            .AsNoTracking()
            .OrderBy(s => s.FileSha512Hash)
            .ThenBy(s => s.FilenameAndPath)
            .ThenBy(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// Finds duplicate files across two different databases by comparing SHA-512 hashes.
    /// Returns items from the base (current) database that have matching hashes in the second database, ordered by hash, path
    /// and primary key. The second database is attached to the connection of the base database and only read.
    /// </summary>
    /// <param name="secondDatabaseFile">Path to the second database file to compare against. The file must exist.</param>
    /// <returns>A list of items from the base database whose hash exists in the second database (empty if there is none), or null if the second database has no non-empty hash.</returns>
    /// <exception cref="FileNotFoundException">The second database file does not exist (it is not created).</exception>
    public List<ScanItemDto>? FindDupsInDifferentDBs(string secondDatabaseFile)
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("FindDupsInDifferentDBs called without SetupDB!");
        }
        if (string.IsNullOrWhiteSpace(secondDatabaseFile))
        {
            throw new ArgumentException("secondDatabaseFile may not be null or empty", nameof(secondDatabaseFile));
        }

        lock (dbContextLock)
        {
            return FindDupsInDifferentDBs(_dbContext, secondDatabaseFile);
        }
    }


    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // TODO: Verwalteten Zustand (verwaltete Objekte) bereinigen
                if (_dbContext is not null)
                {
                    lock (dbContextLock)
                    {
                        _dbContext.SaveChanges();
                        _dbContext.Dispose();
                    }
                }
            }

            // TODO: Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
            // TODO: Große Felder auf NULL setzen
            _disposedValue = true;
        }
    }

    // // TODO: Finalizer nur überschreiben, wenn "Dispose(bool disposing)" Code für die Freigabe nicht verwalteter Ressourcen enthält
    // ~ScanRunner()
    // {
    //     // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
