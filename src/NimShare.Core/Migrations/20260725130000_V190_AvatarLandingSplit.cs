using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.10.178 — Avatar-Anzeige auf öffentlichen vs. persönlichen Landings
    // trennen. Bestehender ShowAvatarOnLandings-Wert wird in beide neuen
    // Spalten kopiert, damit die User-Erfahrung nicht abrupt kippt.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260725130000_V190_AvatarLandingSplit")]
    public partial class V190_AvatarLandingSplit : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "ShowAvatarOnPublicShares",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.AddColumn<bool>(
                name: "ShowAvatarOnPersonalShares",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Daten-Backfill: wer den alten Toggle an hatte, sieht den Avatar
            // in beiden Kanälen weiter — keine Überraschungen.
            mb.Sql("UPDATE \"Users\" SET \"ShowAvatarOnPublicShares\" = \"ShowAvatarOnLandings\", \"ShowAvatarOnPersonalShares\" = \"ShowAvatarOnLandings\"");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "ShowAvatarOnPersonalShares", table: "Users");
            mb.DropColumn(name: "ShowAvatarOnPublicShares", table: "Users");
        }
    }
}
