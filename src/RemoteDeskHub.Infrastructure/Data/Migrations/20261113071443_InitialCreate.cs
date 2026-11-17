using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteDeskHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentEnrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TokenPreview = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RequestedIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PublicKeyPem = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CsrPem = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    CertificatePem = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Nonce = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ManagementPort = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoApprove = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ApprovedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEnrollments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MetricType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Threshold = table.Column<double>(type: "REAL", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScopeComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ScopeGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CooldownMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Result = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PrevHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TriggerType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TriggerConfigJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ActionType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ActionConfigJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    TargetComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    DailyTime = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Shell = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Command = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    StdOut = table.Column<string>(type: "TEXT", nullable: true),
                    StdErr = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    RequireElevation = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBulk = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComputerGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ResponseTimeMs = table.Column<double>(type: "REAL", nullable: true),
                    OpenServicesCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    HasAgent = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRegistered = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsTrusted = table.Column<bool>(type: "INTEGER", nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScanUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    RemotePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TransferredBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SpeedBps = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileTransfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteAssistanceProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Arguments = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Instructions = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAssistanceProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConnectionInfo = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchemaVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AppliedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordSalt = table.Column<string>(type: "TEXT", nullable: false),
                    Iterations = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailedLoginCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Log = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    Succeeded = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggeredBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationExecutions_AutomationTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AutomationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Computers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Architecture = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Manufacturer = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsRegistered = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsTrusted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsManaged = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastBootUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                    MemoryPercent = table.Column<double>(type: "REAL", nullable: false),
                    DiskPercent = table.Column<double>(type: "REAL", nullable: false),
                    CpuTemperatureC = table.Column<double>(type: "REAL", nullable: true),
                    MemoryTotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UptimeSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalDiskBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    FreeDiskBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Computers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Computers_ComputerGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ComputerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CertificatePem = table.Column<string>(type: "TEXT", nullable: true),
                    PublicKeyPem = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastKnownIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ListeningPort = table.Column<int>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EnrolledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevocationReason = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    HealthStatus = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MetricType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcknowledgedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ComputerGroupMemberships",
                columns: table => new
                {
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerGroupMemberships", x => new { x.ComputerId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_ComputerGroupMemberships_ComputerGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ComputerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ComputerGroupMemberships_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HardwareInventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CpuModel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CpuManufacturer = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CpuCores = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuLogicalProcessors = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuMaxClockMhz = table.Column<int>(type: "INTEGER", nullable: true),
                    RamTotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    RamAvailableBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MotherboardManufacturer = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MotherboardModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BiosVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BiosManufacturer = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    GpusJson = table.Column<string>(type: "TEXT", nullable: true),
                    MemoryModulesJson = table.Column<string>(type: "TEXT", nullable: true),
                    DisksJson = table.Column<string>(type: "TEXT", nullable: true),
                    AdaptersJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    CollectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareInventory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HardwareInventory_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PerformanceMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                    MemoryPercent = table.Column<double>(type: "REAL", nullable: false),
                    DiskPercent = table.Column<double>(type: "REAL", nullable: false),
                    NetworkInBps = table.Column<double>(type: "REAL", nullable: false),
                    NetworkOutBps = table.Column<double>(type: "REAL", nullable: false),
                    CpuTemperatureC = table.Column<double>(type: "REAL", nullable: true),
                    UptimeSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    ProcessCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformanceMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PerformanceMetrics_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareInventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Publisher = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InstallDate = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    InstallLocation = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    EstimatedSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Architecture = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RegistryKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    IsPresent = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScanUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInventory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftwareInventory_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_Status",
                table: "AgentEnrollments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_TokenHash",
                table: "AgentEnrollments",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_CertificateThumbprint",
                table: "Agents",
                column: "CertificateThumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_ComputerId",
                table: "Agents",
                column: "ComputerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_State",
                table: "Agents",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_Enabled",
                table: "AlertRules",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ComputerId",
                table: "Alerts",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_DedupeKey",
                table: "Alerts",
                column: "DedupeKey");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_IsActive_CreatedUtc",
                table: "Alerts",
                columns: new[] { "IsActive", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Operation",
                table: "AuditLogs",
                column: "Operation");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_StartedUtc",
                table: "AutomationExecutions",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_TaskId",
                table: "AutomationExecutions",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTasks_Enabled",
                table: "AutomationTasks",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTasks_NextRunUtc",
                table: "AutomationTasks",
                column: "NextRunUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandHistory_ComputerId",
                table: "CommandHistory",
                column: "ComputerId");

            migrationBuilder.CreateIndex(
                name: "IX_CommandHistory_StartedUtc",
                table: "CommandHistory",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ComputerGroupMemberships_GroupId",
                table: "ComputerGroupMemberships",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputerGroups_Name",
                table: "ComputerGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Computers_GroupId",
                table: "Computers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_IpAddress",
                table: "Computers",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_LastSeenUtc",
                table: "Computers",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_Name",
                table: "Computers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Computers_Status",
                table: "Computers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryDevices_IpAddress",
                table: "DiscoveryDevices",
                column: "IpAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryDevices_LastSeenUtc",
                table: "DiscoveryDevices",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_StartedUtc",
                table: "FileTransfers",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HardwareInventory_ComputerId",
                table: "HardwareInventory",
                column: "ComputerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceMetrics_ComputerId_TimestampUtc",
                table: "PerformanceMetrics",
                columns: new[] { "ComputerId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceMetrics_TimestampUtc",
                table: "PerformanceMetrics",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_StartedUtc",
                table: "RemoteSessions",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventory_ComputerId_IsPresent",
                table: "SoftwareInventory",
                columns: new[] { "ComputerId", "IsPresent" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventory_ComputerId_Name",
                table: "SoftwareInventory",
                columns: new[] { "ComputerId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventory_Publisher",
                table: "SoftwareInventory",
                column: "Publisher");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_Username",
                table: "UserAccounts",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEnrollments");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "ApplicationSettings");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "AutomationExecutions");

            migrationBuilder.DropTable(
                name: "CommandHistory");

            migrationBuilder.DropTable(
                name: "ComputerGroupMemberships");

            migrationBuilder.DropTable(
                name: "DiscoveryDevices");

            migrationBuilder.DropTable(
                name: "FileTransfers");

            migrationBuilder.DropTable(
                name: "HardwareInventory");

            migrationBuilder.DropTable(
                name: "PerformanceMetrics");

            migrationBuilder.DropTable(
                name: "RemoteAssistanceProfiles");

            migrationBuilder.DropTable(
                name: "RemoteSessions");

            migrationBuilder.DropTable(
                name: "SchemaVersions");

            migrationBuilder.DropTable(
                name: "SoftwareInventory");

            migrationBuilder.DropTable(
                name: "UserAccounts");

            migrationBuilder.DropTable(
                name: "AutomationTasks");

            migrationBuilder.DropTable(
                name: "Computers");

            migrationBuilder.DropTable(
                name: "ComputerGroups");
        }
    }
}
