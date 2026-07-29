using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.44 — Marcus: die "Dokumentation"-Checkbox trug bisher selbst
    // einen festen, hartkodierten URL-Wert (docshield.tungstenautomation.com)
    // statt nur als Ein/Aus-Schalter für die flexiblen Key-Store-Dokumente
    // zu dienen. DocumentationUrl → DocumentationEnabled (bool); bestehende
    // Links mit gesetztem DocumentationUrl behalten die Checkbox aktiviert.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260729080000_V202_ShareLinkDocumentationEnabled")]
    public partial class V202_ShareLinkDocumentationEnabled : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<bool>(
                name: "DocumentationEnabled",
                table: "ShareLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            mb.Sql("UPDATE ShareLinks SET DocumentationEnabled = 1 WHERE DocumentationUrl IS NOT NULL AND DocumentationUrl <> ''");

            mb.DropColumn(name: "DocumentationUrl", table: "ShareLinks");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.AddColumn<string>(
                name: "DocumentationUrl",
                table: "ShareLinks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            mb.DropColumn(name: "DocumentationEnabled", table: "ShareLinks");
        }
    }
}
