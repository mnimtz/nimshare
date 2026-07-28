using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.38 — WikiPages war nie fertig verdrahtet (kein Controller, keine
    // Views, keine Route seit V171) — Marcus: "brauchen wir nicht", aufgeräumt.
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260728160000_V201_DropWikiPages")]
    public partial class V201_DropWikiPages : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.DropTable(name: "WikiPages");
        }

        protected override void Down(MigrationBuilder mb)
        {
            // Absichtlich kein Wiederaufbau — totes Feature, siehe V171 falls
            // die Tabelle je wieder gebraucht werden sollte.
        }
    }
}
