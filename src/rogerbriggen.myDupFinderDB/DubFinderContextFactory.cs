// Roger Briggen license this file to you under the MIT license.
//

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RogerBriggen.MyDupFinderDB;

public class DubFinderContextFactory : IDesignTimeDbContextFactory<DubFinderContext>
{
    public DubFinderContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DubFinderContext>();
        optionsBuilder.UseSqlite("Data Source=blog.db");

        return new DubFinderContext(optionsBuilder.Options);
    }

    public static DubFinderContext CreateDubFinderContext(string dataSourceName) => new DubFinderContext(GetDbContextOptions(dataSourceName));

    public static DbContextOptions<DubFinderContext> GetDbContextOptions(string dataSourceName)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DubFinderContext>();
        optionsBuilder.UseSqlite($"Data Source={dataSourceName}");
        return optionsBuilder.Options;
    }

}
