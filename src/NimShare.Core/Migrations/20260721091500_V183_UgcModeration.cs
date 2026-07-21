using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations;

// v1.10.82: App-Store-Blocker (Apple Guideline 1.2 UGC) — User müssen
// andere User blockieren und Content melden können.
//
// Handgeschriebene Migration im V182-Stil ohne Designer/Snapshot. EF
// Runtime-Migrate() ist zufrieden; Design-Time "Add-Migration" wird beim
// nächsten Mal einen Diff-Warning werfen, das ist harmlos.
[DbContext(typeof(NimShareDbContext))]
[Migration("20260721091500_V183_UgcModeration")]
public partial class V183_UgcModeration : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        // BlockedUsers
        mb.CreateTable(
            name: "BlockedUsers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                BlockedUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
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
                table.ForeignKey(
                    name: "FK_BlockedUsers_Users_BlockedUserId",
                    column: x => x.BlockedUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
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

        // ContentReports
        mb.CreateTable(
            name: "ContentReports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ReporterUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                SubjectKind = table.Column<int>(type: "INTEGER", nullable: false),
                SubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                SubjectLabel = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                SubjectOwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Reason = table.Column<int>(type: "INTEGER", nullable: false),
                Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                ResolvedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Resolution = table.Column<int>(type: "INTEGER", nullable: true),
                ResolutionNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
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
                    onDelete: ReferentialAction.SetNull);
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
