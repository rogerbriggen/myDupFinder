// Roger Briggen license this file to you under the MIT license.
//

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

    public static DbContextOptions<DubFinderContext> GetDbContextOptions(string dataSourceName) => GetDbContextOptions(dataSourceName, null);

    /// <param name="dataSourceName">The database file.</param>
    /// <param name="settings">
    /// Connection settings that are applied on every connection EF Core opens (see <see cref="ConnectionPragmaInterceptor"/>), or null for none (plain contexts, for example of tests; a benchmark can
    /// still set pragmas through <see cref="MeasurementPragmas"/>). The writing jobs pass the settings of <c>ScanJobDBInserts.SetupDB</c>, the find-dups job <see cref="DatabaseConnectionSettings.ForReading"/>.
    /// </param>
    internal static DbContextOptions<DubFinderContext> GetDbContextOptions(string dataSourceName, DatabaseConnectionSettings? settings)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DubFinderContext>();
        // The builder quotes the path: a path with ';', '=' or quotes does not break the connection string (as plain string concatenation does)
        optionsBuilder.UseSqlite(new SqliteConnectionStringBuilder { DataSource = dataSourceName }.ToString());
        if (settings is null && MeasurementPragmas.Current.Count > 0)
        {
            settings = new DatabaseConnectionSettings();
        }
        if (settings is not null)
        {
            optionsBuilder.AddInterceptors(new ConnectionPragmaInterceptor(settings));
        }
        optionsBuilder.ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning));
        return optionsBuilder.Options;
    }

}
