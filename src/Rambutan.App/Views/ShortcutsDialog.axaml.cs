using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Rambutan.App.Views;

public partial class ShortcutsDialog : Window
{
    public ShortcutsDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
