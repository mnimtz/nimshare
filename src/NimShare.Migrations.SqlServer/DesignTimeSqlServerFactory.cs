using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NimShare.Core.Data;

namespace NimShare.Migrations.SqlServer;

/// <summary>
/// Design-time factory scoped to the SqlServer migrations assembly.
/// Run: <c>dotnet ef migrations add MyMig --project src/NimShare.Migrations.SqlServer</c>
/// The connection string is a stub — EF never opens it during scaffolding.
/// </summary>
// Internal so EF's design-time scan of NimShare.Api's transitive assemblies
// doesn't pick this factory up when authoring Sqlite migrations. v1.9.1's
// V179 was corrupted precisely by that ambiguity — the SqlServer factory got
// picked, so the model came out with SqlServer column types and diffed
// against the Sqlite snapshot as "AlterColumn on every column". Explicit
// invocation from CLI still works because we pass --project pointing at
// this assembly.
internal class DesignTimeSqlServerFactory : IDesignTimeDbContextFactory<NimShareDbContext>
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
