// Roger Briggen license this file to you under the MIT license.
//

using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.myDupFinderDB
{
    public class DubFinderScanErrorContext : DbContext
    {

        public DubFinderScanErrorContext(DbContextOptions<DubFinderScanErrorContext> options) : base(options)
        {

        }

        /// <summary>
        /// Correctly scanned items
        /// </summary>
        public DbSet<ScanItemDto>? ErrorScanItems { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ScanItemDto>()
                .HasIndex(b => b.Filename);
            modelBuilder.Entity<ScanItemDto>()
                .HasIndex(b => b.FileSize);
        }
    }
}
