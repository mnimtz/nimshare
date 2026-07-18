using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntraOid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    QuotaBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    PreferredCulture = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    VerificationToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VerificationStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    CertificateStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastVerificationAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomDomains_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    BlobPath = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReadyAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Files_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UploadRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MaxUploads = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TargetFolder = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    NotifyOnUpload = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUploadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadRequests_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShareLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MaxDownloads = table.Column<int>(type: "INTEGER", nullable: true),
                    DownloadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    NotifyOnAccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAccessAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareLinks_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShareLinks_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShareLinkAccesses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShareLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IpHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Referer = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareLinkAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareLinkAccesses_ShareLinks_ShareLinkId",
                        column: x => x.ShareLinkId,
                        principalTable: "ShareLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomDomains_Hostname",
                table: "CustomDomains",
                column: "Hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomDomains_OwnerId",
                table: "CustomDomains",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_OwnerId",
                table: "Files",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_OwnerId_Folder",
                table: "Files",
                columns: new[] { "OwnerId", "Folder" });

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinkAccesses_ShareLinkId_At",
                table: "ShareLinkAccesses",
                columns: new[] { "ShareLinkId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_FileId",
                table: "ShareLinks",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_OwnerId",
                table: "ShareLinks",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_Slug",
                table: "ShareLinks",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadRequests_OwnerId",
                table: "UploadRequests",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadRequests_Slug",
                table: "UploadRequests",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EntraOid",
                table: "Users",
                column: "EntraOid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomDomains");

            migrationBuilder.DropTable(
                name: "ShareLinkAccesses");

            migrationBuilder.DropTable(
                name: "UploadRequests");

            migrationBuilder.DropTable(
                name: "ShareLinks");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
