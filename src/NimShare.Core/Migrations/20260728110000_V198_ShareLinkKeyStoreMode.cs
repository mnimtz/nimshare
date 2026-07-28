using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.22 — Share-Link-Integration für den Key-Store: KeyStoreMode
    // (Landing fragt Besucher-Email ab statt eines fest hinterlegten Werts)
    // + optionaler Doku-Link.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260728110000_V198_ShareLinkKeyStoreMode")]
    public partial class V198_ShareLinkKeyStoreMode : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "KeyStoreMode",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.AddColumn<string>(
                name: "DocumentationUrl",
                table: "ShareLinks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "DocumentationUrl", table: "ShareLinks");
            mb.DropColumn(name: "KeyStoreMode", table: "ShareLinks");
        }
    }
}
