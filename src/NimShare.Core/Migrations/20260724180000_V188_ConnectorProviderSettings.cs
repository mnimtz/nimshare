using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations;

// v1.10.164 — Admin-Config pro Konnektor-Provider (bisher in appsettings.json).
// Analog AiGatewaySettings: Singleton per Provider-Type, ClientSecret
// DataProtection-verschlüsselt, Admin richtet alles im NimShare-UI ein.
[DbContext(typeof(NimShareDbContext))]
[Migration("20260724180000_V188_ConnectorProviderSettings")]
public partial class V188_ConnectorProviderSettings : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        mb.CreateTable(
            name: "ConnectorProviderSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Provider = table.Column<int>(type: "INTEGER", nullable: false),
                ClientId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                ClientSecretEncrypted = table.Column<byte[]>(type: "BLOB", nullable: true),
                Tenant = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConnectorProviderSettings", x => x.Id);
            });
        mb.CreateIndex(
            name: "IX_ConnectorProviderSettings_Provider",
            table: "ConnectorProviderSettings",
            column: "Provider",
            unique: true);
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropTable(name: "ConnectorProviderSettings");
    }
}
