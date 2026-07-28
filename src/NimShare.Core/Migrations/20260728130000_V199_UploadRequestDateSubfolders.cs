using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.28 — Option "Datums-Unterordner" für Upload-Anfragen: jeder Upload
    // landet zusätzlich in einem yyyy-MM-dd-Unterordner (Default an).
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260728130000_V199_UploadRequestDateSubfolders")]
    public partial class V199_UploadRequestDateSubfolders : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "UseDateSubfolders",
                table: "UploadRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "UseDateSubfolders", table: "UploadRequests");
        }
    }
}
