using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class V178_SigningCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigningCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    SubjectCommonName = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    NotAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    Thumbprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsSelfIssued = table.Column<bool>(type: "INTEGER", nullable: false),
                    PfxDataEncrypted = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SigningCertificates_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigningCertificates_OwnerUserId_NotAfter",
                table: "SigningCertificates",
                columns: new[] { "OwnerUserId", "NotAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_SigningCertificates_OwnerUserId_Thumbprint",
                table: "SigningCertificates",
                columns: new[] { "OwnerUserId", "Thumbprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SigningCertificates");
        }
    }
}
