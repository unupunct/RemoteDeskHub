using System.Windows;
using System.Windows.Input;

namespace RemoteDeskHub.Desktop.Services;

public partial class PasswordPromptWindow : Window
{
    public PasswordPromptWindow(string title, string label)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        Loaded += (_, _) => ValueBox.Focus();
    }

    public string? Value { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ValueBox.Password))
        {
            ErrorText.Text = "A value is required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Value = ValueBox.Password;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Value_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
        }
    }
}
