using System.Windows;

namespace RemoteDeskHub.Desktop.Services;

public partial class ConsentWindow : Window
{
    public ConsentWindow(string title, string message, string consentText)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        MessageText.Text = message;
        ConsentCheck.Content = consentText;
        Loaded += (_, _) => ConsentCheck.Focus();
    }

    public bool ConsentGiven => ConsentCheck.IsChecked == true;

    private void ConsentCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (LaunchButton is not null)
        {
            LaunchButton.IsEnabled = ConsentGiven;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (!ConsentGiven)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
