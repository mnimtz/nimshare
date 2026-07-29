using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.50 — Marcus: Ablauf-Default 8 Wochen für neue Links, damit sich
    // nicht endlos vergessene Links ansammeln. IsPermanent ist der explizite
    // Opt-out (muss aktiv angeklickt werden), analog zum bisherigen Verhalten
    // "ExpiresAt IS NULL = läuft nie ab" — bestehende Links mit NULL-ExpiresAt
    // bekommen daher IsPermanent=1, damit sich ihr Verhalten nicht ändert.
    //
    // Nur AddColumn in Up() — siehe V202-Hotfix-Kommentar: DropColumn in einer
    // SQLite-Migration reißt die komplette Transaktion (inkl. AddColumn) mit
    // sich, wenn es fehlschlägt. Reine AddColumn-Migrationen sind das einzige
    // in diesem Projekt je zuverlässig funktionierende Muster.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260729090000_V203_LinkIsPermanent")]
    public partial class V203_LinkIsPermanent : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "IsPermanent",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.AddColumn<bool>(
                name: "IsPermanent",
                table: "UploadRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.Sql("UPDATE ShareLinks SET IsPermanent = 1 WHERE ExpiresAt IS NULL");
            mb.Sql("UPDATE UploadRequests SET IsPermanent = 1 WHERE ExpiresAt IS NULL");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "IsPermanent", table: "ShareLinks");
            mb.DropColumn(name: "IsPermanent", table: "UploadRequests");
        }
    }
}
