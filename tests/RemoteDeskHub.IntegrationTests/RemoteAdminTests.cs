using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.IntegrationTests;

public sealed class RemoteAdminTests
{
    private static async Task<TestHub> CreateHubWithTransportAsync(FakeAgentTransport transport) =>
        await TestHub.CreateAsync(services =>
        {
            services.AddSingleton<IAgentTransport>(transport);
        });

    private static async Task<SessionUser> CreateSecondAdministratorAsync(TestHub hub, string userName = "admin2")
    {
        var auth = hub.Get<IAuthService>();
        var created = await auth.CreateUserAsync(
            TestHub.AdminUserName, userName, "RemoteDesk-Admin2", UserRole.Administrator);
        Assert.True(created.Success, created.Error);
        var login = await auth.LoginAsync(userName, "RemoteDesk-Admin2", "127.0.0.1");
        Assert.True(login.Success, login.Error);
        return login.Value!;
    }

    [Fact]
    public async Task Command_ExecutesAndRecordsHistory()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        var result = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "whoami", CommandShell.PowerShell, 30, false, confirmed: true, isBulk: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("whoami", result.StdOut);
        Assert.Single(transport.Commands);

        var history = await hub.Get<ICommandService>().GetHistoryAsync(computer.Id);
        Assert.Single(history);
        Assert.Equal(CommandStatus.Succeeded, history[0].Status);
        Assert.Equal("whoami", history[0].Command);
    }

    [Fact]
    public async Task DestructiveCommand_RequiresExplicitConfirmation()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        var blocked = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "Remove-Item C:\\Temp\\data.txt -Recurse", CommandShell.PowerShell, 30, false, confirmed: false, isBulk: false);

        Assert.Empty(transport.Commands);
        Assert.NotEqual(0, blocked.ExitCode);
        Assert.Contains("not explicitly confirmed", blocked.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CriticalCommand_IsBlockedAndAuditedByDefault()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        var result = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "format C: /q", CommandShell.Cmd, 30, false, confirmed: true, isBulk: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("allowlist", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Commands);

        var audit = await hub.Get<IAuditService>().QueryAsync(null, null, AuditOperations.CommandBlocked, 10);
        Assert.Single(audit);
        Assert.Equal("Blocked", audit[0].Result);
    }

    [Fact]
    public async Task CriticalCommand_RunsWhenExactCommandIsAllowlisted()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        await hub.Get<ISettingsService>().SetUserAsync(
            TestHub.AdminUserName, SettingsKeys.CommandAllowlist, "format C: /q", "Security");

        var result = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "format C: /q", CommandShell.Cmd, 30, false, confirmed: true, isBulk: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(transport.Commands);
    }

    [Fact]
    public async Task DestructiveCommand_RequiresSecondAdministratorApproval()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var auth = hub.Get<IAuthService>();
        var created = await auth.CreateUserAsync(TestHub.AdminUserName, "operator1", "RemoteDesk-Operator1", UserRole.Operator);
        Assert.True(created.Success, created.Error);
        var operatorLogin = await auth.LoginAsync("operator1", "RemoteDesk-Operator1", "127.0.0.1");
        Assert.True(operatorLogin.Success, operatorLogin.Error);
        var adminLogin = await auth.LoginAsync(TestHub.AdminUserName, TestHub.AdminPassword, "127.0.0.1");
        Assert.True(adminLogin.Success, adminLogin.Error);

        await hub.Get<ISettingsService>().SetUserAsync(
            TestHub.AdminUserName, SettingsKeys.CommandApprovalRequired, "true", "Security");

        hub.Session.SignIn(operatorLogin.Value!);
        var request = await hub.Get<ICommandService>().ExecuteAsync(
            "operator1", computer, "Remove-Item C:\\Temp\\load-test.txt -Force", CommandShell.PowerShell,
            30, false, confirmed: true, isBulk: false);

        Assert.NotNull(request.ApprovalId);
        Assert.Empty(transport.Commands);
        Assert.Contains("requires approval", request.Error, StringComparison.OrdinalIgnoreCase);

        hub.Session.SignIn(adminLogin.Value!);
        var approval = await hub.Get<ICommandService>().ApproveCommandAsync(
            TestHub.AdminUserName, request.ApprovalId!.Value);
        Assert.True(approval.Success, approval.Error);

        hub.Session.SignIn(operatorLogin.Value!);
        var executed = await hub.Get<ICommandService>().ExecuteApprovedAsync(
            "operator1", request.ApprovalId!.Value);

        Assert.Equal(0, executed.ExitCode);
        Assert.Single(transport.Commands);
        var replay = await hub.Get<ICommandService>().ExecuteApprovedAsync(
            "operator1", request.ApprovalId!.Value);
        Assert.NotEqual(0, replay.ExitCode);
        Assert.Single(transport.Commands);
        var audit = await hub.Get<IAuditService>().QueryAsync(null, null, AuditOperations.CommandApprovalExecuted, 10);
        Assert.Single(audit);
    }

    [Fact]
    public async Task Requester_CannotApproveOwnCommand()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        await hub.Get<ISettingsService>().SetUserAsync(
            TestHub.AdminUserName, SettingsKeys.CommandApprovalRequired, "true", "Security");

        var request = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "Remove-Item C:\\Temp\\load-test.txt -Force", CommandShell.PowerShell,
            30, false, confirmed: true, isBulk: false);

        Assert.NotNull(request.ApprovalId);
        var approval = await hub.Get<ICommandService>().ApproveCommandAsync(
            TestHub.AdminUserName, request.ApprovalId!.Value);

        Assert.False(approval.Success);
        Assert.Contains("different administrator", approval.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task ApprovedCommand_WithDifferentTimeout_IsRejected()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        const string command = "Remove-Item C:\\Temp\\load-test.txt -Force";
        await hub.Get<ISettingsService>().SetUserAsync(
            TestHub.AdminUserName, SettingsKeys.CommandApprovalRequired, "true", "Security");

        var request = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, command, CommandShell.PowerShell, 30, false, true, false);
        Assert.NotNull(request.ApprovalId);

        hub.Session.SignIn(await CreateSecondAdministratorAsync(hub));
        var approval = await hub.Get<ICommandService>().ApproveCommandAsync(
            "admin2", request.ApprovalId!.Value);
        Assert.True(approval.Success, approval.Error);

        var adminLogin = await hub.Get<IAuthService>().LoginAsync(
            TestHub.AdminUserName, TestHub.AdminPassword, "127.0.0.1");
        hub.Session.SignIn(adminLogin.Value!);
        var mismatch = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, command, CommandShell.PowerShell, 31,
            requireElevation: false, confirmed: true, isBulk: false, approvalId: request.ApprovalId);

        Assert.NotEqual(0, mismatch.ExitCode);
        Assert.Contains("does not match", mismatch.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task ApprovedCommand_AfterFifteenMinutes_IsRejected()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        await hub.Get<ISettingsService>().SetUserAsync(
            TestHub.AdminUserName, SettingsKeys.CommandApprovalRequired, "true", "Security");

        var request = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "Remove-Item C:\\Temp\\load-test.txt -Force",
            CommandShell.PowerShell, 30, false, true, false);
        Assert.NotNull(request.ApprovalId);

        hub.Session.SignIn(await CreateSecondAdministratorAsync(hub));
        var approval = await hub.Get<ICommandService>().ApproveCommandAsync(
            "admin2", request.ApprovalId!.Value);
        Assert.True(approval.Success, approval.Error);

        hub.Clock.Advance(TimeSpan.FromMinutes(16));
        var adminLogin = await hub.Get<IAuthService>().LoginAsync(
            TestHub.AdminUserName, TestHub.AdminPassword, "127.0.0.1");
        hub.Session.SignIn(adminLogin.Value!);
        var expired = await hub.Get<ICommandService>().ExecuteApprovedAsync(
            TestHub.AdminUserName, request.ApprovalId.Value);

        Assert.NotEqual(0, expired.ExitCode);
        Assert.Contains("expired", expired.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task RejectedCommand_CannotBeExecuted()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        await hub.Get<ISettingsService>().SetUserAsync(
            TestHub.AdminUserName, SettingsKeys.CommandApprovalRequired, "true", "Security");

        var request = await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "Remove-Item C:\\Temp\\load-test.txt -Force",
            CommandShell.PowerShell, 30, false, true, false);
        Assert.NotNull(request.ApprovalId);

        hub.Session.SignIn(await CreateSecondAdministratorAsync(hub));
        var rejected = await hub.Get<ICommandService>().RejectCommandAsync(
            "admin2", request.ApprovalId!.Value, "Not needed.");
        Assert.True(rejected.Success, rejected.Error);
        var audit = await hub.Get<IAuditService>().QueryAsync(null, null, AuditOperations.CommandApprovalRejected, 10);
        Assert.Single(audit);

        var adminLogin = await hub.Get<IAuthService>().LoginAsync(
            TestHub.AdminUserName, TestHub.AdminPassword, "127.0.0.1");
        hub.Session.SignIn(adminLogin.Value!);
        var executed = await hub.Get<ICommandService>().ExecuteApprovedAsync(
            TestHub.AdminUserName, request.ApprovalId.Value);

        Assert.NotEqual(0, executed.ExitCode);
        Assert.Contains("Rejected", executed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task Command_AuditEntryContainsTranscriptHashes()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "hostname", CommandShell.Cmd, 30, false, true, false);

        var audit = await hub.Get<IAuditService>().QueryAsync(null, null, AuditOperations.CommandExecuted, 10);
        Assert.Single(audit);
        Assert.Contains("Transcript SHA-256", audit[0].Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stdout", audit[0].Details, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Command_RejectsEmptyAndOverlongInput()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var commands = hub.Get<ICommandService>();

        var empty = await commands.ExecuteAsync(TestHub.AdminUserName, computer, "   ", CommandShell.Cmd, 30, false, true, false);
        var overlong = await commands.ExecuteAsync(TestHub.AdminUserName, computer, new string('a', 8001), CommandShell.Cmd, 30, false, true, false);

        Assert.Contains("Enter a command", empty.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("too long", overlong.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task Command_AuditEntryIsWritten()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        await hub.Get<ICommandService>().ExecuteAsync(
            TestHub.AdminUserName, computer, "hostname", CommandShell.Cmd, 30, false, true, false);

        var audit = await hub.Get<IAuditService>().QueryAsync(null, null, AuditOperations.CommandExecuted, 10);
        Assert.Single(audit);
        Assert.Equal("TEST-PC-01", audit[0].ComputerName);
    }

    [Fact]
    public async Task Viewer_CannotRunCommands()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var viewerSession = new SessionContext();
        viewerSession.SignIn(new SessionUser(Guid.NewGuid(), "viewer", UserRole.Viewer, DateTime.UtcNow));

        var commandService = new RemoteDeskHub.Infrastructure.Services.CommandService(
            hub.Get<IDbContextFactory<RemoteDeskDbContext>>(), transport, viewerSession,
            hub.Get<IAuditService>(), hub.Get<ISettingsService>(), hub.Clock);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commandService.ExecuteAsync("viewer", computer, "whoami", CommandShell.Cmd, 30, false, true, false));
    }

    [Fact]
    public async Task ProtectProcesses_CannotBeTerminated()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        var outcome = await hub.Get<IProcessService>()
            .TerminateAsync(TestHub.AdminUserName, computer.Id, 4, "lsass", confirmed: true);

        Assert.False(outcome.Success);
        Assert.Contains("protected", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.TerminateCalls);
    }

    [Fact]
    public async Task NormalProcess_RequiresConfirmation()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var processes = hub.Get<IProcessService>();

        var unconfirmed = await processes.TerminateAsync(TestHub.AdminUserName, computer.Id, 1234, "notepad", confirmed: false);
        var confirmed = await processes.TerminateAsync(TestHub.AdminUserName, computer.Id, 1234, "notepad", confirmed: true);

        Assert.False(unconfirmed.Success);
        Assert.True(confirmed.Success, confirmed.Message);
        Assert.Single(transport.TerminateCalls);
    }

    [Fact]
    public async Task CriticalService_CannotBeStoppedEvenWhenConfirmed()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        var result = await hub.Get<IServiceManagerService>()
            .ExecuteActionAsync(TestHub.AdminUserName, computer.Id, "RpcSs", "stop", null, confirmed: true);

        Assert.False(result.Success);
        Assert.Contains("critical", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.ServiceCalls);
    }

    [Fact]
    public async Task OrdinaryService_StopRequiresConfirmation()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var services = hub.Get<IServiceManagerService>();

        var unconfirmed = await services.ExecuteActionAsync(TestHub.AdminUserName, computer.Id, "Spooler", "stop", null, confirmed: false);
        var confirmed = await services.ExecuteActionAsync(TestHub.AdminUserName, computer.Id, "Spooler", "stop", null, confirmed: true);

        Assert.False(unconfirmed.Success);
        Assert.True(confirmed.Success, confirmed.Error);
        Assert.Single(transport.ServiceCalls);
        Assert.Equal(("Spooler", "stop"), transport.ServiceCalls[0]);
    }

    [Fact]
    public async Task ServiceAction_RejectsUnknownActionAndStartupType()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var services = hub.Get<IServiceManagerService>();

        var badAction = await services.ExecuteActionAsync(TestHub.AdminUserName, computer.Id, "Spooler", "explode", null, true);
        var badStartup = await services.ExecuteActionAsync(TestHub.AdminUserName, computer.Id, "Spooler", "startup", "Whenever", true);

        Assert.False(badAction.Success);
        Assert.False(badStartup.Success);
        Assert.Empty(transport.ServiceCalls);
    }

    [Fact]
    public async Task Upload_TransfersBytesAndVerifiesHash()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var payload = new byte[2_500_000];
        Random.Shared.NextBytes(payload);
        var local = Path.Combine(hub.Paths.TransfersDirectory, "upload.bin");
        await File.WriteAllBytesAsync(local, payload);

        var result = await hub.Get<IFileTransferService>()
            .UploadAsync(TestHub.AdminUserName, computer.Id, local, @"C:\Uploads\upload.bin", null);

        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.True(transport.Files.TryGetValue(@"C:\Uploads\upload.bin", out var remote));
        Assert.Equal(payload, remote);
        Assert.Equal(FakeAgentTransport.Sha256Hex(payload), result.Sha256);
        Assert.True(transport.Writes.Count >= 3);
    }

    [Fact]
    public async Task Download_TransfersBytesAndVerifiesHash()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var payload = new byte[1_500_000];
        Random.Shared.NextBytes(payload);
        transport.Files[@"C:\Remote\download.bin"] = payload;
        var local = Path.Combine(hub.Paths.TransfersDirectory, "downloaded.bin");

        var result = await hub.Get<IFileTransferService>()
            .DownloadAsync(TestHub.AdminUserName, computer.Id, @"C:\Remote\download.bin", local, null);

        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(local));
        Assert.False(File.Exists(local + ".rdpart"));
        Assert.Equal(FakeAgentTransport.Sha256Hex(payload), result.Sha256);
    }

    [Fact]
    public async Task Delete_RequiresConfirmation()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        transport.Files[@"C:\Remote\keep.txt"] = [1, 2, 3];
        var files = hub.Get<IFileTransferService>();

        var unconfirmed = await files.DeleteRemoteAsync(TestHub.AdminUserName, computer.Id, @"C:\Remote\keep.txt", false, confirmed: false);
        Assert.False(unconfirmed.Success);
        Assert.True(transport.Files.ContainsKey(@"C:\Remote\keep.txt"));

        var confirmed = await files.DeleteRemoteAsync(TestHub.AdminUserName, computer.Id, @"C:\Remote\keep.txt", false, confirmed: true);
        Assert.True(confirmed.Success, confirmed.Error);
        Assert.False(transport.Files.ContainsKey(@"C:\Remote\keep.txt"));
    }

    [Fact]
    public async Task Upload_RejectsMissingLocalFile()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Get<IFileTransferService>()
            .UploadAsync(TestHub.AdminUserName, computer.Id, @"C:\definitely-missing\file.bin", @"C:\Remote\file.bin", null));
    }

    [Fact]
    public async Task TransferHistory_IsRecorded()
    {
        var transport = new FakeAgentTransport();
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var local = Path.Combine(hub.Paths.TransfersDirectory, "history.bin");
        await File.WriteAllBytesAsync(local, [9, 8, 7]);

        await hub.Get<IFileTransferService>()
            .UploadAsync(TestHub.AdminUserName, computer.Id, local, @"C:\Remote\history.bin", null);

        var history = await hub.Get<IFileTransferService>().GetHistoryAsync(computer.Id);
        Assert.Single(history);
        Assert.Equal(TransferStatus.Completed, history[0].Status);
        Assert.Equal(TransferDirection.Upload, history[0].Direction);
    }

    [Fact]
    public async Task RemoteReports_AreGeneratedInAllFormats()
    {
        var transport = new FakeAgentTransport
        {
            Processes =
            [
                new RemoteProcessDto { Name = "notepad", Pid = 10, Status = "Running", WorkingSetBytes = 1024 }
            ]
        };
        await using var hub = await CreateHubWithTransportAsync(transport);
        var (computer, _) = await hub.CreateApprovedComputerAsync();
        var reports = hub.Get<IReportService>();

        foreach (var format in new[] { "csv", "html", "pdf" })
        {
            var file = await reports.GenerateNetworkInventoryAsync(format);
            Assert.True(file.Content.Length > 0);
            Assert.EndsWith("." + format, file.FileName, StringComparison.OrdinalIgnoreCase);
        }

        var processReport = await hub.Get<IProcessService>().ExportAsync(computer.Id, "csv");
        Assert.True(processReport.Content.Length > 0);
        Assert.Contains("notepad", System.Text.Encoding.UTF8.GetString(processReport.Content));
    }
}

