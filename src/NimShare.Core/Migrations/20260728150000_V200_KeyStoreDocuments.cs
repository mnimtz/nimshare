using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.37 — Key-Store-Dokumente: PDFs oder feste Links, gebunden an Key-Typen.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260728150000_V200_KeyStoreDocuments")]
    public partial class V200_KeyStoreDocuments : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "KeyStoreDocuments",
                columns: table => new
                {
                    Id = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BlobPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    KeyTypesCsv = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyStoreDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeyStoreDocuments_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            mb.CreateIndex(name: "IX_KeyStoreDocuments_OwnerUserId", table: "KeyStoreDocuments", column: "OwnerUserId");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable(name: "KeyStoreDocuments");
        }
    }
}
