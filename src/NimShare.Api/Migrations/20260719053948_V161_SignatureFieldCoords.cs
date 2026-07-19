using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class V161_SignatureFieldCoords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Height",
                table: "SignatureFields",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Width",
                table: "SignatureFields",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "X",
                table: "SignatureFields",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Y",
                table: "SignatureFields",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Height",
                table: "SignatureFields");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "SignatureFields");

            migrationBuilder.DropColumn(
                name: "X",
                table: "SignatureFields");

            migrationBuilder.DropColumn(
                name: "Y",
                table: "SignatureFields");
        }
    }
}
