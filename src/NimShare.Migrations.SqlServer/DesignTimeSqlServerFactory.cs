using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NimShare.Core.Data;

namespace NimShare.Migrations.SqlServer;

/// <summary>
/// Design-time factory scoped to the SqlServer migrations assembly.
/// Run: <c>dotnet ef migrations add MyMig --project src/NimShare.Migrations.SqlServer</c>
/// The connection string is a stub — EF never opens it during scaffolding.
/// </summary>
public class DesignTimeSqlServerFactory : IDesignTimeDbContextFactory<NimShareDbContext>
{
    public NimShareDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<NimShareDbContext>();
        opts.UseSqlServer(
            "Server=(localdb)\\MSSQLLocalDB;Database=nimshare_design;Trusted_Connection=True;",
            b => b.MigrationsAssembly(typeof(DesignTimeSqlServerFactory).Assembly.GetName().Name));
        return new NimShareDbContext(opts.Options);
    }
}
