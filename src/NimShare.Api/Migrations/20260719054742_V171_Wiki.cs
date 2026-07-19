using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class V171_Wiki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WikiPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OwnerGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentPageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ContentMarkdown = table.Column<string>(type: "TEXT", maxLength: 100000, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastEditedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPages_Groups_OwnerGroupId",
                        column: x => x.OwnerGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WikiPages_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WikiPages_Users_LastEditedByUserId",
                        column: x => x.LastEditedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WikiPages_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WikiPages_WikiPages_ParentPageId",
                        column: x => x.ParentPageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_CreatedByUserId",
                table: "WikiPages",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_LastEditedByUserId",
                table: "WikiPages",
                column: "LastEditedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_OwnerGroupId",
                table: "WikiPages",
                column: "OwnerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_OwnerUserId",
                table: "WikiPages",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_ParentPageId",
                table: "WikiPages",
                column: "ParentPageId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Scope_OwnerGroupId",
                table: "WikiPages",
                columns: new[] { "Scope", "OwnerGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Scope_OwnerUserId",
                table: "WikiPages",
                columns: new[] { "Scope", "OwnerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WikiPages");
        }
    }
}
