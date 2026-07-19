using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class V151_SubFolderPerms_LinkAllowlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedEmails",
                table: "ShareLinks",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireEmailVerify",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FolderAccessOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MaxPermission = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderAccessOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Groups_TargetGroupId",
                        column: x => x.TargetGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_CreatedByUserId",
                table: "FolderAccessOverrides",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_FolderId_TargetGroupId",
                table: "FolderAccessOverrides",
                columns: new[] { "FolderId", "TargetGroupId" },
                filter: "\"TargetGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_FolderId_TargetUserId",
                table: "FolderAccessOverrides",
                columns: new[] { "FolderId", "TargetUserId" },
                filter: "\"TargetUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_TargetGroupId",
                table: "FolderAccessOverrides",
                column: "TargetGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_TargetUserId",
                table: "FolderAccessOverrides",
                column: "TargetUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderAccessOverrides");

            migrationBuilder.DropColumn(
                name: "AllowedEmails",
                table: "ShareLinks");

            migrationBuilder.DropColumn(
                name: "RequireEmailVerify",
                table: "ShareLinks");
        }
    }
}
