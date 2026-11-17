using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Core.Security;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.Views;

/// <summary>Nine-step first-run wizard: administrator, network, TLS, storage, discovery, alerts, agent policy and summary.</summary>
public partial class FirstRunWizard : Window
{
    private readonly IAuthService _auth;
    private readonly ISettingsService _settings;
    private readonly IMonitoringService _monitoring;
    private readonly ICertificateAuthority _certificates;
    private readonly IAppPaths _paths;
    private readonly IHubServer _hub;
    private bool _finishing;

    public FirstRunWizard(IAuthService auth, ISettingsService settings, IMonitoringService monitoring,
        ICertificateAuthority certificates, IAppPaths paths, IHubServer hub)
    {
        InitializeComponent();
        _auth = auth;
        _settings = settings;
        _monitoring = monitoring;
        _certificates = certificates;
        _paths = paths;
        _hub = hub;

        DataPathText.Text = _paths.RootDirectory;
        DatabasePathText.Text = _paths.DatabasePath;
        LogsPathText.Text = _paths.LogsDirectory;
        BackupsPathText.Text = _paths.BackupsDirectory;
        HubUrlBox.Text = $"https://localhost:{HubConstants.DefaultHubPort}";

        Steps.SelectedIndex = 0;
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        BackButton.IsEnabled = Steps.SelectedIndex > 0 && !_finishing;
        NextButton.Visibility = Steps.SelectedIndex < Steps.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = Steps.SelectedIndex == Steps.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        StepCounter.Text = $"Step {Steps.SelectedIndex + 1} of {Steps.Items.Count}";
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateStep(Steps.SelectedIndex, showError: true))
        {
            return;
        }

        if (Steps.SelectedIndex == 7)
        {
            BuildSummary();
        }

        Steps.SelectedIndex++;
        UpdateNavigation();
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        await FinishAsync();
    }

    private void BuildSummary()
    {
        TryPort(HubPortBox.Text, out var hubPort);
        TryPort(AgentPortBox.Text, out var agentPort);
        SummaryText.Text =
            $"Administrator: {AdminUserBox.Text.Trim()}\n" +
            $"Hub endpoint: {HubUrlBox.Text.Trim()} (port {hubPort})\n" +
            $"Agent management port: {agentPort}\n" +
            $"Data root: {_paths.RootDirectory}\n" +
            $"Discovery: {DiscoveryTimeoutBox.Text} ms timeout, {DiscoveryConcurrencyBox.Text} concurrent probes\n" +
            $"Alert thresholds: CPU {CpuThresholdBox.Text}% · memory {MemoryThresholdBox.Text}% · disk free {DiskThresholdBox.Text}% · temperature {TemperatureThresholdBox.Text} °C\n" +
            $"Agent approval: {(AutoApproveBox.IsChecked == true ? "automatic with a valid token" : "manual administrator approval")}\n" +
            $"Permitted file roots: {AllowedRootsBox.Text.Replace("\r", " ").Replace("\n", " ")}";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex > 0)
        {
            Steps.SelectedIndex--;
            UpdateNavigation();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Setup is not complete. If you exit now, RemoteDesk Hub cannot be used until setup is finished. Exit?",
            "RemoteDesk Hub setup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes)
        {
            DialogResult = false;
            Close();
        }
    }

    private void Steps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Steps))
        {
            return;
        }

        UpdateNavigation();
    }

    private bool ValidateStep(int index, bool showError)
    {
        string? error = null;
        switch (index)
        {
            case 1:
                if (!CredentialRules.IsValidUsername(AdminUserBox.Text.Trim(), out var userError))
                {
                    error = userError;
                }
                else if (!PasswordHasher.IsPasswordStrong(AdminPasswordBox.Password, out var passwordError))
                {
                    error = passwordError;
                }
                else if (!string.Equals(AdminPasswordBox.Password, AdminConfirmBox.Password, StringComparison.Ordinal))
                {
                    error = "The two passwords do not match.";
                }
                break;

            case 2:
                if (!TryPort(HubPortBox.Text, out _))
                {
                    error = "Hub port must be a number between 1 and 65535.";
                }
                else if (!TryPort(AgentPortBox.Text, out _))
                {
                    error = "Agent port must be a number between 1 and 65535.";
                }
                else if (!Uri.TryCreate(HubUrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    error = "Hub URL must be a valid https:// address.";
                }
                else if (string.IsNullOrWhiteSpace(BindAddressBox.Text))
                {
                    error = "A bind address is required. Use 0.0.0.0 for all LAN interfaces or 127.0.0.1 for this computer only.";
                }
                break;

            case 5:
                if (!int.TryParse(DiscoveryTimeoutBox.Text, out var timeout) || timeout is < 100 or > 10000)
                {
                    error = "Discovery timeout must be between 100 and 10000 milliseconds.";
                }
                else if (!int.TryParse(DiscoveryConcurrencyBox.Text, out var concurrency) || concurrency is < 1 or > 128)
                {
                    error = "Discovery concurrency must be between 1 and 128.";
                }
                else if (!int.TryParse(DiscoveryIntervalBox.Text, out var interval) || interval is < 0 or > 1440)
                {
                    error = "Discovery interval must be between 0 and 1440 minutes.";
                }
                break;

            case 6:
                if (!TryPercent(CpuThresholdBox.Text, out _))
                {
                    error = "CPU threshold must be between 1 and 100.";
                }
                else if (!TryPercent(MemoryThresholdBox.Text, out _))
                {
                    error = "Memory threshold must be between 1 and 100.";
                }
                else if (!TryPercent(DiskThresholdBox.Text, out _))
                {
                    error = "Disk threshold must be between 1 and 100.";
                }
                else if (!double.TryParse(TemperatureThresholdBox.Text, out var temp) || temp is < 30 or > 150)
                {
                    error = "Temperature threshold must be between 30 and 150 °C.";
                }
                break;

            case 7:
                var roots = AllowedRootsBox.Text
                    .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (roots.Count == 0)
                {
                    error = "At least one permitted file root is required.";
                }
                else if (roots.Any(r => !RemoteDeskHub.Shared.Security.PathGuard.IsValidAbsolutePath(r, out _)))
                {
                    error = "Every permitted file root must be an absolute local drive path (for example C:\\Users).";
                }
                break;
        }

        if (error is not null && showError)
        {
            WizardError.Text = error;
            WizardError.Visibility = Visibility.Visible;
        }
        else
        {
            WizardError.Visibility = Visibility.Collapsed;
        }

        return error is null;
    }

    private async Task FinishAsync()
    {
        if (!ValidateStep(1, false))
        {
            Steps.SelectedIndex = 1;
            UpdateNavigation();
            WizardError.Text = "Check the administrator account details before finishing.";
            WizardError.Visibility = Visibility.Visible;
            return;
        }

        for (var i = 2; i <= 7; i++)
        {
            if (!ValidateStep(i, false))
            {
                Steps.SelectedIndex = i;
                UpdateNavigation();
                ValidateStep(i, showError: true);
                return;
            }
        }

        _finishing = true;
        FinishButton.IsEnabled = false;
        FinishButton.Content = "Finishing…";
        CancelButton.IsEnabled = false;

        try
        {
            var created = await _auth.CreateInitialAdministratorAsync(
                AdminUserBox.Text.Trim(), AdminPasswordBox.Password);
            if (!created.Success)
            {
                WizardError.Text = created.Error ?? "The administrator account could not be created.";
                WizardError.Visibility = Visibility.Visible;
                Steps.SelectedIndex = 1;
                return;
            }

            TryPort(HubPortBox.Text, out var hubPort);
            TryPort(AgentPortBox.Text, out var agentPort);

            await _settings.SetAsync(SettingsKeys.HubPort, hubPort.ToString(), "Network");
            await _settings.SetAsync(SettingsKeys.AgentPort, agentPort.ToString(), "Network");
            await _settings.SetAsync(SettingsKeys.HubUrl, HubUrlBox.Text.Trim(), "Network");
            await _settings.SetAsync(SettingsKeys.BindAddress, BindAddressBox.Text.Trim(), "Network");

            await _settings.SetAsync(SettingsKeys.DiscoveryTimeoutMs, DiscoveryTimeoutBox.Text.Trim(), "Discovery");
            await _settings.SetAsync(SettingsKeys.DiscoveryConcurrency, DiscoveryConcurrencyBox.Text.Trim(), "Discovery");
            await _settings.SetAsync(SettingsKeys.DiscoveryIntervalMinutes, DiscoveryIntervalBox.Text.Trim(), "Discovery");
            await _settings.SetAsync(SettingsKeys.DiscoveryProbePorts, DiscoveryPortsBox.Text.Trim(), "Discovery");

            await _settings.SetAsync(SettingsKeys.AlertCpuThreshold, CpuThresholdBox.Text.Trim(), "Monitoring");
            await _settings.SetAsync(SettingsKeys.AlertMemoryThreshold, MemoryThresholdBox.Text.Trim(), "Monitoring");
            await _settings.SetAsync(SettingsKeys.AlertDiskThreshold, DiskThresholdBox.Text.Trim(), "Monitoring");
            await _settings.SetAsync(SettingsKeys.AlertTemperatureThreshold, TemperatureThresholdBox.Text.Trim(), "Monitoring");

            TryPercent(CpuThresholdBox.Text, out var cpuThreshold);
            TryPercent(MemoryThresholdBox.Text, out var memoryThreshold);
            TryPercent(DiskThresholdBox.Text, out var diskThreshold);
            double.TryParse(TemperatureThresholdBox.Text, out var temperatureThreshold);
            await _monitoring.ApplyDefaultThresholdsAsync("setup", cpuThreshold, memoryThreshold,
                diskThreshold, temperatureThreshold, 30);

            await _settings.SetAsync(SettingsKeys.AgentAutoApprove, AutoApproveBox.IsChecked == true ? "true" : "false", "Agent");
            await _settings.SetAsync(SettingsKeys.AgentAllowCommands, AllowCommandsBox.IsChecked == true ? "true" : "false", "Agent");
            await _settings.SetAsync(SettingsKeys.AgentAllowedRoots,
                string.Join(";", AllowedRootsBox.Text.Split(['\r', '\n', ';'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), "Agent");

            await _certificates.EnsureCreatedAsync();
            CaStatusText.Text = "Certificate authority ready. Agents will trust only certificates issued by this hub.";
            CaStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            WizardError.Text = $"Setup could not be completed: {ex.Message}";
            WizardError.Visibility = Visibility.Visible;
        }
        finally
        {
            _finishing = false;
            FinishButton.IsEnabled = true;
            FinishButton.Content = "Finish setup";
            CancelButton.IsEnabled = true;
            UpdateNavigation();
        }
    }

    private void InitializeCa_Click(object sender, RoutedEventArgs e)
    {
        _ = InitializeCaAsync();
    }

    private async Task InitializeCaAsync()
    {
        try
        {
            CaButton.IsEnabled = false;
            CaStatusText.Text = "Creating the private certificate authority…";
            await _certificates.EnsureCreatedAsync();
            CaStatusText.Text =
                $"Certificate authority ready.\nServer thumbprint: {_certificates.GetServerCertificateThumbprint()}\n" +
                $"Console client thumbprint: {_certificates.GetClientCertificateThumbprint()}";
            CaStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            CaStatusText.Text = $"Certificate authority could not be created: {ex.Message}";
            CaStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        finally
        {
            CaButton.IsEnabled = true;
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_paths.RootDirectory);

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_paths.LogsDirectory);

    private void OpenBackupsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_paths.BackupsDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The folder could not be opened: {ex.Message}", "RemoteDesk Hub",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), out port) && port is >= 1 and <= 65535;

    private static bool TryPercent(string text, out double value) =>
        double.TryParse(text.Trim(), out value) && value is > 0 and <= 100;
}
