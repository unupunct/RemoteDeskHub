using System.Windows;
using System.Windows.Controls;

namespace RemoteDeskHub.Desktop.Services;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string? initialValue, bool multiline)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = label;
        Input.Text = initialValue ?? string.Empty;
        if (multiline)
        {
            Input.AcceptsReturn = true;
            Input.TextWrapping = TextWrapping.Wrap;
            Input.Height = 100;
            Input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
