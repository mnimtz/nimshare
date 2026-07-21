using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations;

// v1.10.104 — Zwilling der Core-Migration für SqlServer. Sqlite kennt
// INTEGER für bool, SqlServer nutzt bit.
[DbContext(typeof(NimShareDbContext))]
[Migration("20260721145510_V184_FolderIsPrivate")]
public partial class V184_FolderIsPrivate : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        mb.AddColumn<bool>(
            name: "IsPrivate",
            table: "Folders",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder mb)
    {
        mb.DropColumn(name: "IsPrivate", table: "Folders");
    }
}
