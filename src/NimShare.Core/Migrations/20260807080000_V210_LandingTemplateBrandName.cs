using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.12.7 — BrandName auf LandingTemplates (nur für Scope=Link gesetzt):
    // Firmen-/Kundenname neben dem Logo auf der Custom-Branding-Landing.
    // Rein additiv, nullable, kein Index/Constraint.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260807080000_V210_LandingTemplateBrandName")]
    public partial class V210_LandingTemplateBrandName : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<string>(
                name: "BrandName",
                table: "LandingTemplates",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "BrandName", table: "LandingTemplates");
        }
    }
}
