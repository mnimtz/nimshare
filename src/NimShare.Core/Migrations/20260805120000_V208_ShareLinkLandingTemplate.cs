using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.12 — optionale, link-eigene Landing-Vorlage (Custom Branding pro Link,
    // u.a. KI-Auto-Fill aus der Empfänger-Domain). Nullable FK auf LandingTemplates.
    // Rein additiv: bestehende Links bleiben NULL → unveränderter Global/
    // UserPersonal-Fallback. Kein FK-Constraint auf DB-Ebene (SQLite ALTER TABLE),
    // die Beziehung + SetNull-Verhalten managed EF (analog SigningCertificateId).
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260805120000_V208_ShareLinkLandingTemplate")]
    public partial class V208_ShareLinkLandingTemplate : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<Guid>(
                name: "LandingTemplateId",
                table: "ShareLinks",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "LandingTemplateId", table: "ShareLinks");
        }
    }
}
