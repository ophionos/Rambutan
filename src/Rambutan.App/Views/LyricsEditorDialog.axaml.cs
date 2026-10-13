using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class LyricsEditorDialog : Window
{
    // Designer only
    public LyricsEditorDialog()
    {
        InitializeComponent();
    }

    public LyricsEditorDialog(LyricsEditorViewModel editor) : this()
    {
        DataContext = editor;
        editor.CloseRequested += Close;
        Closed += (_, _) => editor.CloseRequested -= Close;
        Opened += (_, _) => LyricsText.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LyricsEditorViewModel editor)
            return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("lyrics.editor.openFile"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(Strings.Get("lyrics.editor.fileTypes")) { Patterns = ["*.lrc", "*.txt"] },
                ],
            });
            if (files.Count == 0)
                return;

            await using var stream = await files[0].OpenReadAsync();
            await editor.LoadFileAsync(stream);
        }
        catch (Exception ex)
        {
            editor.ReportFileError(ex);
        }
    }
}
