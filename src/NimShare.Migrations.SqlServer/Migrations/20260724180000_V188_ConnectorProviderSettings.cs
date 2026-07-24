using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations;

// v1.10.164 — SqlServer-Zwilling der Core V188_ConnectorProviderSettings.
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
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Provider = table.Column<int>(type: "int", nullable: false),
                ClientId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                ClientSecretEncrypted = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                Tenant = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
