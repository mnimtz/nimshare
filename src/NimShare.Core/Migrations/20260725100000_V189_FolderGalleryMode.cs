using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.10.166 — Gallery-Ordner-Typ + Upload-erlauben-Flag auf ShareLinks.
    //
    // Folders.Kind (0=Regular, 1=Gallery) tauscht auf der Landing die klassische
    // Datei-Liste gegen eine Grid+Lightbox-Ansicht. ShareLinks.AllowUploads
    // ist nur für Folder-Links auf Gallery-Ordnern wirksam und aktiviert das
    // Upload-Widget für Empfänger (Event-/Hochzeits-Album-Use-Case).
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260725100000_V189_FolderGalleryMode")]
    public partial class V189_FolderGalleryMode : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<int>(
                name: "Kind",
                table: "Folders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            mb.AddColumn<bool>(
                name: "AllowUploads",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.AddColumn<bool>(
                name: "DisplayAsGallery",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "DisplayAsGallery", table: "ShareLinks");
            mb.DropColumn(name: "AllowUploads", table: "ShareLinks");
            mb.DropColumn(name: "Kind", table: "Folders");
        }
    }
}
