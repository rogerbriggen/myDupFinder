// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            _dbContext.Database.Migrate();
            _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        }

    }


    //public IList<ScanItemDto> FindDupsInSameDB()
    public List<ScanItemDto>? FindDupsInSameDB()
    {
        if (_dbContext is null)
        {
            throw new InvalidOperationException("IsAlreadyInDB called without SetupDB!");
        }
        lock (dbContextLock)
        {

            //var duplicates = _dbContext.ScanItems?.GroupBy(i => i.FileSha512Hash)
            //     .Where(x => x.Count() > 1)
            //     .ToList

            var duplicates = _dbContext.ScanItems?
                .FromSqlInterpolated($"SELECT * FROM ScanItems WHERE FileSha512Hash IN (SELECT FileSha512Hash FROM ScanItems GROUP BY FileSHA512Hash HAVING COUNT(*) >1)")
                .ToList();
            return duplicates;
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
