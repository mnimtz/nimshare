using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.56 — Marcus: "Lizenzen"-Vorrat im Key-Store. Schlüssel können
    // ohne Kunde eingetragen (Status=Available) und später einem neuen
    // Kunden zugewiesen werden (Status=Assigned). Alle bestehenden Zeilen
    // hatten immer schon einen Kunden — defaultValue 1 (Assigned) hält das
    // Verhalten für sie unverändert.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260729120000_V206_KeyStoreLicensePool")]
    public partial class V206_KeyStoreLicensePool : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<int>(
                name: "Status",
                table: "KeyStoreEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            mb.AddColumn<DateTimeOffset>(
                name: "AssignedAt",
                table: "KeyStoreEntries",
                type: "TEXT",
                nullable: true);

            mb.AddColumn<bool>(
                name: "IsGlobal",
                table: "KeyStoreEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.CreateIndex(
                name: "IX_KeyStoreEntries_OwnerUserId_Status",
                table: "KeyStoreEntries",
                columns: new[] { "OwnerUserId", "Status" });
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropIndex(name: "IX_KeyStoreEntries_OwnerUserId_Status", table: "KeyStoreEntries");
            mb.DropColumn(name: "Status", table: "KeyStoreEntries");
            mb.DropColumn(name: "AssignedAt", table: "KeyStoreEntries");
            mb.DropColumn(name: "IsGlobal", table: "KeyStoreEntries");
        }
    }
}
