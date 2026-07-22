using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations;

// v1.10.111 — Linksammlung (löst Wiki als Feature ab). Geteilte, flache
// Liste aus {Title, Url, Description, Emoji}. Handgeschrieben im V183/V184-
// Stil MIT [DbContext]/[Migration]-Attributen (sonst überspringt EF sie).
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                Emoji = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
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
