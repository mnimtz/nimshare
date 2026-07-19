using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class V170_Locking_Office : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LockKind",
                table: "Files",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedByUserId",
                table: "Files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LockedUntil",
                table: "Files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OfficeSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DocumentServerUrl = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    JwtSecretEncrypted = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Files_LockedByUserId",
                table: "Files",
                column: "LockedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Files_Users_LockedByUserId",
                table: "Files",
                column: "LockedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Files_Users_LockedByUserId",
                table: "Files");

            migrationBuilder.DropTable(
                name: "OfficeSettings");

            migrationBuilder.DropIndex(
                name: "IX_Files_LockedByUserId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "LockKind",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "LockedByUserId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "Files");
        }
    }
}
