using System.Windows;
using System.Windows.Input;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;

namespace RemoteDeskHub.Desktop.Views;

public partial class LoginWindow : Window
{
    private readonly IAuthService _auth;
    private readonly ISettingsService _settings;
    private readonly IHubServer _hub;
    private readonly ISessionContext _session;

    public LoginWindow(IAuthService auth, ISettingsService settings, IHubServer hub, ISessionContext session)
    {
        InitializeComponent();
        _auth = auth;
        _settings = settings;
        _hub = hub;
        _session = session;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var port = await _settings.GetAsync(RemoteDeskHub.Shared.SettingsKeys.HubPort,
                RemoteDeskHub.Shared.HubConstants.DefaultHubPort);
            EndpointText.Text = _hub.IsRunning
                ? $"Hub listening on port {_hub.BoundPort}"
                : $"Hub listener not running (configured port {port})";
        }
        catch
        {
            EndpointText.Text = "Hub endpoint unavailable";
        }

        UserNameBox.Text = RemoteDeskHub.Shared.HubConstants.DefaultAdminUserName;
        UserNameBox.SelectAll();
        UserNameBox.Focus();
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await SignInAsync();
    }

    private async Task SignInAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var username = UserNameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            ShowError("Enter your user name and password.");
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _auth.LoginAsync(username, password, "local-console");
            if (!result.Success || result.Value is null)
            {
                ShowError(result.Error ?? "Sign in failed.");
                PasswordBox.Clear();
                PasswordBox.Focus();
                return;
            }

            _session.SignIn(result.Value);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Sign in failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SignInButton.IsEnabled = !busy;
        SignInButton.Content = busy ? "Signing in…" : "Sign in";
        UserNameBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SignInAsync();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
