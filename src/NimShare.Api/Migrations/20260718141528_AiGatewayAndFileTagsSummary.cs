using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class AiGatewayAndFileTagsSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiRiskFlag",
                table: "Files",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiSummary",
                table: "Files",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiTags",
                table: "Files",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiGateways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    EnableAutoSummary = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableSmartTags = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableSemanticSearch = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableGuidedUploadRequests = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableContentRiskDetection = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableDraftedShareEmails = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableChatWithFiles = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableOcr = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiGateways", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiGateways");

            migrationBuilder.DropColumn(
                name: "AiRiskFlag",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "AiSummary",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "AiTags",
                table: "Files");
        }
    }
}
