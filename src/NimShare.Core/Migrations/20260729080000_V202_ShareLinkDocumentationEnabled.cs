using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.44 — Marcus: die "Dokumentation"-Checkbox trug bisher selbst
    // einen festen, hartkodierten URL-Wert (docshield.tungstenautomation.com)
    // statt nur als Ein/Aus-Schalter für die flexiblen Key-Store-Dokumente
    // zu dienen. DocumentationUrl → DocumentationEnabled (bool); bestehende
    // Links mit gesetztem DocumentationUrl behalten die Checkbox aktiviert.
    //
    // v1.11.46 — HOTFIX: Ursprünglich enthielt Up() zusätzlich
    // mb.DropColumn(DocumentationUrl). Das hat production gebrochen ("no
    // such column: s.DocumentationEnabled") — EF Core wickelt eine ganze
    // SQLite-Migration in EINE Transaktion; der DropColumn-Schritt ist auf
    // SQLite fehlgeschlagen (SQLite unterstützt ALTER TABLE DROP COLUMN nur
    // sehr eingeschränkt / je nach gebundener Native-Lib-Version), und die
    // GESAMTE Transaktion (inkl. AddColumn + UPDATE + History-Eintrag)
    // wurde zurückgerollt — die Spalte wurde nie angelegt. Kein bisheriges
    // NimShare-Migration hat je erfolgreich eine DropColumn-Operation in
    // Up() ausgeführt (nur in Down(), das nie automatisch läuft) — reine
    // AddColumn-Migrationen sind die einzige in diesem Projekt je
    // funktionierende Muster auf SQLite. DocumentationUrl bleibt daher als
    // harmlose, ungenutzte Alt-Spalte stehen (kein C#-Code liest/schreibt
    // sie mehr seit dem ShareLink-Entity-Umbau) statt sie zu droppen.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260729080000_V202_ShareLinkDocumentationEnabled")]
    public partial class V202_ShareLinkDocumentationEnabled : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "DocumentationEnabled",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.Sql("UPDATE ShareLinks SET DocumentationEnabled = 1 WHERE DocumentationUrl IS NOT NULL AND DocumentationUrl <> ''");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "DocumentationEnabled", table: "ShareLinks");
        }
    }
}
