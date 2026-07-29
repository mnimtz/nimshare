using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.54 — Marcus: "Request an account" von der Login-Seite. Visitor
    // stellt eine Anfrage, Admin genehmigt/lehnt ab unter /settings/users.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260729110000_V205_AccountRequests")]
    public partial class V205_AccountRequests : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "AccountRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountRequests_Users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            mb.CreateIndex(
                name: "IX_AccountRequests_Email",
                table: "AccountRequests",
                column: "Email");

            mb.CreateIndex(
                name: "IX_AccountRequests_Status",
                table: "AccountRequests",
                column: "Status");

            mb.CreateIndex(
                name: "IX_AccountRequests_DecidedByUserId",
                table: "AccountRequests",
                column: "DecidedByUserId");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable(name: "AccountRequests");
        }
    }
}
