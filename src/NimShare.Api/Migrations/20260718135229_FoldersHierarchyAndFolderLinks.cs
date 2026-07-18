using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class FoldersHierarchyAndFolderLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TargetFolderId",
                table: "UploadRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FileId",
                table: "ShareLinks",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                table: "ShareLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                table: "Files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OwnerGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Folders_Folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Folders_Groups_OwnerGroupId",
                        column: x => x.OwnerGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Folders_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UploadRequests_TargetFolderId",
                table: "UploadRequests",
                column: "TargetFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_FolderId",
                table: "ShareLinks",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_FolderId",
                table: "Files",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_OwnerGroupId",
                table: "Folders",
                column: "OwnerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_OwnerUserId",
                table: "Folders",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentFolderId_Name",
                table: "Folders",
                columns: new[] { "ParentFolderId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_Scope_OwnerUserId_OwnerGroupId_ParentFolderId",
                table: "Folders",
                columns: new[] { "Scope", "OwnerUserId", "OwnerGroupId", "ParentFolderId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Files_Folders_FolderId",
                table: "Files",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ShareLinks_Folders_FolderId",
                table: "ShareLinks",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UploadRequests_Folders_TargetFolderId",
                table: "UploadRequests",
                column: "TargetFolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Files_Folders_FolderId",
                table: "Files");

            migrationBuilder.DropForeignKey(
                name: "FK_ShareLinks_Folders_FolderId",
                table: "ShareLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_UploadRequests_Folders_TargetFolderId",
                table: "UploadRequests");

            migrationBuilder.DropTable(
                name: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_UploadRequests_TargetFolderId",
                table: "UploadRequests");

            migrationBuilder.DropIndex(
                name: "IX_ShareLinks_FolderId",
                table: "ShareLinks");

            migrationBuilder.DropIndex(
                name: "IX_Files_FolderId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "TargetFolderId",
                table: "UploadRequests");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "ShareLinks");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "Files");

            migrationBuilder.AlterColumn<Guid>(
                name: "FileId",
                table: "ShareLinks",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
