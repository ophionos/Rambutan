using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Rambutan.App.Views;

public partial class TextPromptDialog : Window
{
    // Designer only
    public TextPromptDialog()
    {
        InitializeComponent();
    }

    public TextPromptDialog(string title, string initialText) : this()
    {
        Title = title;
        InputBox.Text = initialText;
        Opened += (_, _) => InputBox.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Accept();
    }

    private void OnAccept(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        var text = InputBox.Text?.Trim();
        Close(string.IsNullOrEmpty(text) ? null : text);
    }
}
