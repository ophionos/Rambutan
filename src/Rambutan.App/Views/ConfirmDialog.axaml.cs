using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Rambutan.App.Views;

public partial class ConfirmDialog : Window
{
    // Designer only
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string message) : this()
    {
        Message.Text = message;
    }

    /// <summary>With the accept button named for what it does ("Delete"), for a
    /// destructive action.</summary>
    public ConfirmDialog(string message, string acceptLabel) : this(message)
    {
        AcceptButton.Content = acceptLabel;
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
