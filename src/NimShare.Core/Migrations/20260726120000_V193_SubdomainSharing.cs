using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.0 — Subdomain-Sharing (https://wichtig.nimshare.com).
    //
    // * SubdomainShareSettings: Instanz-Konfiguration (Singleton-Row) —
    //   Basis-Domain, Origin-Host, Cloudflare-Token (verschlüsselt).
    // * ShareLinks/UploadRequests.SubdomainSlug: optionaler DNS-Slug,
    //   unique (gefiltert auf NOT NULL).
    // * Users.CanUseSubdomainShares: Admin-vergebenes Recht pro User.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260726120000_V193_SubdomainSharing")]
    public partial class V193_SubdomainSharing : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "SubdomainShareSettings",
                columns: table => new
                {
                    Id = table.Column<System.Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    BaseDomain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    OriginHost = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    AzureVerificationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CloudflareApiTokenEncrypted = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CloudflareZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<System.DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<System.Guid>(type: "TEXT", nullable: true),
                },
                constraints: table => table.PrimaryKey("PK_SubdomainShareSettings", x => x.Id));

            mb.AddColumn<string>(
                name: "SubdomainSlug",
                table: "ShareLinks",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            mb.AddColumn<string>(
                name: "SubdomainSlug",
                table: "UploadRequests",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            mb.AddColumn<bool>(
                name: "CanUseSubdomainShares",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.CreateIndex(
                name: "IX_ShareLinks_SubdomainSlug",
                table: "ShareLinks",
                column: "SubdomainSlug",
                unique: true,
                filter: "\"SubdomainSlug\" IS NOT NULL");

            mb.CreateIndex(
                name: "IX_UploadRequests_SubdomainSlug",
                table: "UploadRequests",
                column: "SubdomainSlug",
                unique: true,
                filter: "\"SubdomainSlug\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropIndex(name: "IX_UploadRequests_SubdomainSlug", table: "UploadRequests");
            mb.DropIndex(name: "IX_ShareLinks_SubdomainSlug", table: "ShareLinks");
            mb.DropColumn(name: "CanUseSubdomainShares", table: "Users");
            mb.DropColumn(name: "SubdomainSlug", table: "UploadRequests");
            mb.DropColumn(name: "SubdomainSlug", table: "ShareLinks");
            mb.DropTable(name: "SubdomainShareSettings");
        }
    }
}
