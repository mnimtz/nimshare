using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.22 — Key-Store: pro-User-Verwaltung von Kunden + Lizenzschlüsseln.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260728090000_V197_KeyStore")]
    public partial class V197_KeyStore : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "KeyStoreEntries",
                columns: table => new
                {
                    Id = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    CustomerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CustomerEmail = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    CustomerEmailDomain = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    KeyType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    KeyValueEncrypted = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ValidFrom = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: true),
                    ValidUntil = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyStoreEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeyStoreEntries_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            mb.CreateIndex(name: "IX_KeyStoreEntries_OwnerUserId", table: "KeyStoreEntries", column: "OwnerUserId");
            mb.CreateIndex(name: "IX_KeyStoreEntries_OwnerUserId_CustomerEmail", table: "KeyStoreEntries",
                columns: new[] { "OwnerUserId", "CustomerEmail" });
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable(name: "KeyStoreEntries");
        }
    }
}
