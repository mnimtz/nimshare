using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.12 — CreatedByUserId auf LandingTemplates (nur für Scope=Link gesetzt):
    // Zugriffsprüfung beim Verknüpfen (IDOR-Schutz) + Aufräumen verwaister
    // Link-Vorlagen. Rein additiv, nullable, kein Index/Constraint.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260805130000_V209_LandingTemplateCreatedBy")]
    public partial class V209_LandingTemplateCreatedBy : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "LandingTemplates",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "CreatedByUserId", table: "LandingTemplates");
        }
    }
}
