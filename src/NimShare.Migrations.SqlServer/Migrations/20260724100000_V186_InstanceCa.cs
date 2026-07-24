using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations;

// v1.10.153 — SqlServer-Zwilling der Core V186_InstanceCa.
[DbContext(typeof(NimShareDbContext))]
[Migration("20260724100000_V186_InstanceCa")]
public partial class V186_InstanceCa : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        mb.CreateTable(
            name: "InstanceCas",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                SubjectDn = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                NotBefore = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                NotAfter = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Thumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                PfxDataEncrypted = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InstanceCas", x => x.Id);
            });
        mb.CreateIndex(
            name: "IX_InstanceCas_IsActive_NotAfter",
            table: "InstanceCas",
            columns: new[] { "IsActive", "NotAfter" });
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropTable(name: "InstanceCas");
    }
}
