using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NimShare.Core.Data;

namespace NimShare.Api;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add</c> for the
/// **Sqlite** provider. Migrations land in <c>src/NimShare.Api/Migrations</c>.
///
/// SqlServer migrations live in a separate class library
/// (<c>src/NimShare.Migrations.SqlServer</c>) — use its own DesignTime
/// factory: <c>dotnet ef migrations add MyMig --project src/NimShare.Migrations.SqlServer --startup-project src/NimShare.Api</c>.
/// Runtime EF picks the correct <see cref="MigrationsAssembly"/> in Program.cs
/// based on the configured provider.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NimShareDbContext>
{
    public NimShareDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<NimShareDbContext>();
        opts.UseSqlite("Data Source=nimshare_design.db",
            b => b.MigrationsAssembly("NimShare.Api"));
        return new NimShareDbContext(opts.Options);
    }
}
