// Roger Briggen license this file to you under the MIT license.
//

using Microsoft.EntityFrameworkCore;
using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderDB;

public class DubFinderContext : DbContext
{

    public DubFinderContext(DbContextOptions<DubFinderContext> options) : base(options)
    {

    }

    /// <summary>
    /// Correctly scanned items
    /// </summary>
    public DbSet<ScanItemDto>? ScanItems { get; set; }

    /// <summary>
    /// Errors during scanning files
    /// </summary>
    public DbSet<ScanErrorItemDto>? ScanErrorItems { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<ScanItemDto>()
            .HasIndex(b => b.FileSha512Hash);
        modelBuilder.Entity<ScanItemDto>()
            .HasIndex(b => b.Filename);
        modelBuilder.Entity<ScanItemDto>()
            .HasIndex(b => b.FileSize);
        modelBuilder.Entity<ScanItemDto>()
            .HasIndex(b => b.FilenameAndPath);
    }
}
