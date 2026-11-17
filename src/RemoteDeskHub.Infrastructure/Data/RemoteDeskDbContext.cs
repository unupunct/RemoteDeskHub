using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Infrastructure.Data;

public sealed class RemoteDeskDbContext(DbContextOptions<RemoteDeskDbContext> options) : DbContext(options)
{
    public DbSet<Computer> Computers => Set<Computer>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentEnrollment> AgentEnrollments => Set<AgentEnrollment>();
    public DbSet<ComputerGroup> ComputerGroups => Set<ComputerGroup>();
    public DbSet<ComputerGroupMembership> ComputerGroupMemberships => Set<ComputerGroupMembership>();
    public DbSet<HardwareInventory> HardwareInventories => Set<HardwareInventory>();
    public DbSet<HardwareInventorySnapshot> HardwareInventorySnapshots => Set<HardwareInventorySnapshot>();
    public DbSet<SoftwareInventoryItem> SoftwareInventory => Set<SoftwareInventoryItem>();
    public DbSet<PerformanceMetric> PerformanceMetrics => Set<PerformanceMetric>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<CommandHistory> CommandHistory => Set<CommandHistory>();
    public DbSet<CommandApproval> CommandApprovals => Set<CommandApproval>();
    public DbSet<FileTransfer> FileTransfers => Set<FileTransfer>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<AutomationTask> AutomationTasks => Set<AutomationTask>();
    public DbSet<AutomationExecution> AutomationExecutions => Set<AutomationExecution>();
    public DbSet<SharedFolderFile> SharedFolderFiles => Set<SharedFolderFile>();
    public DbSet<SharedFolderAgentFile> SharedFolderAgentFiles => Set<SharedFolderAgentFile>();
    public DbSet<SharedFolderLocalFile> SharedFolderLocalFiles => Set<SharedFolderLocalFile>();
    public DbSet<SharedFolderSyncState> SharedFolderSyncState => Set<SharedFolderSyncState>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<DiscoveryDevice> DiscoveryDevices => Set<DiscoveryDevice>();
    public DbSet<RemoteAssistanceProfile> RemoteAssistanceProfiles => Set<RemoteAssistanceProfile>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Computer>(e =>
        {
            e.ToTable("Computers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.FriendlyName).HasMaxLength(128);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.MacAddress).HasMaxLength(32);
            e.Property(x => x.OperatingSystem).HasMaxLength(256);
            e.Property(x => x.OsVersion).HasMaxLength(128);
            e.Property(x => x.MachineId).HasMaxLength(128);
            e.Property(x => x.Architecture).HasMaxLength(64);
            e.Property(x => x.Manufacturer).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.AgentVersion).HasMaxLength(32);
            e.Property(x => x.Source).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.TeamViewerId).HasMaxLength(128);
            e.Property(x => x.AnyDeskId).HasMaxLength(128);
            e.Property(x => x.LmViewerId).HasMaxLength(128);
            e.Property(x => x.RustDeskId).HasMaxLength(128);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.IpAddress);
            e.HasIndex(x => x.MachineId);
            e.HasIndex(x => x.LastSeenUtc);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.GroupId);
            e.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Agent>(e =>
        {
            e.ToTable("Agents");
            e.HasKey(x => x.Id);
            e.Property(x => x.MachineName).HasMaxLength(128).IsRequired();
            e.Property(x => x.MachineId).HasMaxLength(128);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            e.Property(x => x.Version).HasMaxLength(32);
            e.Property(x => x.OperatingSystem).HasMaxLength(256);
            e.Property(x => x.LastKnownIp).HasMaxLength(64);
            e.HasIndex(x => x.CertificateThumbprint).IsUnique();
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.MachineId);
            e.HasIndex(x => x.ComputerId).IsUnique();
            e.HasOne(x => x.Computer)
                .WithOne(x => x.Agent)
                .HasForeignKey<Agent>(x => x.ComputerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentEnrollment>(e =>
        {
            e.ToTable("AgentEnrollments");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.TokenPreview).HasMaxLength(32);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
            e.Property(x => x.ComputerName).HasMaxLength(128);
            e.Property(x => x.RequestedIp).HasMaxLength(64);
            e.Property(x => x.MachineId).HasMaxLength(128);
            e.Property(x => x.PublicKeyPem).HasMaxLength(4000);
            e.Property(x => x.CsrPem).HasMaxLength(8000);
            e.Property(x => x.CertificatePem).HasMaxLength(8000);
            e.Property(x => x.Nonce).HasMaxLength(128);
            e.Property(x => x.Note).HasMaxLength(512);
            e.Property(x => x.RejectReason).HasMaxLength(2000);
            e.Property(x => x.ApprovedBy).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ComputerGroup>(e =>
        {
            e.ToTable("ComputerGroups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ComputerGroupMembership>(e =>
        {
            e.ToTable("ComputerGroupMemberships");
            e.HasKey(x => new { x.ComputerId, x.GroupId });
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Group).WithMany(g => g.Memberships).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HardwareInventory>(e =>
        {
            e.ToTable("HardwareInventory");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ComputerId).IsUnique();
            e.Property(x => x.CpuModel).HasMaxLength(256);
            e.Property(x => x.CpuManufacturer).HasMaxLength(128);
            e.Property(x => x.MotherboardManufacturer).HasMaxLength(128);
            e.Property(x => x.MotherboardModel).HasMaxLength(128);
            e.Property(x => x.BiosVersion).HasMaxLength(128);
            e.Property(x => x.BiosManufacturer).HasMaxLength(128);
            e.HasOne(x => x.Computer).WithOne(c => c.Hardware).HasForeignKey<HardwareInventory>(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HardwareInventorySnapshot>(e =>
        {
            e.ToTable("HardwareInventorySnapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.SnapshotJson).HasMaxLength(32000).IsRequired();
            e.Property(x => x.ChangeSummary).HasMaxLength(8000);
            e.Property(x => x.Source).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.ComputerId, x.CollectedUtc });
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SoftwareInventoryItem>(e =>
        {
            e.ToTable("SoftwareInventory");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(512).IsRequired();
            e.Property(x => x.Publisher).HasMaxLength(512);
            e.Property(x => x.Version).HasMaxLength(128);
            e.Property(x => x.InstallDate).HasMaxLength(32);
            e.Property(x => x.InstallLocation).HasMaxLength(1024);
            e.Property(x => x.Architecture).HasMaxLength(32);
            e.Property(x => x.RegistryKey).HasMaxLength(1024);
            e.HasIndex(x => new { x.ComputerId, x.Name });
            e.HasIndex(x => new { x.ComputerId, x.IsPresent });
            e.HasIndex(x => x.Publisher);
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PerformanceMetric>(e =>
        {
            e.ToTable("PerformanceMetrics");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ComputerId, x.TimestampUtc });
            e.HasIndex(x => x.TimestampUtc);
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Alert>(e =>
        {
            e.ToTable("Alerts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2000).IsRequired();
            e.Property(x => x.ComputerName).HasMaxLength(128);
            e.Property(x => x.DedupeKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.MetricType).HasConversion<string>().HasMaxLength(64);
            e.Property(x => x.Details).HasMaxLength(4000);
            e.HasIndex(x => new { x.IsActive, x.CreatedUtc });
            e.HasIndex(x => x.DedupeKey);
            e.HasIndex(x => x.ComputerId);
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AlertRule>(e =>
        {
            e.ToTable("AlertRules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.MetricType).HasConversion<string>().HasMaxLength(64);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.Enabled);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(128).IsRequired();
            e.Property(x => x.Operation).HasMaxLength(128).IsRequired();
            e.Property(x => x.ComputerName).HasMaxLength(128);
            e.Property(x => x.Result).HasMaxLength(32).IsRequired();
            e.Property(x => x.Error).HasMaxLength(4000);
            e.Property(x => x.Details).HasMaxLength(8000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.PrevHash).HasMaxLength(128);
            e.Property(x => x.Hash).HasMaxLength(128);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.Operation);
        });

        modelBuilder.Entity<CommandHistory>(e =>
        {
            e.ToTable("CommandHistory");
            e.HasKey(x => x.Id);
            e.Property(x => x.Command).HasMaxLength(8000).IsRequired();
            e.Property(x => x.ComputerName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(128).IsRequired();
            e.Property(x => x.Shell).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Error).HasMaxLength(4000);
            e.HasIndex(x => x.StartedUtc);
            e.HasIndex(x => x.ComputerId);
        });

        modelBuilder.Entity<CommandApproval>(e =>
        {
            e.ToTable("CommandApprovals");
            e.HasKey(x => x.Id);
            e.Property(x => x.ComputerName).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestedBy).HasMaxLength(128).IsRequired();
            e.Property(x => x.Command).HasMaxLength(8000).IsRequired();
            e.Property(x => x.Shell).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ReviewedBy).HasMaxLength(128);
            e.Property(x => x.ReviewNote).HasMaxLength(2000);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.RequestedUtc);
            e.HasIndex(x => x.ExpiresUtc);
        });

        modelBuilder.Entity<FileTransfer>(e =>
        {
            e.ToTable("FileTransfers");
            e.HasKey(x => x.Id);
            e.Property(x => x.ComputerName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(128).IsRequired();
            e.Property(x => x.LocalPath).HasMaxLength(2048).IsRequired();
            e.Property(x => x.RemotePath).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(128);
            e.Property(x => x.Error).HasMaxLength(4000);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.StartedUtc);
        });

        modelBuilder.Entity<RemoteSession>(e =>
        {
            e.ToTable("RemoteSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ComputerName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(128).IsRequired();
            e.Property(x => x.Tool).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ConnectionInfo).HasMaxLength(2048);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.ProcessId);
            e.HasIndex(x => x.StartedUtc);
        });

        modelBuilder.Entity<AutomationTask>(e =>
        {
            e.ToTable("AutomationTasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.TriggerConfigJson).HasMaxLength(4000);
            e.Property(x => x.ActionConfigJson).HasMaxLength(8000);
            e.Property(x => x.DailyTime).HasMaxLength(16);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
            e.Property(x => x.LastResult).HasMaxLength(2000);
            e.Property(x => x.TriggerType).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ActionType).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.Enabled);
            e.HasIndex(x => x.NextRunUtc);
        });

        modelBuilder.Entity<AutomationExecution>(e =>
        {
            e.ToTable("AutomationExecutions");
            e.HasKey(x => x.Id);
            e.Property(x => x.TriggeredBy).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Log).HasMaxLength(16000);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.StartedUtc);
        });

        modelBuilder.Entity<SharedFolderFile>(e =>
        {
            e.ToTable("SharedFolderFiles");
            e.HasKey(x => x.Path);
            e.Property(x => x.Path).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceComputerName).HasMaxLength(128);
            e.HasIndex(x => x.IsDeleted);
            e.HasIndex(x => x.UpdatedUtc);
        });

        modelBuilder.Entity<SharedFolderAgentFile>(e =>
        {
            e.ToTable("SharedFolderAgentFiles");
            e.HasKey(x => new { x.ComputerId, x.Path });
            e.Property(x => x.Path).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.CentralSha256).HasMaxLength(64);
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ObservedUtc);
        });

        modelBuilder.Entity<SharedFolderLocalFile>(e =>
        {
            e.ToTable("SharedFolderLocalFiles");
            e.HasKey(x => x.Path);
            e.Property(x => x.Path).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.CentralSha256).HasMaxLength(64);
            e.HasIndex(x => x.ObservedUtc);
        });

        modelBuilder.Entity<SharedFolderSyncState>(e =>
        {
            e.ToTable("SharedFolderSyncState");
            e.HasKey(x => x.Id);
            e.Property(x => x.LastError).HasMaxLength(4000);
        });

        modelBuilder.Entity<ApplicationSetting>(e =>
        {
            e.ToTable("ApplicationSettings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Category).HasMaxLength(64);
        });

        modelBuilder.Entity<UserAccount>(e =>
        {
            e.ToTable("UserAccounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<DiscoveryDevice>(e =>
        {
            e.ToTable("DiscoveryDevices");
            e.HasKey(x => x.Id);
            e.Property(x => x.IpAddress).HasMaxLength(64).IsRequired();
            e.Property(x => x.MacAddress).HasMaxLength(32);
            e.Property(x => x.Hostname).HasMaxLength(256);
            e.Property(x => x.Vendor).HasMaxLength(256);
            e.Property(x => x.OperatingSystem).HasMaxLength(256);
            e.Property(x => x.OpenServicesCsv).HasMaxLength(256);
            e.Property(x => x.FriendlyName).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => x.IpAddress).IsUnique();
            e.HasIndex(x => x.LastSeenUtc);
        });

        modelBuilder.Entity<RemoteAssistanceProfile>(e =>
        {
            e.ToTable("RemoteAssistanceProfiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Tool).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Arguments).HasMaxLength(2000);
            e.Property(x => x.Instructions).HasMaxLength(4000);
        });

        modelBuilder.Entity<SchemaVersion>(e =>
        {
            e.ToTable("SchemaVersions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(32).IsRequired();
        });

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetColumnType("TEXT");
                }
            }
        }
    }
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RemoteDeskDbContext>
{
    public RemoteDeskDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), HubConstants.DataFolderName);
        Directory.CreateDirectory(path);
        var options = new DbContextOptionsBuilder<RemoteDeskDbContext>()
            .UseSqlite($"Data Source={Path.Combine(path, HubConstants.DatabaseFileName)};Default Timeout=30")
            .Options;
        return new RemoteDeskDbContext(options);
    }
}
