using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NimShare.Core.Entities;

namespace NimShare.Core.Data;

public class NimShareDbContext : DbContext
{
    public NimShareDbContext(DbContextOptions<NimShareDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<StorageFile> Files => Set<StorageFile>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<ShareLinkAccess> ShareLinkAccesses => Set<ShareLinkAccess>();
    public DbSet<CustomDomain> CustomDomains => Set<CustomDomain>();
    public DbSet<UploadRequestLink> UploadRequests => Set<UploadRequestLink>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<EmailGatewaySettings> EmailGateways => Set<EmailGatewaySettings>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<AiGatewaySettings> AiGateways => Set<AiGatewaySettings>();
    public DbSet<FileEmbedding> FileEmbeddings => Set<FileEmbedding>();
    public DbSet<DirectShare> DirectShares => Set<DirectShare>();
    public DbSet<UserFavorite> UserFavorites => Set<UserFavorite>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<LandingTemplate> LandingTemplates => Set<LandingTemplate>();
    public DbSet<StorageFileVersion> StorageFileVersions => Set<StorageFileVersion>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<FolderAccessOverride> FolderAccessOverrides => Set<FolderAccessOverride>();
    public DbSet<SignatureRequest> SignatureRequests => Set<SignatureRequest>();
    public DbSet<SignatureParticipant> SignatureParticipants => Set<SignatureParticipant>();
    public DbSet<SignatureField> SignatureFields => Set<SignatureField>();
    public DbSet<SignatureAudit> SignatureAudits => Set<SignatureAudit>();
    public DbSet<OfficeSettings> OfficeSettings => Set<OfficeSettings>();
    public DbSet<WikiPage> WikiPages => Set<WikiPage>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<SigningCertificate> SigningCertificates => Set<SigningCertificate>();
    public DbSet<FilePin> FilePins => Set<FilePin>();
    // v1.10.82: App-Store-Blocker (Apple 1.2 UGC-Guideline)
    public DbSet<BlockedUser> BlockedUsers => Set<BlockedUser>();
    public DbSet<ContentReport> ContentReports => Set<ContentReport>();
    public DbSet<LinkEntry> LinkEntries => Set<LinkEntry>();
    // v1.10.153: Root-CA der Instanz — Singleton. Signiert alle in-app
    // erzeugten User-Signing-Certs, damit Empfänger die NimShare-Root einmal
    // importieren und alle signierten Links dieser Instanz automatisch valid
    // sind.
    public DbSet<InstanceCa> InstanceCas => Set<InstanceCa>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Sqlite does not support ORDER BY on DateTimeOffset — apply a value
        // converter that stores them as long ticks so queries work in both
        // Sqlite (dev) and SQL Server (prod).
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var converter = new ValueConverter<DateTimeOffset, long>(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            var nullableConverter = new ValueConverter<DateTimeOffset?, long?>(
                v => v.HasValue ? v.Value.UtcTicks : null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
            foreach (var entityType in b.Model.GetEntityTypes())
            {
                foreach (var prop in entityType.GetProperties())
                {
                    if (prop.ClrType == typeof(DateTimeOffset))
                        prop.SetValueConverter(converter);
                    else if (prop.ClrType == typeof(DateTimeOffset?))
                        prop.SetValueConverter(nullableConverter);
                }
            }
        }

        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            // EntraOid is unique when non-empty; empty is the sentinel for local-only accounts.
            e.HasIndex(x => x.EntraOid);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.EntraOid).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(120);
            e.Property(x => x.PreferredCulture).HasMaxLength(5).IsRequired();
            e.Property(x => x.AvatarBlobPath).HasMaxLength(400);
            e.Property(x => x.TotpSecret).HasMaxLength(64);
        });

        b.Entity<StorageFileVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.FileId, x.VersionNumber }).IsUnique();
            e.Property(x => x.BlobPath).HasMaxLength(400).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserNotification>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Body).HasMaxLength(1000);
            e.Property(x => x.Href).HasMaxLength(500);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FolderAccessOverride>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.FolderId, x.TargetUserId }).HasFilter("\"TargetUserId\" IS NOT NULL");
            e.HasIndex(x => new { x.FolderId, x.TargetGroupId }).HasFilter("\"TargetGroupId\" IS NOT NULL");
            e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.TargetUser).WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.TargetGroup).WithMany().HasForeignKey(x => x.TargetGroupId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ShareLink>().Property(x => x.AllowedEmails).HasMaxLength(2000);

        b.Entity<SignatureRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InitiatorUserId);
            e.HasIndex(x => x.SourceFileId);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2000);
            e.HasOne(x => x.SourceFile).WithMany().HasForeignKey(x => x.SourceFileId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FinalFile).WithMany().HasForeignKey(x => x.FinalFileId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.Initiator).WithMany().HasForeignKey(x => x.InitiatorUserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SignatureParticipant>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RequestId);
            e.HasIndex(x => x.TokenHash);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(120);
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.DeclinedReason).HasMaxLength(500);
            e.HasOne(x => x.Request).WithMany(r => r.Participants).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SignatureField>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RequestId);
            e.Property(x => x.Label).HasMaxLength(120);
            e.Property(x => x.Value).HasMaxLength(500);
            e.Property(x => x.SignatureImagePath).HasMaxLength(400);
            e.HasOne(x => x.Request).WithMany(r => r.Fields).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Participant).WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SignatureAudit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RequestId, x.At });
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<SignatureRequest>().WithMany(r => r.Audits).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StorageFile>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => new { x.Scope, x.GroupId });
            e.HasIndex(x => new { x.OwnerId, x.Folder });
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
            e.Property(x => x.BlobPath).HasMaxLength(400).IsRequired();
            e.Property(x => x.ContainerName).HasMaxLength(60).IsRequired();
            e.Property(x => x.Folder).HasMaxLength(400);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.AiSummary).HasMaxLength(2000);
            e.Property(x => x.AiSummaryLang).HasMaxLength(5);
            e.Property(x => x.ExtractedText).HasMaxLength(200_000);
            e.Property(x => x.AiTags).HasMaxLength(500);
            e.Property(x => x.AiRiskFlag).HasMaxLength(120);
            e.HasOne(x => x.Owner).WithMany(u => u.Files).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Group).WithMany(g => g.Files).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Group>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
        });

        b.Entity<GroupMembership>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Groups).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Invitation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.TokenHash);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(120).IsRequired();
        });

        b.Entity<EmailGatewaySettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FromAddress).HasMaxLength(320).IsRequired();
            e.Property(x => x.FromName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SmtpHost).HasMaxLength(253);
            e.Property(x => x.SmtpUsername).HasMaxLength(200);
            e.Property(x => x.SmtpPasswordEncrypted).HasMaxLength(2000);
            e.Property(x => x.ResendApiKeyEncrypted).HasMaxLength(2000);
        });

        b.Entity<AiGatewaySettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ApiKeyEncrypted).HasMaxLength(2000);
            e.Property(x => x.Model).HasMaxLength(120);
            e.Property(x => x.Endpoint).HasMaxLength(400);
        });

        b.Entity<FileEmbedding>(e =>
        {
            e.HasKey(x => x.FileId);
            e.Property(x => x.Model).HasMaxLength(120).IsRequired();
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Folder>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Scope, x.OwnerUserId, x.OwnerGroupId, x.ParentFolderId });
            e.HasIndex(x => new { x.ParentFolderId, x.Name });
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.Emoji).HasMaxLength(8);
            e.Property(x => x.Color).HasMaxLength(8);
            e.HasOne(x => x.Parent).WithMany(p => p.Children).HasForeignKey(x => x.ParentFolderId).OnDelete(DeleteBehavior.Restrict);
            // NoAction rather than Cascade: Users also cascades directly into
            // Files (OwnerId), and Folder → Files is Restrict. Cascading Users
            // → Folders would give SqlServer two cascade paths ending in
            // File-side cleanup and it refuses ("may cause cycles or multiple
            // cascade paths"). App-layer already deletes folders explicitly
            // when purging a user, so runtime behaviour is unchanged; only
            // the referential-integrity policy on hard-delete flips.
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.OwnerGroup).WithMany().HasForeignKey(x => x.OwnerGroupId).OnDelete(DeleteBehavior.NoAction);
        });

        // Wire the new StorageFile.FolderId (nullable, restrict on delete — the app
        // moves files out of a folder before allowing folder delete).
        b.Entity<OfficeSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DocumentServerUrl).HasMaxLength(400);
            e.Property(x => x.JwtSecretEncrypted).HasMaxLength(2000);
        });

        b.Entity<ApiToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash);
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(120).IsRequired();
            e.Property(x => x.TokenPrefix).HasMaxLength(12).IsRequired();
            e.Property(x => x.Scopes).HasMaxLength(500);
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Webhook>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.Url).HasMaxLength(500).IsRequired();
            e.Property(x => x.SecretEncrypted).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Events).HasMaxLength(500);
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmailTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerUserId, x.Kind, x.Locale });
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(400).IsRequired();
            e.Property(x => x.BodyMarkdown).HasMaxLength(10_000).IsRequired();
            e.Property(x => x.Locale).HasMaxLength(8).IsRequired();
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Contact>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerUserId, x.Email });
            e.HasIndex(x => new { x.OwnerUserId, x.LastUsedAt });
            e.Property(x => x.Email).HasMaxLength(250).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Company).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Tags).HasMaxLength(500);
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SigningCertificate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerUserId, x.NotAfter });
            e.HasIndex(x => new { x.OwnerUserId, x.Thumbprint }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.SubjectCommonName).HasMaxLength(240).IsRequired();
            e.Property(x => x.Issuer).HasMaxLength(240);
            e.Property(x => x.Thumbprint).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        // v1.10.153: FK von ShareLink/UploadRequestLink → SigningCertificate
        // explizit als SetNull. Die 409-„Zertifikat in Verwendung"-Guard im
        // CertificatesApiController.Delete verhindert nur den direkten Cert-
        // Delete. Wenn ein User gelöscht wird, cascadet SigningCertificate
        // (Owner-FK) mit — die Links bleiben aber bestehen und verlieren nur
        // ihr Signer-Badge (SigningCertificateId → null). Für einen sauberen
        // "Snapshot bleibt sichtbar"-Weg müssten Subject/Thumbprint in den
        // Link denormalisiert werden — offen für spätere Iteration.
        b.Entity<ShareLink>()
            .HasOne(l => l.SigningCertificate)
            .WithMany()
            .HasForeignKey(l => l.SigningCertificateId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<UploadRequestLink>()
            .HasOne(l => l.SigningCertificate)
            .WithMany()
            .HasForeignKey(l => l.SigningCertificateId)
            .OnDelete(DeleteBehavior.SetNull);

        // v1.10.153: Root-CA der Instanz — Singleton.
        b.Entity<InstanceCa>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IsActive, x.NotAfter });
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.SubjectDn).HasMaxLength(400).IsRequired();
            e.Property(x => x.Thumbprint).HasMaxLength(64).IsRequired();
        });

        b.Entity<FilePin>(e =>
        {
            e.HasKey(x => x.Id);
            // A user can only pin the same file once — the unique index also
            // makes the "pin already exists?" check a single row lookup.
            e.HasIndex(x => new { x.UserId, x.FileId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.PinnedAt });
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // File-cascade means deleting the underlying file removes every pin
            // in one shot; the app never sees a dangling pin.
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
        });

        // v1.10.82: App-Store-Blocker (Apple Guideline 1.2 — UGC-Support)
        b.Entity<BlockedUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.BlockedUserId }).IsUnique();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // BlockedUserRef nur Restrict — sonst würde das Löschen eines
            // blockierten Users den Block-Eintrag mitreißen und der User könnte
            // uns nach Neuanlage wieder ungeblockt kontaktieren. Löschung des
            // Blocked-User räumt dessen Block-Zeilen im UserDeletionService auf.
            e.HasOne(x => x.BlockedUserRef).WithMany().HasForeignKey(x => x.BlockedUserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ContentReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.SubjectKind, x.SubjectId });
            e.HasIndex(x => x.ReporterUserId);
            e.Property(x => x.SubjectLabel).HasMaxLength(500);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.ResolutionNote).HasMaxLength(2000);
            e.HasOne(x => x.Reporter).WithMany().HasForeignKey(x => x.ReporterUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ResolvedBy).WithMany().HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.SetNull).IsRequired(false);
        });

        b.Entity<WikiPage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Scope, x.OwnerUserId });
            e.HasIndex(x => new { x.Scope, x.OwnerGroupId });
            e.HasIndex(x => x.ParentPageId);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            e.Property(x => x.ContentMarkdown).HasMaxLength(100_000);
            e.HasOne(x => x.ParentPage).WithMany().HasForeignKey(x => x.ParentPageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade).IsRequired(false);
            e.HasOne(x => x.OwnerGroup).WithMany().HasForeignKey(x => x.OwnerGroupId).OnDelete(DeleteBehavior.Cascade).IsRequired(false);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LastEditedByUser).WithMany().HasForeignKey(x => x.LastEditedByUserId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        });

        b.Entity<StorageFile>()
            .HasOne(f => f.LockedByUser)
            .WithMany()
            .HasForeignKey(f => f.LockedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        b.Entity<StorageFile>().Property(x => x.LockKind).HasMaxLength(20);

        b.Entity<StorageFile>()
            .HasOne(f => f.FolderRef)
            .WithMany(fo => fo.Files)
            .HasForeignKey(f => f.FolderId)
            .OnDelete(DeleteBehavior.Restrict);

        // ShareLink now allows FileId OR FolderId (nullable both). FileId's existing
        // FK from an earlier config is loosened here.
        b.Entity<ShareLink>()
            .HasOne(l => l.Folder)
            .WithMany()
            .HasForeignKey(l => l.FolderId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<UploadRequestLink>()
            .HasOne(l => l.TargetFolderRef)
            .WithMany()
            .HasForeignKey(l => l.TargetFolderId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<ShareLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.FileId);
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(120);
            e.Property(x => x.Message).HasMaxLength(2000);
            // Single cascade path: User -> Files -> ShareLinks. Setting either
            // relationship to Cascade on top would create a "multiple cascade
            // paths" conflict on SQL Server. Both are Restrict; the code path
            // that deletes a User is expected to soft-delete or explicitly
            // clean up files/links first.
            e.HasOne(x => x.File).WithMany(f => f.ShareLinks).HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.Owner).WithMany(u => u.ShareLinks).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ShareLinkAccess>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ShareLinkId, x.At });
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.Referer).HasMaxLength(300);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            // v1.10.156: optionale Klartext-IP (max 45 = längste IPv6-Textform).
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasOne(x => x.ShareLink).WithMany(l => l.Accesses).HasForeignKey(x => x.ShareLinkId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomDomain>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Hostname).IsUnique();
            e.HasIndex(x => x.OwnerId);
            e.Property(x => x.Hostname).HasMaxLength(253).IsRequired();
            e.Property(x => x.VerificationToken).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.Owner).WithMany(u => u.CustomDomains).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UploadRequestLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.OwnerId);
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(120);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.TargetFolder).HasMaxLength(400);
            e.HasOne(x => x.Owner).WithMany(u => u.UploadRequests).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DirectShare>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TargetUserId, x.FileId });
            e.HasIndex(x => new { x.TargetUserId, x.FolderId });
            e.HasIndex(x => new { x.TargetGroupId, x.FileId });
            e.HasIndex(x => new { x.TargetGroupId, x.FolderId });
            // Concurrency-safe upsert: two POSTs to /direct-shares with the
            // same (item, target) can now only produce one row — the second
            // hits the unique index and we treat it as "already there".
            e.HasIndex(x => new { x.FileId, x.TargetUserId }).IsUnique().HasFilter("\"FileId\" IS NOT NULL AND \"TargetUserId\" IS NOT NULL");
            e.HasIndex(x => new { x.FileId, x.TargetGroupId }).IsUnique().HasFilter("\"FileId\" IS NOT NULL AND \"TargetGroupId\" IS NOT NULL");
            e.HasIndex(x => new { x.FolderId, x.TargetUserId }).IsUnique().HasFilter("\"FolderId\" IS NOT NULL AND \"TargetUserId\" IS NOT NULL");
            e.HasIndex(x => new { x.FolderId, x.TargetGroupId }).IsUnique().HasFilter("\"FolderId\" IS NOT NULL AND \"TargetGroupId\" IS NOT NULL");
            // Restrict everywhere: DB won't cascade-delete these; controllers
            // clean them up explicitly when the target file/folder or user/group
            // goes away, so we don't hit SQL Server's multi-cascade-path guard.
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.TargetUser).WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.TargetGroup).WithMany().HasForeignKey(x => x.TargetGroupId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.SharedByUser).WithMany().HasForeignKey(x => x.SharedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserFavorite>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.FileId }).IsUnique().HasFilter("\"FileId\" IS NOT NULL");
            e.HasIndex(x => new { x.UserId, x.FolderId }).IsUnique().HasFilter("\"FolderId\" IS NOT NULL");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        });

        b.Entity<ActivityEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ActorUserId, x.At });
            e.HasIndex(x => x.At);
            e.Property(x => x.Summary).HasMaxLength(400).IsRequired();
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LandingTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            // Only one Global row; only one UserPersonal per user.
            e.HasIndex(x => x.Scope).HasFilter("\"Scope\" = 0").IsUnique();
            e.HasIndex(x => x.OwnerUserId).HasFilter("\"OwnerUserId\" IS NOT NULL").IsUnique();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Subtitle).HasMaxLength(400);
            e.Property(x => x.BodyMarkdown).HasMaxLength(4000);
            e.Property(x => x.FooterText).HasMaxLength(500);
            e.Property(x => x.PrimaryColor).HasMaxLength(9);
            e.Property(x => x.LogoBlobPath).HasMaxLength(400);
            e.Property(x => x.LogoUrl).HasMaxLength(500);
            e.Property(x => x.HeroBlobPath).HasMaxLength(400);
            e.Property(x => x.HeroUrl).HasMaxLength(500);
            e.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
