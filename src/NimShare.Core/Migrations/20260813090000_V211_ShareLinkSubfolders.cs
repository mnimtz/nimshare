using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.12.12 — Ordner-Freigaben können Unterordner rekursiv einbeziehen:
    // IncludeSubfolders (Opt-in, default false = Altverhalten) + optionale
    // maximale Tiefe SubfolderDepth (null = unbegrenzt). Rein additiv.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260813090000_V211_ShareLinkSubfolders")]
    public partial class V211_ShareLinkSubfolders : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "IncludeSubfolders",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
            mb.AddColumn<int>(
                name: "SubfolderDepth",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "IncludeSubfolders", table: "ShareLinks");
            mb.DropColumn(name: "SubfolderDepth", table: "ShareLinks");
        }
    }
}
