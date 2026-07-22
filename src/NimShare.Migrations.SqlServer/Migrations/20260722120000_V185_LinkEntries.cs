using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations;

// v1.10.111 — SqlServer-Zwilling der Linksammlung-Migration.
[DbContext(typeof(NimShareDbContext))]
[Migration("20260722120000_V185_LinkEntries")]
public partial class V185_LinkEntries : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        mb.CreateTable(
            name: "LinkEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                Emoji = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                SortOrder = table.Column<int>(type: "int", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LinkEntries", x => x.Id);
            });
        mb.CreateIndex(name: "IX_LinkEntries_SortOrder", table: "LinkEntries", column: "SortOrder");
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropTable(name: "LinkEntries");
    }
}
