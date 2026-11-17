using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Desktop.ViewModels;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ShellViewModel _shell;
    private readonly IAppEvents _events;
    private readonly DispatcherTimer _idleTimer;
    private bool _allowClose;
    private bool _minimizeToTray = true;
    private bool _notifications = true;
    private int _sessionTimeoutMinutes;
    private DateTime _lastActivity = DateTime.UtcNow;

    public MainWindow(ShellViewModel shell, ISettingsService settings, IAppEvents events)
    {
        InitializeComponent();
        _shell = shell;
        _events = events;
        DataContext = shell;
        _settings = settings;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += OnIdleTick;

        PreviewMouseDown += (_, _) => _lastActivity = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _lastActivity = DateTime.UtcNow;

        Loaded += async (_, _) =>
        {
            try
            {
                _minimizeToTray = await _settings.GetAsync(SettingsKeys.ApplicationMinimizeToTray, true);
                _notifications = await _settings.GetAsync(SettingsKeys.EnableWindowsNotifications, true);
                _sessionTimeoutMinutes = await _settings.GetAsync(SettingsKeys.SessionTimeoutMinutes, 60);
                _lastActivity = DateTime.UtcNow;
                if (_sessionTimeoutMinutes > 0)
                {
                    _idleTimer.Start();
                }
            }
            catch
            {
                _minimizeToTray = true;
                _notifications = true;
            }
        };

        _events.NotificationRaised += OnNotificationRaised;

        shell.SignOutRequested += (_, _) =>
        {
            SignedOut = true;
            _shell.Stop();
            _allowClose = true;
            Close();
        };
    }

    /// <summary>True when the window was closed by the Sign out command rather than Exit.</summary>
    public bool SignedOut { get; private set; }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _minimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _idleTimer.Stop();
        _events.NotificationRaised -= OnNotificationRaised;
        TrayIcon.Dispose();
        base.OnClosed(e);
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (_sessionTimeoutMinutes <= 0 || _allowClose)
        {
            _idleTimer.Stop();
            return;
        }

        if (DateTime.UtcNow - _lastActivity < TimeSpan.FromMinutes(_sessionTimeoutMinutes))
        {
            return;
        }

        _idleTimer.Stop();
        TrayIcon.ShowBalloonTip(
            "Session timed out",
            $"No activity for {_sessionTimeoutMinutes} minute(s). Sign in again to continue.",
            BalloonIcon.Warning);
        _shell.SignOutForInactivity();
    }

    private void OnNotificationRaised(object? sender, AppNotification notification)
    {
        if (!_notifications)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var icon = notification.Severity switch
            {
                AlertSeverity.Critical => BalloonIcon.Error,
                AlertSeverity.Warning => BalloonIcon.Warning,
                _ => BalloonIcon.Info
            };
            TrayIcon.ShowBalloonTip(notification.Title, notification.Message, icon);
        });
    }

    private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        CloseForExit();
        Application.Current.Shutdown();
    }
}
