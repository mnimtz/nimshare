using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NimShare.Core.Data;

#nullable disable

namespace NimShare.Api.Migrations
{
    // v1.11.13 — Admin kann beim Einladen die Sprache des eingeladenen
    // Nutzers wählen; Invite-/Reminder-Mail geht dann in dieser Sprache raus
    // statt fest verdrahtetem Englisch (Send) bzw. Deutsch (Resend).
    [DbContext(typeof(NimShareDbContext))]
    [Migration("20260727140000_V194_InvitationLanguage")]
    public partial class V194_InvitationLanguage : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<string>(
                name: "Language",
                table: "Invitations",
                type: "TEXT",
                maxLength: 5,
                nullable: false,
                defaultValue: "en");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "Language", table: "Invitations");
        }
    }
}
