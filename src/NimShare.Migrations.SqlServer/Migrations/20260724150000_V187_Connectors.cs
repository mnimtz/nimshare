using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations;

// v1.10.163 — SqlServer-Zwilling der Core V187_Connectors.
[DbContext(typeof(NimShareDbContext))]
[Migration("20260724150000_V187_Connectors")]
public partial class V187_Connectors : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        mb.CreateTable(
            name: "Connectors",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Type = table.Column<int>(type: "int", nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                RefreshTokenEncrypted = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                ExternalAccountId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                PreserveFolderStructure = table.Column<bool>(type: "bit", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Connectors", x => x.Id);
                table.ForeignKey(
                    name: "FK_Connectors_Users_OwnerUserId",
                    column: x => x.OwnerUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
        mb.CreateIndex(
            name: "IX_Connectors_OwnerUserId_Type",
            table: "Connectors",
            columns: new[] { "OwnerUserId", "Type" });
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropTable(name: "Connectors");
    }
}
