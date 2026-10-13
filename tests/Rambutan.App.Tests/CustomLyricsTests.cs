using System.Text;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;
using Rambutan.Lyrics.Providers;
using Rambutan.Lyrics.Store;

namespace Rambutan.App.Tests;

/// <summary>The user's own lyrics from the pane: add,
/// edit, remove and the timing nudges, against a real store on disk.</summary>
public sealed class CustomLyricsTests : IDisposable
{
    private static readonly TrackInfo Crush = new()
    {
        VideoId = "crush",
        Title = "Instant Crush",
        Artists = [new("Daft Punk", "UCdp")],
        Duration = TimeSpan.FromMinutes(5),
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-custom-" + Guid.NewGuid().ToString("N"));
    private readonly LyricsService _service;

    public CustomLyricsTests() => _service = LyricsService.Create(_dir, []);

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A pane on the real service; <paramref name="found"/> stands in
    /// for what the providers would answer.</summary>
    private LyricsViewModel Pane(LyricsLookup? found = null) => new(
        found is null ? _service.GetAsync : (_, _) => Task.FromResult(found),
        _ => Task.CompletedTask,
        custom: CustomLyricsAccess.For(_service));

    private static async Task Settle(LyricsViewModel vm)
    {
        for (var i = 0; i < 200 && vm.State is LyricsState.Loading; i++)
            await Task.Delay(10);
        Assert.NotEqual(LyricsState.Loading, vm.State);
    }

    /// <summary>The window's part: shows the editor and does what the user does in it.</summary>
    private static Func<LyricsEditorViewModel, Task> User(Action<LyricsEditorViewModel> act) => editor =>
    {
        act(editor);
        return Task.CompletedTask;
    };

    [Fact]
    public async Task AddingLyrics_WhereNoneWereFound_SavesAndShowsThem()
    {
        var vm = Pane();
        vm.OnTrackChanged(Crush);
        await Settle(vm);
        Assert.Equal(LyricsState.None, vm.State);
        Assert.True(vm.CanEditLyrics);

        vm.OpenEditor = User(editor =>
        {
            Assert.Equal("", editor.Text);
            Assert.False(editor.CanRemove);
            Assert.False(editor.SaveCommand.CanExecute(null));
            editor.Text = "[00:01.00]<00:01.00>Mine <00:01.50>now";
            editor.SaveCommand.Execute(null);
        });
        await vm.EditLyricsCommand.ExecuteAsync(null);

        Assert.Equal(LyricsState.Synced, vm.State);
        Assert.True(vm.IsCustom);
        Assert.True(vm.IsOffsetShown);
        Assert.Equal("Your lyrics", vm.SourceLabel);
        Assert.Equal(2, vm.Lines[0].Words!.Count);
        Assert.Equal("[00:01.00]<00:01.00>Mine <00:01.50>now", _service.GetCustom("crush")!.Text);
    }

    [Fact]
    public async Task Editing_FoundLyrics_StartsFromThemAsLrc()
    {
        var found = LrcParser.Parse("[00:04.00]Streetlights\n[00:08.50]夜の街に", LrcLibProvider.SourceName);
        var vm = Pane(new LyricsLookup(LyricsStatus.Found, found));
        vm.OnTrackChanged(Crush);
        await Settle(vm);
        Assert.False(vm.IsCustom);
        Assert.False(vm.IsOffsetShown);

        string? seen = null;
        vm.OpenEditor = User(editor => seen = editor.Text); // then cancelled
        await vm.EditLyricsCommand.ExecuteAsync(null);

        Assert.Equal(LrcWriter.Write(found), seen);
        Assert.Null(_service.GetCustom("crush"));
        Assert.Equal("Lyrics from LRCLIB", vm.SourceLabel);
    }

    [Fact]
    public async Task Nudges_MoveTheLines_SaveTheOffset_AndNeverShowTheSkeleton()
    {
        _service.SaveCustom("crush", new CustomLyrics("[00:02.00]Mine"));
        var vm = Pane();
        vm.OnTrackChanged(Crush);
        await Settle(vm);
        Assert.Equal("Timing as written", vm.OffsetLabel);
        Assert.False(vm.IsOffsetSet);
        var states = new List<LyricsState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LyricsViewModel.State))
                states.Add(vm.State);
        };

        await vm.ShowEarlierCommand.ExecuteAsync(null);
        Assert.Equal(TimeSpan.FromSeconds(1.9), vm.Lines[0].Start);
        Assert.Equal("0.1 s earlier", vm.OffsetLabel);

        await vm.ShowMuchLaterCommand.ExecuteAsync(null);
        Assert.Equal(TimeSpan.FromSeconds(2.4), vm.Lines[0].Start);
        Assert.Equal("0.4 s later", vm.OffsetLabel);
        Assert.Equal(new CustomLyrics("[00:02.00]Mine", -400), _service.GetCustom("crush"));

        await vm.ResetOffsetCommand.ExecuteAsync(null);
        Assert.Equal(TimeSpan.FromSeconds(2), vm.Lines[0].Start);
        Assert.Equal(0, _service.GetCustom("crush")!.OffsetMs);

        Assert.DoesNotContain(LyricsState.Loading, states);
    }

    [Fact]
    public async Task Editing_KeepsTheOffset()
    {
        _service.SaveCustom("crush", new CustomLyrics("[00:02.00]Mine", 300));
        var vm = Pane();
        vm.OnTrackChanged(Crush);
        await Settle(vm);

        vm.OpenEditor = User(editor =>
        {
            editor.Text = "[00:03.00]Changed";
            editor.SaveCommand.Execute(null);
        });
        await vm.EditLyricsCommand.ExecuteAsync(null);

        Assert.Equal(TimeSpan.FromSeconds(2.7), vm.Lines[0].Start);
        Assert.Equal(300, vm.CustomOffsetMs);
    }

    [Fact]
    public async Task Removing_AsksFirst_ThenFallsBackToWhatWasFound()
    {
        _service.SaveCustom("crush", new CustomLyrics("Mine"));
        var vm = Pane();
        vm.OnTrackChanged(Crush);
        await Settle(vm);
        Assert.True(vm.IsCustom);
        Assert.Equal(LyricsState.Unsynced, vm.State);

        vm.OpenEditor = User(editor =>
        {
            Assert.True(editor.CanRemove);
            editor.RemoveCommand.Execute(null);
            Assert.True(editor.IsConfirmingRemove);
            Assert.False(editor.IsRemoveShown);
            Assert.Equal(LyricsEditorOutcome.Cancelled, editor.Outcome);
            editor.RemoveCommand.Execute(null);
        });
        await vm.EditLyricsCommand.ExecuteAsync(null);

        Assert.Null(_service.GetCustom("crush"));
        Assert.False(vm.IsCustom);
        Assert.Equal(LyricsState.None, vm.State);
    }

    [Fact]
    public async Task ATrackChange_WhileEditing_SavesForTheTrackItWasOpenedOn()
    {
        var vm = Pane();
        vm.OnTrackChanged(Crush);
        await Settle(vm);
        var next = Crush with { VideoId = "next", Title = "Next" };

        vm.OpenEditor = User(editor =>
        {
            vm.OnTrackChanged(next);
            editor.Text = "Mine";
            editor.SaveCommand.Execute(null);
        });
        await vm.EditLyricsCommand.ExecuteAsync(null);
        await Settle(vm);

        Assert.Equal("Mine", _service.GetCustom("crush")!.Text);
        Assert.Null(_service.GetCustom("next"));
        Assert.Equal(LyricsState.None, vm.State);
    }

    [Theory]
    [InlineData("", "Nothing to save yet.")]
    [InlineData("Just words", "No timestamps: shown as text that doesn't follow playback.")]
    [InlineData("[00:01.00]One", "Line-timed: the playing line is highlighted.")]
    [InlineData("[00:01.00]<00:01.00>One <00:01.50>two", "Word-timed: each word lights up as it's sung.")]
    public void TheEditor_SaysHowTheTextWillPlay(string text, string label)
    {
        var editor = new LyricsEditorViewModel("T", text, false, _ => { }, null);

        Assert.Equal(label, editor.FormatLabel);
        Assert.Equal(text.Length > 0, editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void AFailedSave_KeepsTheEditorOpen_AndSaysWhy()
    {
        var closed = false;
        var editor = new LyricsEditorViewModel("T", "Mine", false, _ => throw new IOException("disk full"), null);
        editor.CloseRequested += () => closed = true;

        editor.SaveCommand.Execute(null);

        Assert.False(closed);
        Assert.Equal(LyricsEditorOutcome.Cancelled, editor.Outcome);
        Assert.Equal("Couldn't save: disk full", editor.Error);
        Assert.Equal("Mine", editor.Text);
    }

    [Fact]
    public async Task AnOpenedFile_ReplacesTheText_WhateverItsBomAndLineEnds()
    {
        var editor = new LyricsEditorViewModel("T", "old", false, null, null);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("[00:01.00]夜\r\n[00:02.00]街\r\n")).ToArray();

        await editor.LoadFileAsync(new MemoryStream(bytes));

        Assert.Equal("[00:01.00]夜\n[00:02.00]街\n", editor.Text);
        Assert.Null(editor.Error);
    }

    [Fact]
    public async Task AFileTooLargeToBeLyrics_IsRefused()
    {
        var editor = new LyricsEditorViewModel("T", "old", false, null, null);

        await editor.LoadFileAsync(new MemoryStream(new byte[LyricsEditorViewModel.MaxFileBytes + 1]));

        Assert.Equal("old", editor.Text);
        Assert.NotNull(editor.Error);
    }
}
