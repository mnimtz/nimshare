using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations;

// v1.10.108 — nachgereichter SqlServer-Zwilling der V183 (UGC-Moderation,
// App-Store Guideline 1.2). Fehlte seit v1.10.82: SqlServer-Deploys hatten
// dadurch weder BlockedUsers noch ContentReports ("Invalid object name"
// beim ersten Block/Report). Typen analog zum Sqlite-Original: Guid →
// uniqueidentifier, long-Timestamps → bigint, Strings → nvarchar(n).
[DbContext(typeof(NimShareDbContext))]
[Migration("20260721091500_V183_UgcModeration")]
public partial class V183_UgcModeration : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        mb.CreateTable(
            name: "BlockedUsers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BlockedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BlockedUsers", x => x.Id);
                table.ForeignKey(
                    name: "FK_BlockedUsers_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                // Zweiter FK auf Users bewusst NoAction — doppelter Cascade-
                // Pfad auf dieselbe Tabelle ist in SqlServer verboten
                // (Fehler 1785 "may cause cycles or multiple cascade paths").
                table.ForeignKey(
                    name: "FK_BlockedUsers_Users_BlockedUserId",
                    column: x => x.BlockedUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
            });
        mb.CreateIndex(
            name: "IX_BlockedUsers_UserId_BlockedUserId",
            table: "BlockedUsers",
            columns: new[] { "UserId", "BlockedUserId" },
            unique: true);
        mb.CreateIndex(
            name: "IX_BlockedUsers_BlockedUserId",
            table: "BlockedUsers",
            column: "BlockedUserId");

        mb.CreateTable(
            name: "ContentReports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReporterUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SubjectKind = table.Column<int>(type: "int", nullable: false),
                SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SubjectLabel = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                SubjectOwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Reason = table.Column<int>(type: "int", nullable: false),
                Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                ResolvedAt = table.Column<long>(type: "bigint", nullable: true),
                ResolvedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Resolution = table.Column<int>(type: "int", nullable: true),
                ResolutionNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ContentReports", x => x.Id);
                table.ForeignKey(
                    name: "FK_ContentReports_Users_ReporterUserId",
                    column: x => x.ReporterUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ContentReports_Users_ResolvedByUserId",
                    column: x => x.ResolvedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
            });
        mb.CreateIndex(
            name: "IX_ContentReports_Status_CreatedAt",
            table: "ContentReports",
            columns: new[] { "Status", "CreatedAt" });
        mb.CreateIndex(
            name: "IX_ContentReports_SubjectKind_SubjectId",
            table: "ContentReports",
            columns: new[] { "SubjectKind", "SubjectId" });
        mb.CreateIndex(
            name: "IX_ContentReports_ReporterUserId",
            table: "ContentReports",
            column: "ReporterUserId");
        mb.CreateIndex(
            name: "IX_ContentReports_ResolvedByUserId",
            table: "ContentReports",
            column: "ResolvedByUserId");
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropTable(name: "ContentReports");
        mb.DropTable(name: "BlockedUsers");
    }
}
