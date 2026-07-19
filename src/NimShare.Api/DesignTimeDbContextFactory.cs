using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NimShare.Core.Data;

namespace NimShare.Api;

/// <summary>
/// Design-time factory used by `dotnet ef migrations add`. Two providers are
/// supported; the caller picks between them with the standard EF `--provider`
/// arg or the <c>NIMSHARE_MIGRATION_PROVIDER</c> env var:
/// <code>
///   dotnet ef migrations add MyMigration --output-dir Migrations/Sqlite
///   dotnet ef migrations add MyMigration --output-dir Migrations/SqlServer -- --provider SqlServer
/// </code>
/// Runtime EF picks migrations via <c>MigrationsAssembly</c> on the
/// UseSqlite/UseSqlServer options in Program.cs.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NimShareDbContext>
{
    public NimShareDbContext CreateDbContext(string[] args)
    {
        // Extract --provider Sqlite|SqlServer from either the raw args or the
        // env var. Default to Sqlite so plain `dotnet ef migrations add …`
        // keeps working the way it always has.
        var provider = Environment.GetEnvironmentVariable("NIMSHARE_MIGRATION_PROVIDER") ?? "Sqlite";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--provider", StringComparison.OrdinalIgnoreCase))
            {
                provider = args[i + 1];
                break;
            }
        }

        var opts = new DbContextOptionsBuilder<NimShareDbContext>();
        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            // Any parseable connection string works — EF never actually opens
            // it during migration authoring.
            opts.UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=nimshare_design;Trusted_Connection=True;",
                b => b.MigrationsAssembly("NimShare.Api").MigrationsHistoryTable("__EFMigrationsHistory_SqlServer"));
        }
        else
        {
            opts.UseSqlite("Data Source=nimshare_design.db",
                b => b.MigrationsAssembly("NimShare.Api"));
        }
        return new NimShareDbContext(opts.Options);
    }
}
