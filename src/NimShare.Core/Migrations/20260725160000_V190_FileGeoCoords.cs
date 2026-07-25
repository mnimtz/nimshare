using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.10.178 — EXIF-GPS-Koordinaten des Aufnahmeorts pro StorageFile.
    //
    // Zweck: Album-Landing (Gallery-Modus) rendert oberhalb des Fotogrids
    // eine kleine OSM/Leaflet-Karte mit dem Aufnahmeort aller enthaltenen
    // Fotos. Leaflet.fitBounds sorgt aus NW+SO-Ecke automatisch für den
    // passenden Zoom (Marcus's „links, rechts, oben, unten"-Regel).
    //
    // Beide Spalten sind nullable — Fotos ohne GPS (Screenshots, Scanner-
    // Dateien, absichtlich strip-te Bilder) bleiben unbefüllt, die Karte
    // wird dann einfach nicht gerendert. Kein Rollback-Bedarf für vor-
    // handene Daten.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260725160000_V190_FileGeoCoords")]
    public partial class V190_FileGeoCoords : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<double>(
                name: "Latitude",
                table: "Files",
                type: "REAL",
                nullable: true);

            mb.AddColumn<double>(
                name: "Longitude",
                table: "Files",
                type: "REAL",
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "Longitude", table: "Files");
            mb.DropColumn(name: "Latitude", table: "Files");
        }
    }
}
