using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations;

// v1.10.42 — siehe Zwilling im NimShare.Core-Assembly für die Rationale.
// SqlServer verwendet andere Column-Types (nvarchar statt TEXT).
[DbContext(typeof(NimShareDbContext))]
[Migration("20260720120000_V182_ForensicFields")]
public partial class V182_ForensicFields : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        mb.AddColumn<string>(name: "Country", table: "SignatureAudits", type: "nvarchar(2)", maxLength: 2, nullable: true);
        mb.AddColumn<string>(name: "City", table: "SignatureAudits", type: "nvarchar(80)", maxLength: 80, nullable: true);
        mb.AddColumn<string>(name: "DeviceType", table: "SignatureAudits", type: "nvarchar(20)", maxLength: 20, nullable: true);
        mb.AddColumn<string>(name: "Timezone", table: "SignatureAudits", type: "nvarchar(60)", maxLength: 60, nullable: true);

        mb.AddColumn<string>(name: "City", table: "ShareLinkAccesses", type: "nvarchar(80)", maxLength: 80, nullable: true);
        mb.AddColumn<string>(name: "DeviceType", table: "ShareLinkAccesses", type: "nvarchar(20)", maxLength: 20, nullable: true);
        mb.AddColumn<string>(name: "Timezone", table: "ShareLinkAccesses", type: "nvarchar(60)", maxLength: 60, nullable: true);
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropColumn(name: "Country", table: "SignatureAudits");
        mb.DropColumn(name: "City", table: "SignatureAudits");
        mb.DropColumn(name: "DeviceType", table: "SignatureAudits");
        mb.DropColumn(name: "Timezone", table: "SignatureAudits");
        mb.DropColumn(name: "City", table: "ShareLinkAccesses");
        mb.DropColumn(name: "DeviceType", table: "ShareLinkAccesses");
        mb.DropColumn(name: "Timezone", table: "ShareLinkAccesses");
    }
}
