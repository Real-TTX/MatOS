using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Data;

/// <summary>
/// EF Core context for the SQLite logic database. Enforces the project conventions centrally:
/// PascalCase singular table names, a BIGINT <c>Id</c> primary key, audit columns that are filled
/// automatically, and soft delete (a <c>Remove</c> becomes an <see cref="UpdateState.Deleted"/> update
/// and deleted rows are hidden by a global query filter).
/// </summary>
public class MatOSDbContext : DbContext
{
    private readonly ICurrentUserAccessor _currentUser;

    public MatOSDbContext(DbContextOptions<MatOSDbContext> options, ICurrentUserAccessor currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<InstalledApp> InstalledApps => Set<InstalledApp>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<AppBinding> AppBindings => Set<AppBinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.Theme).HasMaxLength(16).IsRequired();
            e.Property(x => x.Wallpaper).HasMaxLength(16).IsRequired().HasDefaultValue("orbit");
            e.Property(x => x.ExternalSubject).HasMaxLength(64);
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL AND \"UpdateState\" <> 0");
            e.Property(x => x.Role).HasConversion<int>();
            // Unique only among non-deleted rows, so a username can be reused after a soft delete.
            e.HasIndex(x => x.Username).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasKey(x => x.Id);
            e.Property(x => x.Token).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.IdentitySessionId).HasMaxLength(128);
            e.HasIndex(x => x.IdentitySessionId);
            e.HasOne(x => x.User)
                .WithMany(u => u.Sessions)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<InstalledApp>(e =>
        {
            e.ToTable("InstalledApp");
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.CatalogId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
            e.Property(x => x.ProjectName).HasMaxLength(100).IsRequired();
            e.Property(x => x.UiService).HasMaxLength(100);
            e.Property(x => x.PublicUrl).HasMaxLength(500);
            e.Property(x => x.UiPath).HasMaxLength(200);
            e.Property(x => x.Lifecycle).HasConversion<int>();
            e.Property(x => x.StatusMessage).HasMaxLength(1000);
            e.Property(x => x.Engine).HasMaxLength(16).IsRequired();
            e.Property(x => x.ComposeSha).HasMaxLength(64);
            // The slug is the compose project key and the folder name; reusable after a soft delete.
            e.HasIndex(x => x.Slug).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasIndex(x => x.CatalogId);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.ToTable("Job");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.AppSlug).HasMaxLength(64);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.AppId);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<Backup>(e =>
        {
            e.ToTable("Backup");
            e.HasKey(x => x.Id);
            e.Property(x => x.AppSlug).HasMaxLength(64).IsRequired();
            e.Property(x => x.AppName).HasMaxLength(200).IsRequired();
            e.Property(x => x.CatalogId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Folder).HasMaxLength(100).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => x.AppSlug);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<BackupSchedule>(e =>
        {
            e.ToTable("BackupSchedule");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Frequency).HasConversion<int>();
            e.Property(x => x.Weekday).HasConversion<int>();
            e.Property(x => x.LastStatus).HasMaxLength(500);
            e.HasIndex(x => x.AppId);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<ShareLink>(e =>
        {
            e.ToTable("ShareLink");
            e.HasKey(x => x.Id);
            e.Property(x => x.Token).HasMaxLength(64).IsRequired();
            e.Property(x => x.Label).HasMaxLength(200);
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.AppId);
            e.HasOne(x => x.App)
                .WithMany()
                .HasForeignKey(x => x.AppId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<AppBinding>(e =>
        {
            e.ToTable("AppBinding");
            e.HasKey(x => x.Id);
            e.Property(x => x.Capability).HasMaxLength(100).IsRequired();
            e.Property(x => x.VariablesProtected).IsRequired();
            e.HasIndex(x => new { x.ConsumerAppId, x.Capability });
            e.HasIndex(x => x.ProviderAppId);
            e.HasOne(x => x.ConsumerApp).WithMany().HasForeignKey(x => x.ConsumerAppId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ProviderApp).WithMany().HasForeignKey(x => x.ProviderAppId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.UpdateState != UpdateState.Deleted);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills audit columns and turns hard deletes into soft deletes.</summary>
    private void ApplyAudit()
    {
        var now = DateTime.UtcNow;
        var userId = _currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreateDate = now;
                    entry.Entity.CreateUserId = userId;
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Entity.UpdateState = UpdateState.Created;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    if (entry.Entity.UpdateState != UpdateState.Deleted)
                    {
                        entry.Entity.UpdateState = UpdateState.Updated;
                    }

                    break;

                case EntityState.Deleted:
                    // Soft delete: keep the row, flag it and hide it via the query filter.
                    entry.State = EntityState.Modified;
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Entity.UpdateState = UpdateState.Deleted;
                    break;
            }
        }
    }
}
