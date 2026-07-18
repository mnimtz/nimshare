using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class DirectShareFavoritesActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DirectShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Permission = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectShares_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Groups_TargetGroupId",
                        column: x => x.TargetGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Users_SharedByUserId",
                        column: x => x.SharedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserFavorites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFavorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserFavorites_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserFavorites_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserFavorites_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_ActorUserId_At",
                table: "ActivityEvents",
                columns: new[] { "ActorUserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_At",
                table: "ActivityEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FileId",
                table: "DirectShares",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FolderId",
                table: "DirectShares",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_SharedByUserId",
                table: "DirectShares",
                column: "SharedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetGroupId_FileId",
                table: "DirectShares",
                columns: new[] { "TargetGroupId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetGroupId_FolderId",
                table: "DirectShares",
                columns: new[] { "TargetGroupId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetUserId_FileId",
                table: "DirectShares",
                columns: new[] { "TargetUserId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetUserId_FolderId",
                table: "DirectShares",
                columns: new[] { "TargetUserId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_FileId",
                table: "UserFavorites",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_FolderId",
                table: "UserFavorites",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FileId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FolderId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FolderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEvents");

            migrationBuilder.DropTable(
                name: "DirectShares");

            migrationBuilder.DropTable(
                name: "UserFavorites");
        }
    }
}
