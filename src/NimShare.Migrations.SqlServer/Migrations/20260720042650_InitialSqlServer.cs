using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiGateways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Endpoint = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EnableAutoSummary = table.Column<bool>(type: "bit", nullable: false),
                    EnableSmartTags = table.Column<bool>(type: "bit", nullable: false),
                    EnableSemanticSearch = table.Column<bool>(type: "bit", nullable: false),
                    EnableGuidedUploadRequests = table.Column<bool>(type: "bit", nullable: false),
                    EnableContentRiskDetection = table.Column<bool>(type: "bit", nullable: false),
                    EnableDraftedShareEmails = table.Column<bool>(type: "bit", nullable: false),
                    EnableChatWithFiles = table.Column<bool>(type: "bit", nullable: false),
                    EnableOcr = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiGateways", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailGateways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SmtpHost = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    SmtpPort = table.Column<int>(type: "int", nullable: false),
                    SmtpUseStartTls = table.Column<bool>(type: "bit", nullable: false),
                    SmtpUsername = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SmtpPasswordEncrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResendApiKeyEncrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailGateways", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OfficeSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DocumentServerUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    JwtSecretEncrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntraOid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    AvatarUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AvatarBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: false),
                    PublicCanRead = table.Column<bool>(type: "bit", nullable: false),
                    PublicCanWrite = table.Column<bool>(type: "bit", nullable: false),
                    PublicCanDelete = table.Column<bool>(type: "bit", nullable: false),
                    PreferredCulture = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    TotpSecret = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TotpEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TotpEnrolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActivityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TokenPrefix = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiTokens_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Company = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UseCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hostname = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    VerificationToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VerificationStatus = table.Column<int>(type: "int", nullable: false),
                    CertificateStatus = table.Column<int>(type: "int", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastVerificationAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
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
                name: "EmailTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailTemplates_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Emoji = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Color = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Folders_Folders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Folders_Groups_OwnerGroupId",
                        column: x => x.OwnerGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Folders_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GroupMemberships",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMemberships", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_GroupMemberships_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invitations_Users_InvitedById",
                        column: x => x.InvitedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LandingTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Subtitle = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BodyMarkdown = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    FooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PrimaryColor = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: true),
                    LogoBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LogoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HeroBlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    HeroUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LandingTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LandingTemplates_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SigningCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SubjectCommonName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NotAfter = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Thumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsSelfIssued = table.Column<bool>(type: "bit", nullable: false),
                    PfxDataEncrypted = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UseCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SigningCertificates_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Href = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SecretEncrypted = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Events = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastDeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Webhooks_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WikiPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ParentPageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ContentMarkdown = table.Column<string>(type: "nvarchar(max)", maxLength: 100000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastEditedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WikiPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WikiPages_Groups_OwnerGroupId",
                        column: x => x.OwnerGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WikiPages_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WikiPages_Users_LastEditedByUserId",
                        column: x => x.LastEditedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WikiPages_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        // v1.10.115: NoAction — WikiPages hat mehrere Users-FKs
                        // (CreatedBy/LastEditedBy), SQL Server verbietet Cascade
                        // zusätzlich dazu.
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_WikiPages_WikiPages_ParentPageId",
                        column: x => x.ParentPageId,
                        principalTable: "WikiPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    BlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ContainerName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Folder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AiSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AiSummaryLang = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: true),
                    AiTags = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AiRiskFlag = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ExtractedText = table.Column<string>(type: "nvarchar(max)", maxLength: 200000, nullable: true),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    KeepVersions = table.Column<int>(type: "int", nullable: false),
                    LockedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadyAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Files_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Files_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Files_Users_LockedByUserId",
                        column: x => x.LockedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Files_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        // v1.10.115: NoAction statt Cascade — SQL Server verbietet
                        // mehrere Cascade-Pfade zur selben Tabelle (Files hat auch
                        // LockedByUserId→Users). Datei-Aufräumen beim Löschen eines
                        // Kontos macht DeleteAccount ohnehin im Code (inkl. Blobs).
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateTable(
                name: "FolderAccessOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MaxPermission = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderAccessOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Groups_TargetGroupId",
                        column: x => x.TargetGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FolderAccessOverrides_Users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UploadRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MaxUploads = table.Column<int>(type: "int", nullable: true),
                    UploadCount = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TargetFolder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TargetFolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotifyOnUpload = table.Column<bool>(type: "bit", nullable: false),
                    IsRevoked = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUploadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecurringDaysOfWeek = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecurringWindowDays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadRequests_Folders_TargetFolderId",
                        column: x => x.TargetFolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UploadRequests_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Permission = table.Column<int>(type: "int", nullable: false),
                    SharedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectShares_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Groups_TargetGroupId",
                        column: x => x.TargetGroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Users_SharedByUserId",
                        column: x => x.SharedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DirectShares_Users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FileEmbeddings",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Vector = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileEmbeddings", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_FileEmbeddings_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShareLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MaxDownloads = table.Column<int>(type: "int", nullable: true),
                    DownloadCount = table.Column<int>(type: "int", nullable: false),
                    HitCount = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    NotifyOnAccess = table.Column<bool>(type: "bit", nullable: false),
                    IsRevoked = table.Column<bool>(type: "bit", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    AllowedEmails = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RequireEmailVerify = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareLinks_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShareLinks_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShareLinks_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SignatureRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InitiatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DeliveryOrder = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Deadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
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
                name: "StorageFileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    BlobPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageFileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageFileVersions_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StorageFileVersions_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserFavorites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFavorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserFavorites_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserFavorites_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserFavorites_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShareLinkAccesses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShareLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Referer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true)
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

            migrationBuilder.CreateTable(
                name: "SignatureAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeclinedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Page = table.Column<int>(type: "int", nullable: false),
                    Anchor = table.Column<int>(type: "int", nullable: false),
                    X = table.Column<double>(type: "float", nullable: false),
                    Y = table.Column<double>(type: "float", nullable: false),
                    Width = table.Column<double>(type: "float", nullable: false),
                    Height = table.Column<double>(type: "float", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SignatureImagePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FilledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
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
                name: "IX_ActivityEvents_ActorUserId_At",
                table: "ActivityEvents",
                columns: new[] { "ActorUserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEvents_At",
                table: "ActivityEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_OwnerUserId",
                table: "ApiTokens",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenHash",
                table: "ApiTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_OwnerUserId_Email",
                table: "Contacts",
                columns: new[] { "OwnerUserId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_OwnerUserId_LastUsedAt",
                table: "Contacts",
                columns: new[] { "OwnerUserId", "LastUsedAt" });

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
                name: "IX_DirectShares_FileId_TargetGroupId",
                table: "DirectShares",
                columns: new[] { "FileId", "TargetGroupId" },
                unique: true,
                filter: "\"FileId\" IS NOT NULL AND \"TargetGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FileId_TargetUserId",
                table: "DirectShares",
                columns: new[] { "FileId", "TargetUserId" },
                unique: true,
                filter: "\"FileId\" IS NOT NULL AND \"TargetUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FolderId_TargetGroupId",
                table: "DirectShares",
                columns: new[] { "FolderId", "TargetGroupId" },
                unique: true,
                filter: "\"FolderId\" IS NOT NULL AND \"TargetGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_FolderId_TargetUserId",
                table: "DirectShares",
                columns: new[] { "FolderId", "TargetUserId" },
                unique: true,
                filter: "\"FolderId\" IS NOT NULL AND \"TargetUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_SharedByUserId",
                table: "DirectShares",
                column: "SharedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetGroupId_FileId",
                table: "DirectShares",
                columns: new[] { "TargetGroupId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetGroupId_FolderId",
                table: "DirectShares",
                columns: new[] { "TargetGroupId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetUserId_FileId",
                table: "DirectShares",
                columns: new[] { "TargetUserId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectShares_TargetUserId_FolderId",
                table: "DirectShares",
                columns: new[] { "TargetUserId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_OwnerUserId_Kind_Locale",
                table: "EmailTemplates",
                columns: new[] { "OwnerUserId", "Kind", "Locale" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_FolderId",
                table: "Files",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_GroupId",
                table: "Files",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_LockedByUserId",
                table: "Files",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_OwnerId",
                table: "Files",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_OwnerId_Folder",
                table: "Files",
                columns: new[] { "OwnerId", "Folder" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Scope_GroupId",
                table: "Files",
                columns: new[] { "Scope", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_CreatedByUserId",
                table: "FolderAccessOverrides",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_FolderId_TargetGroupId",
                table: "FolderAccessOverrides",
                columns: new[] { "FolderId", "TargetGroupId" },
                filter: "\"TargetGroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_FolderId_TargetUserId",
                table: "FolderAccessOverrides",
                columns: new[] { "FolderId", "TargetUserId" },
                filter: "\"TargetUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_TargetGroupId",
                table: "FolderAccessOverrides",
                column: "TargetGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderAccessOverrides_TargetUserId",
                table: "FolderAccessOverrides",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_OwnerGroupId",
                table: "Folders",
                column: "OwnerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_OwnerUserId",
                table: "Folders",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentFolderId_Name",
                table: "Folders",
                columns: new[] { "ParentFolderId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_Scope_OwnerUserId_OwnerGroupId_ParentFolderId",
                table: "Folders",
                columns: new[] { "Scope", "OwnerUserId", "OwnerGroupId", "ParentFolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMemberships_UserId",
                table: "GroupMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Name",
                table: "Groups",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_Email",
                table: "Invitations",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_InvitedById",
                table: "Invitations",
                column: "InvitedById");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_LandingTemplates_OwnerUserId",
                table: "LandingTemplates",
                column: "OwnerUserId",
                unique: true,
                filter: "\"OwnerUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LandingTemplates_Scope",
                table: "LandingTemplates",
                column: "Scope",
                unique: true,
                filter: "\"Scope\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinkAccesses_ShareLinkId_At",
                table: "ShareLinkAccesses",
                columns: new[] { "ShareLinkId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_FileId",
                table: "ShareLinks",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_FolderId",
                table: "ShareLinks",
                column: "FolderId");

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

            migrationBuilder.CreateIndex(
                name: "IX_SigningCertificates_OwnerUserId_NotAfter",
                table: "SigningCertificates",
                columns: new[] { "OwnerUserId", "NotAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_SigningCertificates_OwnerUserId_Thumbprint",
                table: "SigningCertificates",
                columns: new[] { "OwnerUserId", "Thumbprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageFileVersions_CreatedByUserId",
                table: "StorageFileVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageFileVersions_FileId_VersionNumber",
                table: "StorageFileVersions",
                columns: new[] { "FileId", "VersionNumber" },
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
                name: "IX_UploadRequests_TargetFolderId",
                table: "UploadRequests",
                column: "TargetFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_FileId",
                table: "UserFavorites",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_FolderId",
                table: "UserFavorites",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FileId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FileId" },
                unique: true,
                filter: "\"FileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId_FolderId",
                table: "UserFavorites",
                columns: new[] { "UserId", "FolderId" },
                unique: true,
                filter: "\"FolderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_UserId_ReadAt_CreatedAt",
                table: "UserNotifications",
                columns: new[] { "UserId", "ReadAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_EntraOid",
                table: "Users",
                column: "EntraOid");

            migrationBuilder.CreateIndex(
                name: "IX_Webhooks_OwnerUserId",
                table: "Webhooks",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_CreatedByUserId",
                table: "WikiPages",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_LastEditedByUserId",
                table: "WikiPages",
                column: "LastEditedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_OwnerGroupId",
                table: "WikiPages",
                column: "OwnerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_OwnerUserId",
                table: "WikiPages",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_ParentPageId",
                table: "WikiPages",
                column: "ParentPageId");

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Scope_OwnerGroupId",
                table: "WikiPages",
                columns: new[] { "Scope", "OwnerGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_WikiPages_Scope_OwnerUserId",
                table: "WikiPages",
                columns: new[] { "Scope", "OwnerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEvents");

            migrationBuilder.DropTable(
                name: "AiGateways");

            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "CustomDomains");

            migrationBuilder.DropTable(
                name: "DirectShares");

            migrationBuilder.DropTable(
                name: "EmailGateways");

            migrationBuilder.DropTable(
                name: "EmailTemplates");

            migrationBuilder.DropTable(
                name: "FileEmbeddings");

            migrationBuilder.DropTable(
                name: "FolderAccessOverrides");

            migrationBuilder.DropTable(
                name: "GroupMemberships");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "LandingTemplates");

            migrationBuilder.DropTable(
                name: "OfficeSettings");

            migrationBuilder.DropTable(
                name: "ShareLinkAccesses");

            migrationBuilder.DropTable(
                name: "SignatureAudits");

            migrationBuilder.DropTable(
                name: "SignatureFields");

            migrationBuilder.DropTable(
                name: "SigningCertificates");

            migrationBuilder.DropTable(
                name: "StorageFileVersions");

            migrationBuilder.DropTable(
                name: "UploadRequests");

            migrationBuilder.DropTable(
                name: "UserFavorites");

            migrationBuilder.DropTable(
                name: "UserNotifications");

            migrationBuilder.DropTable(
                name: "Webhooks");

            migrationBuilder.DropTable(
                name: "WikiPages");

            migrationBuilder.DropTable(
                name: "ShareLinks");

            migrationBuilder.DropTable(
                name: "SignatureParticipants");

            migrationBuilder.DropTable(
                name: "SignatureRequests");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "Folders");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
