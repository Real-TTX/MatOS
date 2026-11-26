using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Data;

/// <summary>The service's database (<c>/data/services.db</c>): the installations and the registration log.</summary>
public sealed class ServicesDbContext(DbContextOptions<ServicesDbContext> options) : DbContext(options)
{
    public DbSet<Instance> Instances => Set<Instance>();

    public DbSet<Registration> Registrations => Set<Registration>();

    public DbSet<CustomDomain> Domains => Set<CustomDomain>();

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
    }
}

/// <summary>Stores a time as it is and reads it back as UTC (see <see cref="ServicesDbContext"/>).</summary>
internal sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
