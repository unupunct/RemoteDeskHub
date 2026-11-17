using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Domain;
using RemoteDeskHub.Core.Models;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Desktop.Services;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.ViewModels;

public sealed partial class UsersViewModel : PageViewModel
{
    private readonly IAuthService _auth;
    private readonly ISessionContext _session;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private ObservableCollection<UserAccountView> users = [];

    [ObservableProperty]
    private UserAccountView? selectedUser;

    [ObservableProperty]
    private string newUsername = string.Empty;

    [ObservableProperty]
    private string newPassword = string.Empty;

    [ObservableProperty]
    private UserRole newRole = UserRole.Operator;

    [ObservableProperty]
    private UserRole selectedRole = UserRole.Operator;

    [ObservableProperty]
    private string summaryText = string.Empty;

    public UsersViewModel(IAuthService auth, ISessionContext session, IDialogService dialogs)
    {
        _auth = auth;
        _session = session;
        _dialogs = dialogs;
    }

    public override string Title => "Users & Roles";
    public override string Subtitle => "Accounts, roles and access to management operations";

    public string Actor => _session.CurrentUser?.UserName ?? HubConstants.AuditActorSystem;
    public bool CanManage => _session.CurrentUser?.Can(Permission.ManageUsers) == true;
    public IReadOnlyList<UserRole> Roles { get; } = Enum.GetValues<UserRole>();

    public override async Task LoadAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _auth.GetUsersAsync(token);
            Users = new ObservableCollection<UserAccountView>(data);
            SelectedUser ??= Users.FirstOrDefault(u => u.Username == Actor) ?? Users.FirstOrDefault();
            SummaryText = $"{data.Count} account(s) · {data.Count(u => u.IsEnabled)} enabled · " +
                          $"{data.Count(u => u.Role == UserRole.Administrator && u.IsEnabled)} administrator(s)";
        });
    }

    partial void OnSelectedUserChanged(UserAccountView? value)
    {
        if (value is not null)
        {
            SelectedRole = value.Role;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            var data = await _auth.GetUsersAsync(token);
            Users = new ObservableCollection<UserAccountView>(data);
            SummaryText = $"{data.Count} account(s) · {data.Count(u => u.IsEnabled)} enabled";
        }, "User list refreshed.");
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUsername))
        {
            Fail("Enter a user name.");
            return;
        }

        var password = _dialogs.PromptPassword("New user password",
            $"Set a password for '{NewUsername.Trim()}'. At least 10 characters with upper-case, lower-case and a digit.");
        if (password is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _auth.CreateUserAsync(Actor, NewUsername.Trim(), password, NewRole, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The user could not be created.");
            }

            NewUsername = string.Empty;
            NewPassword = string.Empty;
            var data = await _auth.GetUsersAsync(token);
            Users = new ObservableCollection<UserAccountView>(data);
        }, $"User '{NewUsername}' created.");
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedUser is null)
        {
            return;
        }

        var enable = !SelectedUser.IsEnabled;
        if (!enable && !_dialogs.Confirm("Disable account",
                $"Disable '{SelectedUser.Username}'? They will be unable to sign in until re-enabled.", danger: true))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _auth.SetUserEnabledAsync(Actor, SelectedUser.Id, enable, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The account could not be updated.");
            }

            var data = await _auth.GetUsersAsync(token);
            Users = new ObservableCollection<UserAccountView>(data);
            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id) ?? Users.FirstOrDefault();
        }, enable ? "Account enabled." : "Account disabled.");
    }

    [RelayCommand]
    private async Task ApplyRoleAsync()
    {
        if (SelectedUser is null)
        {
            return;
        }

        if (SelectedUser.Role == SelectedRole)
        {
            StatusMessage = "The selected role is already applied.";
            return;
        }

        if (!_dialogs.Confirm("Change role",
                $"Change '{SelectedUser.Username}' from {SelectedUser.Role} to {SelectedRole}?\n\n{Permissions.Describe(SelectedRole)}",
                danger: SelectedRole < SelectedUser.Role))
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _auth.ChangeRoleAsync(Actor, SelectedUser.Id, SelectedRole, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The role could not be changed.");
            }

            var data = await _auth.GetUsersAsync(token);
            Users = new ObservableCollection<UserAccountView>(data);
            SelectedUser = Users.FirstOrDefault(u => u.Id == SelectedUser.Id) ?? Users.FirstOrDefault();
        }, "Role updated.");
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (SelectedUser is null)
        {
            return;
        }

        var password = _dialogs.PromptPassword("Reset password",
            $"Set a new password for '{SelectedUser.Username}'. The account will be unlocked.");
        if (password is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _auth.ResetPasswordAsync(Actor, SelectedUser.Id, password, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The password could not be reset.");
            }
        }, "Password reset.");
    }

    [RelayCommand]
    private async Task ChangeOwnPasswordAsync()
    {
        var current = _dialogs.PromptPassword("Change your password", "Enter your current password.");
        if (current is null)
        {
            return;
        }

        var updated = _dialogs.PromptPassword("Change your password", "Enter the new password.");
        if (updated is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var result = await _auth.ChangePasswordAsync(Actor, current, updated, token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? "The password could not be changed.");
            }
        }, "Your password was changed.");
    }
}
