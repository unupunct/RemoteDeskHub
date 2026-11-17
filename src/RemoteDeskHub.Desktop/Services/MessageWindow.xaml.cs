using System.Windows;
using System.Windows.Media;

namespace RemoteDeskHub.Desktop.Services;

public enum MessageKind
{
    Information,
    Warning,
    Error,
    Confirmation
}

public partial class MessageWindow : Window
{
    public MessageWindow(string title, string message, MessageKind kind, bool danger = false)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        MessageText.Text = message;
        AccentBar.Background = (Brush)(Application.Current.TryFindResource(kind switch
        {
            MessageKind.Error => "DangerBrush",
            MessageKind.Warning => "WarningBrush",
            MessageKind.Confirmation when danger => "DangerBrush",
            MessageKind.Confirmation => "AccentCyanBrush",
            _ => "AccentBrush"
        }) ?? Brushes.DodgerBlue);

        if (kind == MessageKind.Confirmation)
        {
            CancelButton.Visibility = Visibility.Visible;
            OkButton.Content = danger ? "Confirm" : "Continue";
            OkButton.Style = (Style)(Application.Current.TryFindResource(danger ? "DangerButton" : "PrimaryButton") ?? OkButton.Style);
        }
        else
        {
            OkButton.Content = "Close";
        }

        Loaded += (_, _) => OkButton.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
