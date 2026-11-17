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

    /// <summary>
    /// The identity of the volume of each scanned base path
    /// </summary>
    public DbSet<ScanVolumeDto>? ScanVolumes { get; set; }


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
        modelBuilder.Entity<ScanVolumeDto>()
            .HasIndex(b => new { b.OriginComputer, b.ScanName, b.BasePath })
            .IsUnique();
    }
}
