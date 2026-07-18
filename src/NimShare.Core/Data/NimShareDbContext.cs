using Microsoft.EntityFrameworkCore;
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

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EntraOid).IsUnique();
            e.HasIndex(x => x.Email);
            e.Property(x => x.EntraOid).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.PreferredCulture).HasMaxLength(5).IsRequired();
        });

        b.Entity<StorageFile>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => new { x.OwnerId, x.Folder });
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
            e.Property(x => x.BlobPath).HasMaxLength(400).IsRequired();
            e.Property(x => x.ContainerName).HasMaxLength(60).IsRequired();
            e.Property(x => x.Folder).HasMaxLength(400);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasOne(x => x.Owner).WithMany(u => u.Files).HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
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
