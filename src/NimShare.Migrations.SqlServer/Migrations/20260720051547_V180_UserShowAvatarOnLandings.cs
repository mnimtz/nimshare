using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class V180_UserShowAvatarOnLandings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowAvatarOnLandings",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowAvatarOnLandings",
                table: "Users");
        }
    }
}
