using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations;

// v1.10.42: forensische Zusatzdaten für Signatur-Beweiskraft und Link-
// Reports. Alle Spalten nullable → keine Backfill-Daten nötig,
// bestehende Zeilen bleiben unverändert.
//
// Der Designer/BuildTargetModel wird bei diesem manuell erstellten
// Migration-Skript weggelassen — EF Core braucht ihn nur für "Add-
// Migration"-Design-Time-Diff, nicht fürs Runtime-Migrate(). Marcus's
// nächste "Add-Migration" wird einen kleinen Diff-Warning werfen, was
// harmlos ist (wir aktualisieren dann Snapshot + Designer manuell).
[DbContext(typeof(NimShareDbContext))]
[Migration("20260720120000_V182_ForensicFields")]
public partial class V182_ForensicFields : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        // SignatureAudits — 4 neue Spalten
        mb.AddColumn<string>(name: "Country", table: "SignatureAudits", type: "TEXT", maxLength: 2, nullable: true);
        mb.AddColumn<string>(name: "City", table: "SignatureAudits", type: "TEXT", maxLength: 80, nullable: true);
        mb.AddColumn<string>(name: "DeviceType", table: "SignatureAudits", type: "TEXT", maxLength: 20, nullable: true);
        mb.AddColumn<string>(name: "Timezone", table: "SignatureAudits", type: "TEXT", maxLength: 60, nullable: true);

        // ShareLinkAccesses — 3 neue (CountryCode existiert schon)
        mb.AddColumn<string>(name: "City", table: "ShareLinkAccesses", type: "TEXT", maxLength: 80, nullable: true);
        mb.AddColumn<string>(name: "DeviceType", table: "ShareLinkAccesses", type: "TEXT", maxLength: 20, nullable: true);
        mb.AddColumn<string>(name: "Timezone", table: "ShareLinkAccesses", type: "TEXT", maxLength: 60, nullable: true);
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropColumn(name: "Country", table: "SignatureAudits");
        mb.DropColumn(name: "City", table: "SignatureAudits");
        mb.DropColumn(name: "DeviceType", table: "SignatureAudits");
        mb.DropColumn(name: "Timezone", table: "SignatureAudits");
        mb.DropColumn(name: "City", table: "ShareLinkAccesses");
        mb.DropColumn(name: "DeviceType", table: "ShareLinkAccesses");
        mb.DropColumn(name: "Timezone", table: "ShareLinkAccesses");
    }
}
