using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.52 — Marcus: dezente, immer sichtbare Emoji-Reaktionsleiste auf
    // der Link-Landing. Anonym (keine Besucher-Identität gespeichert),
    // Dedupe läuft rein über die Server-Session, nicht über diese Tabelle.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260729100000_V204_ShareLinkReactions")]
    public partial class V204_ShareLinkReactions : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "ShareLinkReactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Emoji = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareLinkReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareLinkReactions_ShareLinks_ShareLinkId",
                        column: x => x.ShareLinkId,
                        principalTable: "ShareLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            mb.CreateIndex(
                name: "IX_ShareLinkReactions_ShareLinkId",
                table: "ShareLinkReactions",
                column: "ShareLinkId");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable(name: "ShareLinkReactions");
        }
    }
}
