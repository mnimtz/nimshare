using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.10.104 (Stage 2 „Windows-ACL"): Public-Ordner können als privat
    // markiert werden. Private Ordner sind nur für Ersteller, Admin und
    // explizit berechtigte User/Gruppen (via DirectShare-Grants) sichtbar.
    // Kein Grant → wie ein Windows-Ordner ohne Berechtigung: unsichtbar.
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
