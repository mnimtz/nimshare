using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.10.191 — Thumbnail-Fertig-Flag pro StorageFile.
    //
    // Teil des Gallery-Perf-Redesigns: statt bei jedem Landing-Aufruf 2×N
    // Warmup-Tasks zu feuern und pro Kachel einen MVC-Request zu machen,
    // weiß die DB jetzt, welche Files fertige Thumbs (400 + 1600) im
    // Blob-Cache haben. Die Landing rendert für die direkte SAS-URLs ins
    // HTML (null Requests pro Kachel), Pending-Files kriegen einen
    // Platzhalter + JS-Status-Polling. Der ThumbnailWorker (Channel-Queue,
    // BackgroundService) setzt das Flag nach dem Build.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260725190000_V191_ThumbsReadyFlag")]
    public partial class V191_ThumbsReadyFlag : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<DateTimeOffset>(
                name: "ThumbsReadyAt",
                table: "Files",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "ThumbsReadyAt", table: "Files");
        }
    }
}
