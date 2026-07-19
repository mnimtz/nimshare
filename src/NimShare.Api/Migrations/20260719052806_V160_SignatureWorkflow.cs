using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class V160_SignatureWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignatureRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Deadline = table.Column<long>(type: "INTEGER", nullable: true),
                    FinalFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_Files_FinalFileId",
                        column: x => x.FinalFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_Files_SourceFileId",
                        column: x => x.SourceFileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_Users_InitiatorUserId",
                        column: x => x.InitiatorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignatureAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    IpHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureAudits_SignatureRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "SignatureRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignatureParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ViewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SignedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeclinedReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IpHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureParticipants_SignatureRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "SignatureRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignatureFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Page = table.Column<int>(type: "INTEGER", nullable: false),
                    Anchor = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Value = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SignatureImagePath = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    FilledAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureFields_SignatureParticipants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "SignatureParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureFields_SignatureRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "SignatureRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureAudits_RequestId_At",
                table: "SignatureAudits",
                columns: new[] { "RequestId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureFields_ParticipantId",
                table: "SignatureFields",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureFields_RequestId",
                table: "SignatureFields",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureParticipants_RequestId",
                table: "SignatureParticipants",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureParticipants_TokenHash",
                table: "SignatureParticipants",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_FinalFileId",
                table: "SignatureRequests",
                column: "FinalFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_InitiatorUserId",
                table: "SignatureRequests",
                column: "InitiatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_SourceFileId",
                table: "SignatureRequests",
                column: "SourceFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignatureAudits");

            migrationBuilder.DropTable(
                name: "SignatureFields");

            migrationBuilder.DropTable(
                name: "SignatureParticipants");

            migrationBuilder.DropTable(
                name: "SignatureRequests");
        }
    }
}
