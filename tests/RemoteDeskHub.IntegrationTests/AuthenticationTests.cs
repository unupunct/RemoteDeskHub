using Microsoft.EntityFrameworkCore;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Infrastructure.Data;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.IntegrationTests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task CreateInitialAdministrator_Succeeds()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var auth = hub.Get<IAuthService>();

        var result = await auth.CreateInitialAdministratorAsync("admin", "RemoteDesk-Admin1");

        Assert.True(result.Success, result.Error);
        Assert.Equal(UserRole.Administrator, result.Value!.Role);
        Assert.True(await auth.HasAnyUserAsync());
    }

    [Fact]
    public async Task CreateInitialAdministratorTwice_Fails()
    {
        await using var hub = await TestHub.CreateAsync();
        var result = await hub.Get<IAuthService>()
            .CreateInitialAdministratorAsync("second", "RemoteDesk-Admin2");

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateInitialAdministrator_RejectsWeakPasswordAndBadUserName()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var auth = hub.Get<IAuthService>();

        Assert.False((await auth.CreateInitialAdministratorAsync("admin", "weak")).Success);
        Assert.False((await auth.CreateInitialAdministratorAsync("ab", "RemoteDesk-Admin1")).Success);
    }

    [Fact]
    public async Task Viewer_CannotCallAdministrativeServiceMethods()
    {
        await using var hub = await TestHub.CreateAsync();
        hub.Session.SignIn(new SessionUser(Guid.NewGuid(), "viewer1", UserRole.Viewer, DateTime.UtcNow));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hub.Get<IAuthService>().GetUsersAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hub.Get<IAuditService>().QueryAsync(null, null, null, 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            hub.Get<ISettingsService>().SetUserAsync("viewer1", SettingsKeys.HubPort, "7443", "Network"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hub.Get<IAutomationService>().GetTasksAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hub.Get<ICommandService>().GetHistoryAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hub.Get<ICommandService>().GetApprovalsAsync());
    }

    [Fact]
    public async Task Login_SucceedsWithCorrectPassword()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var auth = hub.Get<IAuthService>();
        await auth.CreateInitialAdministratorAsync("admin", "RemoteDesk-Admin1");

        var login = await auth.LoginAsync("admin", "RemoteDesk-Admin1", "127.0.0.1");

        Assert.True(login.Success, login.Error);
        Assert.Equal("admin", login.Value!.UserName);
    }

    [Fact]
    public async Task Login_FailsWithWrongPassword()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var auth = hub.Get<IAuthService>();
        await auth.CreateInitialAdministratorAsync("admin", "RemoteDesk-Admin1");

        var login = await auth.LoginAsync("admin", "wrong-password", "127.0.0.1");

        Assert.False(login.Success);
        Assert.Equal("Invalid user name or password.", login.Error);
    }

    [Fact]
    public async Task Login_UnknownUser_Fails()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);

        var login = await hub.Get<IAuthService>().LoginAsync("ghost", "RemoteDesk-Admin1", null);

        Assert.False(login.Success);
    }

    [Fact]
    public async Task FiveFailedAttempts_LockAccountUntilClockAdvances()
    {
        await using var hub = await TestHub.CreateAsync(createAdministrator: false);
        var auth = hub.Get<IAuthService>();
        await auth.CreateInitialAdministratorAsync("admin", "RemoteDesk-Admin1");

        for (var i = 0; i < 5; i++)
        {
            Assert.False((await auth.LoginAsync("admin", "nope-nope-nope", "127.0.0.1")).Success);
        }

        var locked = await auth.LoginAsync("admin", "RemoteDesk-Admin1", "127.0.0.1");
        Assert.False(locked.Success);
        Assert.Contains("Too many failed attempts", locked.Error);

        hub.Clock.Advance(TimeSpan.FromMinutes(6));

        var recovered = await auth.LoginAsync("admin", "RemoteDesk-Admin1", "127.0.0.1");
        Assert.True(recovered.Success, recovered.Error);
    }

    [Fact]
    public async Task DisabledAccount_CannotSignIn()
    {
        await using var hub = await TestHub.CreateAsync();
        var auth = hub.Get<IAuthService>();

        var created = await auth.CreateUserAsync(TestHub.AdminUserName, "operator1", "Operator-Pass1", UserRole.Operator);
        Assert.True(created.Success, created.Error);

        var user = (await auth.GetUsersAsync()).Single(u => u.Username == "operator1");
        Assert.True((await auth.SetUserEnabledAsync(TestHub.AdminUserName, user.Id, false)).Success);

        var login = await auth.LoginAsync("operator1", "Operator-Pass1", null);
        Assert.False(login.Success);
        Assert.Contains("disabled", login.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LastAdministrator_CannotBeDisabled()
    {
        await using var hub = await TestHub.CreateAsync();
        var auth = hub.Get<IAuthService>();
        var admin = (await auth.GetUsersAsync()).Single(u => u.Username == TestHub.AdminUserName);

        var result = await auth.SetUserEnabledAsync(TestHub.AdminUserName, admin.Id, false);

        Assert.False(result.Success);
        Assert.Contains("last enabled administrator", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Viewer_CannotCreateUsers()
    {
        await using var hub = await TestHub.CreateAsync();
        var auth = hub.Get<IAuthService>();
        await auth.CreateUserAsync(TestHub.AdminUserName, "viewer1", "Viewer-Pass1", UserRole.Viewer);

        // Build a viewer session context for the call under test.
        var viewerSession = new SessionContext();
        var viewer = (await auth.GetUsersAsync()).Single(u => u.Username == "viewer1");
        viewerSession.SignIn(new SessionUser(viewer.Id, viewer.Username, viewer.Role, DateTime.UtcNow));

        var scopedAuth = new RemoteDeskHub.Infrastructure.Services.AuthService(
            hub.Get<IDbContextFactory<RemoteDeskDbContext>>(),
            hub.Get<IAuditService>(),
            viewerSession,
            hub.Clock);

        var result = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => scopedAuth.CreateUserAsync("viewer1", "sneaky", "Sneaky-Pass1", UserRole.Administrator));

        Assert.Contains("not permitted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoleChange_And_PasswordReset_Work()
    {
        await using var hub = await TestHub.CreateAsync();
        var auth = hub.Get<IAuthService>();
        await auth.CreateUserAsync(TestHub.AdminUserName, "ops1", "Operator-Pass1", UserRole.Operator);
        var user = (await auth.GetUsersAsync()).Single(u => u.Username == "ops1");

        Assert.True((await auth.ChangeRoleAsync(TestHub.AdminUserName, user.Id, UserRole.Viewer)).Success);
        Assert.Equal(UserRole.Viewer, (await auth.GetUsersAsync()).Single(u => u.Id == user.Id).Role);

        Assert.True((await auth.ResetPasswordAsync(TestHub.AdminUserName, user.Id, "Reset-Pass-New1")).Success);
        Assert.True((await auth.LoginAsync("ops1", "Reset-Pass-New1", null)).Success);
        Assert.False((await auth.LoginAsync("ops1", "Operator-Pass1", null)).Success);
    }

    [Fact]
    public async Task ChangePassword_RequiresCurrentPassword()
    {
        await using var hub = await TestHub.CreateAsync();

        var wrong = await hub.Get<IAuthService>().ChangePasswordAsync("admin", "not-the-password", "New-Pass-1234");
        var right = await hub.Get<IAuthService>().ChangePasswordAsync("admin", TestHub.AdminPassword, "New-Pass-1234");

        Assert.False(wrong.Success);
        Assert.True(right.Success, right.Error);
        Assert.True((await hub.Get<IAuthService>().LoginAsync("admin", "New-Pass-1234", null)).Success);
    }

    [Fact]
    public async Task PasswordIsNotStoredInPlainText()
    {
        await using var hub = await TestHub.CreateAsync();
        await using var db = await hub.Get<IDbContextFactory<RemoteDeskDbContext>>().CreateDbContextAsync();
        var user = await db.UserAccounts.SingleAsync(u => u.Username == TestHub.AdminUserName);

        Assert.DoesNotContain(TestHub.AdminPassword, user.PasswordHash);
        Assert.NotEqual(TestHub.AdminPassword, user.PasswordHash);
        Assert.Equal(PasswordHasher.DefaultIterations, user.Iterations);
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordSalt));
    }
}
