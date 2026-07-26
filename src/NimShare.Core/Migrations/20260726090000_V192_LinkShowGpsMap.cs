using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.10.196 — GPS-Karten-Toggle pro ShareLink (Album-Landing).
    // Default 1 = Karte an (bisheriges Verhalten bleibt für Alt-Links
    // erhalten); der Link-Ersteller kann sie beim Freigeben abschalten.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260726090000_V192_LinkShowGpsMap")]
    public partial class V192_LinkShowGpsMap : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "ShowGpsMap",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "ShowGpsMap", table: "ShareLinks");
        }
    }
}
