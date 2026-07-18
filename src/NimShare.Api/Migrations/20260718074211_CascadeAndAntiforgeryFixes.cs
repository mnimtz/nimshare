using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class CascadeAndAntiforgeryFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShareLinks_Files_FileId",
                table: "ShareLinks");

            migrationBuilder.AddForeignKey(
                name: "FK_ShareLinks_Files_FileId",
                table: "ShareLinks",
                column: "FileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShareLinks_Files_FileId",
                table: "ShareLinks");

            migrationBuilder.AddForeignKey(
                name: "FK_ShareLinks_Files_FileId",
                table: "ShareLinks",
                column: "FileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
