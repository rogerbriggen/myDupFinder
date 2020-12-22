// Roger Briggen license this file to you under the MIT license.
//

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;


namespace RogerBriggen.myDupFinderDB
{
    public class DubFinderScanErrorContextFactory : IDesignTimeDbContextFactory<DubFinderScanErrorContext>
    {
        public DubFinderScanErrorContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<DubFinderScanErrorContext>();
            optionsBuilder.UseSqlite("Data Source=blog.db");

            return new DubFinderScanErrorContext(optionsBuilder.Options);
        }

        public static DubFinderScanErrorContext CreateDubFinderContext(string dataSourceName)
        {
            return new DubFinderScanErrorContext(GetDbContextOptions(dataSourceName));
        }

        public static DbContextOptions<DubFinderScanErrorContext> GetDbContextOptions(string dataSourceName)
        {
            var optionsBuilder = new DbContextOptionsBuilder<DubFinderScanErrorContext>();
            optionsBuilder.UseSqlite($"Data Source={dataSourceName}");
            return optionsBuilder.Options;
        }

    }
}
