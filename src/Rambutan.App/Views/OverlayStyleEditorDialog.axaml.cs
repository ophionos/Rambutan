using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class OverlayStyleEditorDialog : Window
{
    private bool _discardConfirmed;

    static OverlayStyleEditorDialog()
    {
        // The colour picker's spectrum names the colour under its marker in a
        // tooltip it opens itself, in English (Avalonia's own names, shown when
        // the thread culture is English, not the app's language). The app can't
        // translate them, and the hex box says the colour, so they stay closed.
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Panel>((panel, e) =>
        {
            if (e.NewValue is true && panel.Name == "PART_SelectionEllipsePanel" && panel.TemplatedParent is ColorSpectrum)
                ToolTip.SetIsOpen(panel, false);
        });
    }

    // Designer only
    public OverlayStyleEditorDialog()
    {
        InitializeComponent();
    }

    public OverlayStyleEditorDialog(OverlayStyleEditorViewModel editor) : this()
    {
        DataContext = editor;
        Opened += (_, _) => FirstField.Focus();
        // Whatever closes the window, unsaved edits leave the overlays.
        Closed += (_, _) => editor.Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Only a close the user asked for here waits for the discard question.</summary>
    internal static bool AsksBeforeClosing(WindowCloseReason reason) =>
        reason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;

    /// <summary>Unsaved edits are the user's work: closing asks before it drops them.</summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // The app quitting, or the main window closing, doesn't wait for an
        // answer; the edits are dropped as on Discard (Closed restores the style).
        if (_discardConfirmed || !AsksBeforeClosing(e.CloseReason) ||
            DataContext is not OverlayStyleEditorViewModel { IsDirty: true })
            return;
        e.Cancel = true;
        if (await new ConfirmDialog(Strings.Get("settings.overlay.editor.discard")).ShowDialog<bool>(this))
        {
            _discardConfirmed = true;
            Close();
        }
    }
}
