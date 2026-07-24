using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations;

// v1.10.153 — Root-CA der Instanz. Singleton-Tabelle; alle User-Signing-
// Certs werden ab jetzt von dieser CA signiert statt self-signed, damit
// Empfänger die NimShare-Root einmal importieren können und alle Links
// dieser Instanz automatisch validieren.
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
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                SubjectDn = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                NotAfter = table.Column<long>(type: "INTEGER", nullable: false),
                Thumbprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                PfxDataEncrypted = table.Column<byte[]>(type: "BLOB", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
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
