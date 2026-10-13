using Avalonia.Controls;
using Avalonia.Interactivity;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class ProfilesDialog : Window
{
    // Designer only
    public ProfilesDialog()
    {
        InitializeComponent();
    }

    public ProfilesDialog(ProfilesViewModel profiles) : this()
    {
        DataContext = profiles;
        // Its own prompts sit over this dialog, not the main window.
        profiles.TextPrompt = async (title, initial) =>
            await new TextPromptDialog(title, initial).ShowDialog<string?>(this);
        profiles.Confirm = async (message, accept) =>
            await new ConfirmDialog(message, accept).ShowDialog<bool>(this);
        Closed += (_, _) =>
        {
            profiles.TextPrompt = null;
            profiles.Confirm = null;
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
