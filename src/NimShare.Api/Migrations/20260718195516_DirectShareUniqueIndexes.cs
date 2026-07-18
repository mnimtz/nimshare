using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class DirectShareUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserFavorites_UserId_FileId",
                table: "UserFavorites");

            migrationBuilder.DropIndex(
                name: "IX_UserFavorites_UserId_FolderId",
                table: "UserFavorites");

            migrationBuilder.DropIndex(
                name: "IX_DirectShares_FileId",
                table: "DirectShares");

            migrationBuilder.DropIndex(
                name: "IX_DirectShares_FolderId",
                table: "DirectShares");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FileId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FileId" },
                unique: true,
                filter: "\"FileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FolderId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FolderId" },
                unique: true,
                filter: "\"FolderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FileId_TargetGroupId",
                table: "DirectShares",
                columns: new[] { "FileId", "TargetGroupId" },
                unique: true,
                filter: "\"FileId\" IS NOT NULL AND \"TargetGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FileId_TargetUserId",
                table: "DirectShares",
                columns: new[] { "FileId", "TargetUserId" },
                unique: true,
                filter: "\"FileId\" IS NOT NULL AND \"TargetUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FolderId_TargetGroupId",
                table: "DirectShares",
                columns: new[] { "FolderId", "TargetGroupId" },
                unique: true,
                filter: "\"FolderId\" IS NOT NULL AND \"TargetGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FolderId_TargetUserId",
                table: "DirectShares",
                columns: new[] { "FolderId", "TargetUserId" },
                unique: true,
                filter: "\"FolderId\" IS NOT NULL AND \"TargetUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserFavorites_UserId_FileId",
                table: "UserFavorites");

            migrationBuilder.DropIndex(
                name: "IX_UserFavorites_UserId_FolderId",
                table: "UserFavorites");

            migrationBuilder.DropIndex(
                name: "IX_DirectShares_FileId_TargetGroupId",
                table: "DirectShares");

            migrationBuilder.DropIndex(
                name: "IX_DirectShares_FileId_TargetUserId",
                table: "DirectShares");

            migrationBuilder.DropIndex(
                name: "IX_DirectShares_FolderId_TargetGroupId",
                table: "DirectShares");

            migrationBuilder.DropIndex(
                name: "IX_DirectShares_FolderId_TargetUserId",
                table: "DirectShares");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FileId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FolderId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FileId",
                table: "DirectShares",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FolderId",
                table: "DirectShares",
                column: "FolderId");
        }
    }
}
