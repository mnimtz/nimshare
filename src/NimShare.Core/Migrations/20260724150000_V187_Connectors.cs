using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations;

// v1.10.163 — Konnektoren-Verbindungen (OneDrive Business zuerst). Ein User
// kann mehrere externe Cloud-Speicher verbinden und aus dort liegenden
// Ordnern/Dateien Import-Jobs anstoßen (cloud-to-cloud Streaming direkt
// in seinen Personal-Ablagebereich).
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                RefreshTokenEncrypted = table.Column<byte[]>(type: "BLOB", nullable: false),
                ExternalAccountId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                PreserveFolderStructure = table.Column<bool>(type: "INTEGER", nullable: false),
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
