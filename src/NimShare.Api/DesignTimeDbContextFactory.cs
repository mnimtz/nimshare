using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NimShare.Core.Data;

namespace NimShare.Api;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations add</c>. NimShare is
/// Sqlite-only (v1.11.48 — Azure SQL support removed); migrations land in
/// <c>src/NimShare.Core/Migrations</c>.
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
