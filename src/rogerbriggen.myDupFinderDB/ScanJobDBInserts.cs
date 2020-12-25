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

namespace RogerBriggen.MyDupFinderDB
{
    public class ScanJobDBInserts : IDisposable
    {

        private DubFinderContext? _dbContext;
        private DbContextOptions<DubFinderContext>? _dbContextOptions;
        private Queue<ScanItemDto> _finishedScanItemCollection = new Queue<ScanItemDto>();
        private Queue<ScanErrorItemDto> _errorScanItemCollection = new Queue<ScanErrorItemDto>();
        private const int _commitCount = 100; //Save changes every 100 item
        private const bool _recreateContext = true; //Its faster to create a dbIndex after a wile
        private readonly object dbContextLock = new object();
        private readonly ILogger<ScanJobDBInserts> _logger;
        public int TotalSuccessCount { get; private set; }
        public int TotalErrorCount { get; private set; }


        public ScanJobDBInserts(ILogger<ScanJobDBInserts>? logger)
        {
            _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<ScanJobDBInserts>();
        }
        public void SetupDB(string databaseFile)
        {
            lock(dbContextLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(databaseFile) ?? "");
                _dbContextOptions = DubFinderContextFactory.GetDbContextOptions(databaseFile);
                _dbContext = new DubFinderContext(_dbContextOptions);
                _dbContext.Database.Migrate();
                _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
                TotalSuccessCount = 0;
                TotalErrorCount = 0;
            }
            
        }

        public void Enqueue(ScanItemDto si)
        {
            if ((_dbContext is null) || (_dbContextOptions is null))
            {
                throw new InvalidOperationException("Enqueue called without SetupDB!");
            }
            lock (dbContextLock)
            {
                _finishedScanItemCollection.Enqueue(si);
                WriteChangesInternal();
            }
        }

        public void Enqueue(ScanErrorItemDto sei)
        {
            if ((_dbContext is null) || (_dbContextOptions is null))
            {
                throw new InvalidOperationException("Enqueue called without SetupDB!");
            }
            lock (dbContextLock)
            {
                _errorScanItemCollection.Enqueue(sei);
                WriteChangesInternal();
            }
        }

        private void WriteChangesInternal()
        {
            if ((_dbContext is null) || (_dbContextOptions is null))
            {
                throw new InvalidOperationException("Enqueue or WriteChanges called without SetupDB!");
            }
            lock (dbContextLock)
            {
                foreach (ScanItemDto siInList in _finishedScanItemCollection)
                {
                    _dbContext.Add(siInList);
                }
                foreach (ScanErrorItemDto seiInList in _errorScanItemCollection)
                {
                    _dbContext.Add(seiInList);
                }
                int written = _dbContext.SaveChanges();
                if (written == _finishedScanItemCollection.Count + _errorScanItemCollection.Count)
                {
                    TotalSuccessCount += _finishedScanItemCollection.Count;
                    TotalErrorCount += _errorScanItemCollection.Count;
                    //all ok... we delete all items
                    _finishedScanItemCollection = new Queue<ScanItemDto>();
                    _errorScanItemCollection = new Queue<ScanErrorItemDto>();
                }
                if (_recreateContext && (written % _commitCount == 0))
                {
                    _dbContext.Dispose();
                    _dbContext = new DubFinderContext(_dbContextOptions);
                    _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
                }
            }
        }

        public bool IsEmptyScanItemTable()
        {
            if (_dbContext is null)
            {
                throw new InvalidOperationException("IsEmptyScanItemTable called without SetupDB!");
            }
            lock (dbContextLock)
            {
                var itemCount = _dbContext.ScanItems?.Count();
                if (itemCount is null)
                {
                    return true;
                }
                else if (itemCount == 0)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        public bool IsAlreadyInDB(ScanItemDto si)
        {
            if (_dbContext is null)
            {
                throw new InvalidOperationException("IsAlreadyInDB called without SetupDB!");
            }
            lock (dbContextLock)
            {
                var itemCount = _dbContext.ScanItems?.Where(s => ((s.FilenameAndPath == si.FilenameAndPath) && (s.FileSize == si.FileSize) && (s.ScanExecutionComputer == si.ScanExecutionComputer))).Count();
                if (itemCount is null)
                {
                    return false;
                }
                else if (itemCount == 0)
                {
                    return false;
                }
                else if (itemCount == 1)
                {
                    return true;
                }
                else
                {
                    //Strange
                    _logger.LogWarning($"IsAlreadyInDB had a strange itemCount of {itemCount}...");
                    return true;
                }
            }
            
        }

        public bool IsBasePathAlreadyInDB(string basePath)
        {
            if (_dbContext is null)
            {
                throw new InvalidOperationException("IsBasePathAlreadyInDB called without SetupDB!");
            }
            lock (dbContextLock)
            {
                
                var itemCount = _dbContext.ScanItems?.Where(s => (s.PathBase == basePath)).Count();

                if (itemCount is null)
                {
                    return false;
                }
                else if (itemCount == 0)
                {
                    return false;
                }
                else
                {
                    //There are already some in the database
                    return true;
                }
            }

        }


        public void WriteChanges()
        {
            if (_dbContext is null)
            {
                throw new InvalidOperationException("WriteChanges called without SetupDB!");
            }
            lock (dbContextLock)
            {
                WriteChangesInternal();
            }
        }

        public void Dispose()
        {
            if (_dbContext is not null)
            {
                lock (dbContextLock)
                {
                    _dbContext.SaveChanges();
                    _dbContext.Dispose();
                }
            }
        }
    }
}
