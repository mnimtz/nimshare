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
            e.HasOne(x => x.File).WithMany(f => f.ShareLinks).HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
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
    }
}
