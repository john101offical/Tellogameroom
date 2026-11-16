using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TelloGameRoom.Core.Enums;
using TelloGameRoom.Server.Data.Entities;

namespace TelloGameRoom.Server.Data;

/// <summary>
/// The single source of truth. The model avoids provider-specific types (money is integer cents, times are UTC
/// <see cref="DateTime"/>) so PostgreSQL can replace SQLite with a new migrations set and no model changes.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ShopSettings> ShopSettings => Set<ShopSettings>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<EmployeePermissionOverride> EmployeePermissionOverrides => Set<EmployeePermissionOverride>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserBranch> UserBranches => Set<UserBranch>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<MachineGroup> MachineGroups => Set<MachineGroup>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime in the model is UTC. Stamp the kind on the way out of the database.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShopSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ShopName).HasMaxLength(100);
            e.Property(x => x.LogoContentType).HasMaxLength(50);
            e.Property(x => x.DefaultLanguage).HasMaxLength(10);
        });

        modelBuilder.Entity<Branch>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.CurrencyCode).HasMaxLength(3);
            e.Property(x => x.CurrencySymbol).HasMaxLength(5);
            e.Property(x => x.AdminPinHash).HasMaxLength(256);
        });

        modelBuilder.Entity<EmployeePermissionOverride>(e => e.HasKey(x => x.Permission));

        modelBuilder.Entity<User>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(32);
            e.Property(x => x.NormalizedUsername).HasMaxLength(32);
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
        });

        modelBuilder.Entity<UserBranch>(e =>
        {
            e.HasKey(x => new { x.UserId, x.BranchId });
            e.HasOne(x => x.User).WithMany(u => u.Branches).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.DeviceName).HasMaxLength(100);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MachineGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.HasIndex(x => new { x.BranchId, x.Name }).IsUnique();
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Machine>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.HasIndex(x => new { x.BranchId, x.Name }).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.HostName).HasMaxLength(100);
            e.Property(x => x.OsVersion).HasMaxLength(100);
            e.Property(x => x.AppVersion).HasMaxLength(32);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PairingCode>(e =>
        {
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.Property(x => x.MachineName).HasMaxLength(50);
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.Property(x => x.FreeReason).HasMaxLength(500);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Machine).WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BranchId, x.StartedAtUtc });
            e.HasIndex(x => new { x.Status, x.EndsAtUtc });

            // A machine can have at most one session that has not ended. Enforced by the database, not just by code.
            e.HasIndex(x => x.MachineId)
                .IsUnique()
                .HasFilter($"\"Status\" <> {(int)SessionStatus.Ended}")
                .HasDatabaseName("UX_Sessions_OneOpenPerMachine");
        });

        modelBuilder.Entity<SessionEvent>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasOne(x => x.Session).WithMany(s => s.Events).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BranchId, x.AtUtc });
            e.HasIndex(x => x.ShiftId);
            e.HasIndex(x => x.SessionId);
        });

        modelBuilder.Entity<Shift>(e =>
        {
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BranchId, x.OpenedAtUtc });

            // One open shift per employee.
            e.HasIndex(x => x.UserId)
                .IsUnique()
                .HasFilter("\"ClosedAtUtc\" IS NULL")
                .HasDatabaseName("UX_Shifts_OneOpenPerUser");
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Username).HasMaxLength(32);
            e.Property(x => x.MachineName).HasMaxLength(50);
            e.Property(x => x.OldValue).HasMaxLength(2000);
            e.Property(x => x.NewValue).HasMaxLength(2000);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => x.AtUtc);
            e.HasIndex(x => new { x.BranchId, x.AtUtc });
            e.HasIndex(x => x.Action);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnlyTables();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAppendOnlyTables();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>The audit log and the session ledger are append-only. Refuse anything but inserts.</summary>
    private void GuardAppendOnlyTables()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLogEntry or SessionEvent
                && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException($"{entry.Entity.GetType().Name} rows are append-only and cannot be modified or deleted.");
            }
        }
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
