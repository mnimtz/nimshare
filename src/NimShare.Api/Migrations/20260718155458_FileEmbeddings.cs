using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class FileEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileEmbeddings",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Vector = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileEmbeddings", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_FileEmbeddings_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileEmbeddings");
        }
    }
}
