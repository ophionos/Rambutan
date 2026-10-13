using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.Lyrics.Store;

namespace Rambutan.App.ViewModels;

public enum LyricsEditorOutcome { Cancelled, Saved, Removed }

/// <summary>
/// The custom lyrics editor: pasted or opened text,
/// what it will play as, then save or remove. Saving and removing go through
/// the callbacks given, which throw on failure; the editor stays open and says
/// why, so the user's text is never lost to a failed write.
/// </summary>
public sealed partial class LyricsEditorViewModel : ViewModelBase
{
    /// <summary>Larger than any lyrics file: a bigger one is the wrong file.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private readonly Action<string>? _save;
    private readonly Action? _remove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Format), nameof(FormatLabel))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = "";

    /// <summary>Why the last save, removal or file open failed; null when it didn't.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>Remove was pressed once: the editor asks before removing the user's work.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoveShown))]
    private bool _isConfirmingRemove;

    // Design-time.
    public LyricsEditorViewModel()
        : this("Instant Crush", "[00:04.00]Streetlights blinking out of time\n[00:08.50]夜の街に 光が揺れる", true, null, null)
    {
    }

    /// <param name="trackTitle">The track the lyrics are for, for the heading.</param>
    /// <param name="text">The text to start from: the user's own lyrics, or what was found online.</param>
    /// <param name="canRemove">The track has lyrics of the user's own to remove.</param>
    public LyricsEditorViewModel(string trackTitle, string text, bool canRemove, Action<string>? save, Action? remove)
    {
        Heading = Strings.Format("lyrics.editor.title", ("title", trackTitle));
        _text = text;
        CanRemove = canRemove;
        _save = save;
        _remove = remove;
    }

    public string Heading { get; }

    public bool CanRemove { get; }

    /// <summary>The first Remove button; it gives way to the confirmation row.</summary>
    public bool IsRemoveShown => CanRemove && !IsConfirmingRemove;

    public LyricsEditorOutcome Outcome { get; private set; }

    /// <summary>Raised when the editor is done (saved or removed) and should close.</summary>
    public event Action? CloseRequested;

    public CustomLyricsFormat Format => CustomLyricsStore.FormatOf(Text);

    public string FormatLabel => Strings.Get(Format switch
    {
        CustomLyricsFormat.WordTimed => "lyrics.editor.format.wordTimed",
        CustomLyricsFormat.LineTimed => "lyrics.editor.format.lineTimed",
        CustomLyricsFormat.Plain => "lyrics.editor.format.plain",
        _ => "lyrics.editor.format.empty",
    });

    private bool CanSave => Format is not CustomLyricsFormat.Empty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        try
        {
            _save?.Invoke(Text);
        }
        catch (Exception ex)
        {
            Error = Strings.Format("lyrics.editor.saveFailed", ("message", ex.Message));
            return;
        }

        Finish(LyricsEditorOutcome.Saved);
    }

    [RelayCommand]
    private void Remove()
    {
        if (!CanRemove)
            return;

        if (!IsConfirmingRemove)
        {
            IsConfirmingRemove = true;
            return;
        }

        try
        {
            _remove?.Invoke();
        }
        catch (Exception ex)
        {
            IsConfirmingRemove = false;
            Error = Strings.Format("lyrics.editor.removeFailed", ("message", ex.Message));
            return;
        }

        Finish(LyricsEditorOutcome.Removed);
    }

    [RelayCommand]
    private void KeepLyrics() => IsConfirmingRemove = false;

    /// <summary>Replaces the text with a file's contents (.lrc or .txt). The
    /// encoding follows a byte-order mark, else UTF-8.</summary>
    public async Task LoadFileAsync(Stream stream)
    {
        try
        {
            var buffer = new byte[MaxFileBytes + 1];
            var read = 0;
            int got;
            while (read < buffer.Length && (got = await stream.ReadAsync(buffer.AsMemory(read))) > 0)
                read += got;

            if (read > MaxFileBytes)
            {
                Error = Strings.Get("lyrics.editor.fileTooLarge");
                return;
            }

            using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            Text = (await reader.ReadToEndAsync()).Replace("\r\n", "\n");
            Error = null;
        }
        catch (Exception ex)
        {
            Error = Strings.Format("lyrics.editor.fileFailed", ("message", ex.Message));
        }
    }

    /// <summary>The file picker couldn't open the file it returned.</summary>
    public void ReportFileError(Exception ex) =>
        Error = Strings.Format("lyrics.editor.fileFailed", ("message", ex.Message));

    private void Finish(LyricsEditorOutcome outcome)
    {
        Error = null;
        Outcome = outcome;
        CloseRequested?.Invoke();
    }
}
