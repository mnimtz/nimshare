using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.18 — optionale, verschlüsselte Seriennummer/Lizenzcode pro
    // Share-Link (z.B. bei Software-Downloads). Klick-zum-Anzeigen +
    // optionaler Email-Versand auf der Landing.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260727180000_V196_ShareLinkSerialNumber")]
    public partial class V196_ShareLinkSerialNumber : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<string>(
                name: "SerialNumberEncrypted",
                table: "ShareLinks",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "SerialNumberEncrypted", table: "ShareLinks");
        }
    }
}
