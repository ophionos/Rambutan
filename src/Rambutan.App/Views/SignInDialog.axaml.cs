using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.InnerTube;

namespace Rambutan.App.Views;

public partial class SignInDialog : Window
{
    // Designer only
    public SignInDialog()
    {
        InitializeComponent();
    }

    public SignInDialog(SignInDialogViewModel vm) : this()
    {
        DataContext = vm;
        vm.Succeeded += OnSucceeded;
        // Opening counts: the request may already be on the clipboard. Activated
        // covers coming back from the browser after copying it (step 4).
        Activated += async (_, _) => vm.OfferClipboard(await ReadClipboardAsync());
        Closed += (_, _) =>
        {
            vm.Succeeded -= OnSucceeded;
            vm.Cancel();
        };
    }

    private void OnSucceeded(InnerTubeSession session, SessionInfo info) => Close(true);

    private async System.Threading.Tasks.Task<string?> ReadClipboardAsync()
    {
        try
        {
            return Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        }
        catch (Exception)
        {
            // Another app holding the clipboard (Windows), or no clipboard owner
            // (X11): the paste box still works.
            return null;
        }
    }

    private async void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(SignInDialogViewModel.YouTubeMusicUrl);
                CopyLinkButton.Content = Strings.Get("dialog.signIn.linkCopied");
            }
        }
        catch (Exception)
        {
            // Clipboard busy or missing; the step still names the site.
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
