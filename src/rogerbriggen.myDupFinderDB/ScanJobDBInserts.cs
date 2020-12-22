// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderDB
{
    public class ScanJobDBInserts : IDisposable
    {

        private DubFinderContext? _dbContext;
        private DbContextOptions<DubFinderContext>? _dbContextOptions;
        private Queue<ScanItemDto> _finishedScanItemCollection = new Queue<ScanItemDto>();
        private const int _commitCount = 100; //Save changes every 100 item
        private const bool _recreateContext = true; //Its faster to create a dbIndex after a wile
        private readonly object dbContextLock = new object();
        public int TotalCount { get; private set; }
        

        public void SetupDB(string databaseFile)
        {
            lock(dbContextLock)
            {
                _dbContextOptions = DubFinderContextFactory.GetDbContextOptions(databaseFile);
                _dbContext = new DubFinderContext(_dbContextOptions);
                _dbContext.Database.Migrate();
                _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
                TotalCount = 0;
            }
            
        }

        public void Enqueue(ScanItemDto si)
        {
            if ((_dbContext is null) || (_dbContextOptions is null) || (_dbContext.ScanItems is null))
            {
                throw new InvalidOperationException("Enqueue called without SetupDB!");
            }
            lock (dbContextLock)
            {
                _finishedScanItemCollection.Enqueue(si);
                if (_finishedScanItemCollection.Count % _commitCount == 0)
                {
                    foreach(ScanItemDto siInList in _finishedScanItemCollection)
                    {
                        _dbContext.ScanItems.Add(siInList);
                    }
                    int written = _dbContext.SaveChanges();
                    TotalCount += written;
                    if (written == _finishedScanItemCollection.Count)
                    {
                        //all ok... we delete all items
                        _finishedScanItemCollection = new Queue<ScanItemDto>();
                    }
                    if (_recreateContext)
                    {
                        _dbContext.Dispose();
                        _dbContext = new DubFinderContext(_dbContextOptions);
                        _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
                    }
                }
            }
        }


        public void WriteChanges()
        {
            if (_dbContext is null)
            {
                throw new InvalidOperationException("Enqueue called without SetupDB!");
            }
            lock (dbContextLock)
            {
                foreach (ScanItemDto siInList in _finishedScanItemCollection)
                {
                    _dbContext.Add(siInList);
                }
                int written = _dbContext.SaveChanges();
                if (written == _finishedScanItemCollection.Count)
                {
                    //all ok... we delete all items
                    _finishedScanItemCollection = new Queue<ScanItemDto>();
                }
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
