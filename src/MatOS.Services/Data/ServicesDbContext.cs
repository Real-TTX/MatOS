using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Data;

/// <summary>
/// The service's database (<c>/data/services.db</c>): the installations, the registration log, and the accounts and the audit of the admin area.
/// </summary>
/// <param name="http">Who changes a row of the admin area (its audit columns); absent outside a request (the jobs, the design-time factory).</param>
public sealed class ServicesDbContext(DbContextOptions<ServicesDbContext> options, IHttpContextAccessor? http = null) : DbContext(options)
{
    public DbSet<Instance> Instances => Set<Instance>();

    public DbSet<Registration> Registrations => Set<Registration>();

    public DbSet<CustomDomain> Domains => Set<CustomDomain>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<AdminAction> AdminActions => Set<AdminAction>();

    /// <summary>
    /// Every time is UTC, but SQLite stores text without a kind and EF Core reads it back as <see cref="DateTimeKind.Unspecified"/>, which
    /// JSON then writes without an offset (a client reads it as its local time). The converter marks what is read as UTC.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Instance>(e =>
        {
            e.ToTable("Instance");
            e.Property(i => i.Name).HasMaxLength(32).IsRequired();
            e.HasIndex(i => i.Name).IsUnique();
            e.Property(i => i.KeyHash).HasMaxLength(64).IsRequired();
            e.HasIndex(i => i.KeyHash).IsUnique();
            // Stored as text, so the database stays readable with sqlite3 alone.
            e.Property(i => i.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(i => i.PublicIpv4).HasMaxLength(15);
            e.Property(i => i.PublicIpv6).HasMaxLength(45);
            e.Property(i => i.Version).HasMaxLength(64);
            e.Property(i => i.CreatedFromIp).HasMaxLength(45);
            e.Property(i => i.LastPortCheck).HasMaxLength(2000);
            e.Property(i => i.BlockedReason).HasMaxLength(300);
            e.HasMany(i => i.Domains).WithOne(d => d.Instance).HasForeignKey(d => d.InstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomDomain>(e =>
        {
            e.ToTable("CustomDomain");
            e.Property(d => d.Domain).HasMaxLength(253).IsRequired();
            e.HasIndex(d => d.Domain).IsUnique();
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Detail).HasMaxLength(300);
        });

        modelBuilder.Entity<Registration>(e =>
        {
            e.ToTable("Registration");
            e.Property(r => r.Ip).HasMaxLength(45).IsRequired();
            e.HasIndex(r => new { r.Ip, r.CreateDate });
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("User");
            e.Property(u => u.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(u => u.Email).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.Property(u => u.Name).HasMaxLength(256).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(u => u.Theme).HasMaxLength(16);
            e.HasQueryFilter(u => u.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.Property(s => s.Token).HasMaxLength(64).IsRequired();
            e.HasIndex(s => s.Token).IsUnique();
            e.Property(s => s.UserAgent).HasMaxLength(512);
            e.Property(s => s.IpAddress).HasMaxLength(64);
            e.HasOne(s => s.User).WithMany(u => u.Sessions).HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => s.UpdateState != UpdateState.Deleted);
        });

        modelBuilder.Entity<AdminAction>(e =>
        {
            e.ToTable("AdminAction");
            e.Property(a => a.UserEmail).HasMaxLength(256).IsRequired();
            e.Property(a => a.Action).HasMaxLength(32).IsRequired();
            e.Property(a => a.InstanceName).HasMaxLength(32).IsRequired();
            e.Property(a => a.Detail).HasMaxLength(300);
            e.HasIndex(a => new { a.InstanceName, a.CreateDate });
            e.HasQueryFilter(a => a.UpdateState != UpdateState.Deleted);
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

    /// <summary>Fills the audit columns of the new tables and turns a delete of their rows into a soft delete (as in MatOS's database).</summary>
    private void ApplyAudit()
    {
        var now = DateTime.UtcNow;
        long? userId = long.TryParse(http?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
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
                    entry.State = EntityState.Modified;
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Entity.UpdateState = UpdateState.Deleted;
                    break;
            }
        }
    }
}

/// <summary>Stores a time as it is and reads it back as UTC (see <see cref="ServicesDbContext"/>).</summary>
internal sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
