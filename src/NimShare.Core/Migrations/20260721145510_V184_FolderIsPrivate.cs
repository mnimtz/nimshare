using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.10.104 (Stage 2 „Windows-ACL"): Public-Ordner können als privat
    // markiert werden. Private Ordner sind nur für Ersteller, Admin und
    // explizit berechtigte User/Gruppen (via DirectShare-Grants) sichtbar.
    // Kein Grant → wie ein Windows-Ordner ohne Berechtigung: unsichtbar.
    //
    // v1.10.106: [DbContext] + [Migration]-Attribute nachgezogen. Ohne
    // die findet EF beim Assembly-Scan die Klasse zwar, kann sie aber
    // nicht als Migration einreihen — MigrateAsync ueberspringt sie
    // stumm und der IsPrivate-Column landet nie in produktiven DBs. V183
    // hat dasselbe Muster, ich hatte es beim Copy aus V181 (mit Designer)
    // vergessen.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260721145510_V184_FolderIsPrivate")]
    public partial class V184_FolderIsPrivate : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "IsPrivate",
                table: "Folders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "IsPrivate", table: "Folders");
        }
    }
}
