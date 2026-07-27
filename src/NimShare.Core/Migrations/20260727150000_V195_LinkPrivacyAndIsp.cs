using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.14 — Link-Report: ISP/Org-String pro Zugriffs-Event (zur
    // Erkennung automatisierter Link-Vorschau-Abrufe, z.B. Microsoft Teams)
    // + LinkPrivacySettings-Singleton als Admin-UI-Toggle für StoreFullIp
    // (löst den bisherigen reinen Config-Wert ab, der als Fallback bleibt).
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260727150000_V195_LinkPrivacyAndIsp")]
    public partial class V195_LinkPrivacyAndIsp : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<string>(
                name: "Isp",
                table: "ShareLinkAccesses",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            mb.CreateTable(
                name: "LinkPrivacySettings",
                columns: table => new
                {
                    Id = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    StoreFullIp = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<System.Guid>(type: "TEXT", nullable: true),
                },
                constraints: table => table.PrimaryKey("PK_LinkPrivacySettings", x => x.Id));
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable(name: "LinkPrivacySettings");
            mb.DropColumn(name: "Isp", table: "ShareLinkAccesses");
        }
    }
}
