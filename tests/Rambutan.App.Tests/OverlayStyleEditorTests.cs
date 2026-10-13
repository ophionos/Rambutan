using System.Net.Http.Headers;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Queue;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.App.Tests;

/// <summary>The OBS overlay's style editor: edits reach open overlays at
/// once, Save keeps them in the versioned file, and the native preview follows
/// the overlay page's layout.</summary>
public sealed class OverlayStyleEditorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-overlay-editor-");
    private readonly ObsOverlayService _overlay;
    private DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    public OverlayStyleEditorTests() => _overlay = Create();

    private ObsOverlayService Create() => new(
        FakePlayback.Coordinator(new QueueModel()),
        new RemoteTokenVault(_dir.FullName, null, "overlay-link", "keyring.label.overlay"),
        new OverlayStyleStore(_dir.FullName),
        new CoverSource((_, _) => Task.FromResult(Array.Empty<byte>()), _ => null),
        _ => -1, port: 0);

    private OverlayStyleEditorViewModel Editor(AppearanceSettings? appearance = null) =>
        new(_overlay, appearance, () => _now);

    public void Dispose()
    {
        _overlay.StopAsync().GetAwaiter().GetResult();
        _dir.Delete(recursive: true);
    }

    private string StyleFile => Path.Combine(_dir.FullName, "overlay-style.json");

    private void Later() => _now += OverlayStyleEditorViewModel.CoalesceWindow + TimeSpan.FromSeconds(1);

    // ---- CSS hex colours ----

    [Theory]
    [InlineData("#FFFFFF", 255, 255, 255, 255)]
    [InlineData("#FFFFFFB3", 0xB3, 255, 255, 255)]   // alpha last, as CSS has it
    [InlineData("#000000A6", 0xA6, 0, 0, 0)]
    [InlineData("#F00", 255, 255, 0, 0)]
    [InlineData("#F008", 0x88, 255, 0, 0)]
    [InlineData("#ff4e45", 255, 0xFF, 0x4E, 0x45)]
    public void CssHex_ReadsTheAlphaLast(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(CssHexColor.TryParse(text, out var color));
        Assert.Equal(Color.FromArgb(a, r, g, b), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData(null)]
    public void CssHex_RefusesWhatIsntHex(string? text) => Assert.False(CssHexColor.TryParse(text, out _));

    [Theory]
    [InlineData(" ff4e45 ", "#FF4E45")]
    [InlineData("#abc", "#ABC")]
    [InlineData("#12345", null)]
    [InlineData("  ", null)]
    public void CssHex_Normalize_AddsTheHashAndUppercases(string text, string? expected) =>
        Assert.Equal(expected, CssHexColor.Normalize(text));

    [Theory]
    [InlineData(255, 0xFF, 0x4E, 0x45, "#FF4E45")]
    [InlineData(0x80, 0x11, 0x22, 0x33, "#11223380")]
    [InlineData(0, 0, 0, 0, "#00000000")]
    public void CssHex_Format_WritesTheAlphaLast_AndOnlyWhenNotSolid(byte a, byte r, byte g, byte b, string expected)
    {
        Assert.Equal(expected, CssHexColor.Format(Color.FromArgb(a, r, g, b)));
        Assert.Equal(Color.FromArgb(a, r, g, b), CssHexColor.Parse(expected, default));
    }

    // ---- Editing, live ----

    [Fact]
    public void AnEdit_ReachesTheOverlayAtOnce_ButIsNotWritten()
    {
        using var editor = Editor();

        editor.AlignIndex = 1;

        Assert.Equal(OverlayAlign.Right, _overlay.Style.Align);
        Assert.Equal(OverlayConfig.Default, _overlay.SavedStyle);
        Assert.False(File.Exists(StyleFile));
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task AnEdit_IsWhatAnOpenOverlayReadsNext()
    {
        await _overlay.SetEnabledAsync(true);
        using var editor = Editor();

        editor.CornerRadius = 24;
        editor.AccentColor.Hex = "#00ff88";

        var token = _overlay.Url![(_overlay.Url!.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_overlay.Port}/api/v1/overlay/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var json = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
        var served = OverlayConfigFormat.Parse(json).Config!;
        Assert.Equal(24, served.CornerRadius);
        Assert.Equal("#00FF88", served.Colors.Accent);
    }

    [Fact]
    public void Save_WritesTheVersionedFile_ThatLoadsAtTheNextStart()
    {
        using var editor = Editor();
        editor.Scale = 1.5;
        editor.ShowAlbum = true;

        Assert.True(editor.TrySave());

        Assert.False(editor.IsDirty);
        Assert.True(_overlay.IsCustomStyle);
        var text = File.ReadAllText(StyleFile);
        Assert.Contains("\"format\": \"rambutan-overlay\"", text);
        Assert.Contains("\"version\": 1", text);
        var next = Create();
        Assert.Equal(editor.Current, next.Style);
        Assert.Equal(1.5, next.Style.Scale);
    }

    [Fact]
    public void SavingTheDefault_RemovesTheFile()
    {
        _overlay.ImportStyle("""{"format":"rambutan-overlay","version":1,"scale":2}""");
        using var editor = Editor();

        editor.UseDefaultCommand.Execute(null);
        Assert.Equal(2, _overlay.SavedStyle.Scale);   // an edit until saved
        Assert.True(File.Exists(StyleFile));
        editor.SaveCommand.Execute(null);

        Assert.False(File.Exists(StyleFile));
        Assert.False(_overlay.IsCustomStyle);
        Assert.Equal(OverlayConfig.Default, _overlay.Style);
    }

    [Fact]
    public void ANewerVersionsFile_IsNotTouched_UntilAnEditIsSaved()
    {
        const string newer = """{"format":"rambutan-overlay","version":99,"scale":2}""";
        File.WriteAllText(StyleFile, newer);
        var overlay = Create();
        using var editor = new OverlayStyleEditorViewModel(overlay, null, () => _now);

        Assert.NotNull(editor.SavedStyleWarning);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);   // Ctrl+S with nothing to save

        Assert.Equal(newer, File.ReadAllText(StyleFile));

        editor.CornerRadius = 20;
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);

        Assert.Equal(20, OverlayConfigFormat.Parse(File.ReadAllText(StyleFile)).Config!.CornerRadius);
        Assert.Null(editor.SavedStyleWarning);
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, true)]
    [InlineData(WindowCloseReason.Undefined, true)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    public void OnlyACloseFromTheDialog_AsksAboutUnsavedEdits(WindowCloseReason reason, bool asks) =>
        Assert.Equal(asks, OverlayStyleEditorDialog.AsksBeforeClosing(reason));

    [Fact]
    public void TheDefaultEditedBackByHand_IsTheDefault()
    {
        using var editor = Editor();
        editor.Scale = 1.2500000001;
        editor.TextColor.Hex = "#fff";
        Later();
        editor.Scale = 1.0000000003;
        editor.TextColor.Hex = "#ffffff";

        Assert.Equal(OverlayConfig.Default, editor.Current);
        Assert.False(editor.IsDirty);
    }

    [Theory]
    [InlineData(0.1, 47.6, 1.0)]
    [InlineData(1.2345, 3.4, 40.0)]
    [InlineData(9.0, 100.0, 99.0)]
    public void WhatTheEditorMakes_RoundTripsThroughTheFile(double scale, double radius, double bars)
    {
        using var editor = Editor();
        editor.Scale = scale;
        editor.CornerRadius = radius;
        editor.Bars = bars;

        var config = editor.Current;
        Assert.InRange(config.Scale, OverlayConfig.MinScale, OverlayConfig.MaxScale);
        Assert.InRange(config.CornerRadius, 0, OverlayConfig.MaxCornerRadius);
        Assert.InRange(config.Visualizer.Bars, OverlayVisualizer.MinBars, OverlayVisualizer.MaxBars);
        Assert.Equal(config, OverlayConfigFormat.Parse(OverlayConfigFormat.ToFileJson(config)).Config);
    }

    // ---- Undo, revert ----

    [Fact]
    public void Undo_StepsBackOneEditAtATime()
    {
        using var editor = Editor();
        editor.AlignIndex = 1;
        Later();
        editor.ShowArtwork = false;

        editor.UndoCommand.Execute(null);

        Assert.True(editor.ShowArtwork);
        Assert.Equal(OverlayAlign.Right, editor.Current.Align);
        Assert.Equal(editor.Current, _overlay.Style);

        editor.UndoCommand.Execute(null);

        Assert.Equal(0, editor.AlignIndex);
        Assert.Equal(OverlayConfig.Default, _overlay.Style);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void OneDragOfASlider_IsOneUndoStep()
    {
        using var editor = Editor();
        foreach (var scale in new[] { 1.05, 1.1, 1.15, 1.2 })
        {
            _now += TimeSpan.FromMilliseconds(50);
            editor.Scale = scale;
        }

        editor.UndoCommand.Execute(null);

        Assert.Equal(1.0, editor.Scale);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void EditsToDifferentFields_AreSeparateSteps_EvenClose()
    {
        using var editor = Editor();
        editor.Scale = 1.5;
        editor.CornerRadius = 20;

        editor.UndoCommand.Execute(null);

        Assert.Equal(1.5, editor.Current.Scale);
        Assert.Equal(12, editor.Current.CornerRadius);
    }

    [Fact]
    public void RevertToSaved_GoesBackToTheFile_AndCanBeUndone()
    {
        _overlay.ImportStyle("""{"format":"rambutan-overlay","version":1,"align":"right"}""");
        using var editor = Editor();
        editor.AlignIndex = 0;
        editor.Scale = 2;

        editor.RevertToSavedCommand.Execute(null);

        Assert.Equal(_overlay.SavedStyle, editor.Current);
        Assert.Equal(1, editor.AlignIndex);
        Assert.False(editor.IsDirty);

        editor.UndoCommand.Execute(null);
        Assert.Equal(2, editor.Current.Scale);
    }

    [Fact]
    public void Closing_WithoutSaving_PutsTheSavedStyleBackOnTheOverlay()
    {
        var editor = Editor();
        editor.ShowVisualizer = false;
        Assert.False(_overlay.Style.Show.Visualizer);

        editor.Close();

        Assert.Equal(OverlayConfig.Default, _overlay.Style);
        Assert.False(File.Exists(StyleFile));
    }

    // ---- Colours ----

    [Fact]
    public void ABadHex_IsNotApplied_AndSaysSo_UntilItIsFixed()
    {
        using var editor = Editor();

        editor.AccentColor.Hex = "#12";

        Assert.NotNull(editor.AccentColor.Error);
        Assert.Equal(OverlayConfig.Default.Colors.Accent, editor.Current.Colors.Accent);
        Assert.Equal(OverlayConfig.Default, _overlay.Style);

        editor.AccentColor.Hex = "#123456";

        Assert.Null(editor.AccentColor.Error);
        Assert.Equal("#123456", editor.Current.Colors.Accent);
    }

    [Fact]
    public void Undo_ClearsAFieldsError_ByPuttingItsValueBack()
    {
        using var editor = Editor();
        editor.AccentColor.Hex = "#00FF00";
        editor.AccentColor.Hex = "nope";

        editor.UndoCommand.Execute(null);

        Assert.Null(editor.AccentColor.Error);
        Assert.Equal(OverlayConfig.Default.Colors.Accent, editor.AccentColor.Hex);
    }

    [Fact]
    public void EachColorField_HasASpokenName_ThatSaysHex()
    {
        using var editor = Editor();

        Assert.Equal(4, editor.Colors.Count);
        Assert.All(editor.Colors, c => Assert.EndsWith("color, hex", c.AccessibleName));
        Assert.Equal(4, editor.Colors.Select(c => c.AccessibleName).Distinct().Count());
    }

    [Fact]
    public void ThePicker_WritesTheHex_WithTheAlphaLast()
    {
        using var editor = Editor();

        editor.BackgroundColor.Color = Color.FromArgb(0x80, 0x11, 0x22, 0x33);

        Assert.Equal("#11223380", editor.BackgroundColor.Hex);
        Assert.Equal("#11223380", editor.Current.Colors.Background);
        Assert.Equal("#11223380", _overlay.Style.Colors.Background);
    }

    [Fact]
    public void ThePicker_LeavesAHexThatNamesItsColorAlone()
    {
        using var editor = Editor();
        editor.TextColor.Hex = "#fff";
        var steps = editor.CanUndo;
        _now += TimeSpan.FromSeconds(5);

        // What a picker does as it opens: hands back the colour it was given.
        editor.TextColor.Color = editor.TextColor.Color;

        Assert.Equal("#fff", editor.TextColor.Hex);
        Assert.Equal("#FFF", editor.Current.Colors.Text);
        Assert.Equal(steps, editor.CanUndo);
    }

    [Fact]
    public void TypingAHex_MovesThePicker()
    {
        using var editor = Editor();
        var changed = new List<string?>();
        editor.AccentColor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.AccentColor.Hex = "#00ff88";

        Assert.Contains(nameof(editor.AccentColor.Color), changed);
        Assert.Equal(Color.FromRgb(0x00, 0xFF, 0x88), editor.AccentColor.Color);
    }

    [Fact]
    public void OneDragAcrossThePicker_IsOneUndoStep()
    {
        using var editor = Editor();
        foreach (var green in new byte[] { 0x10, 0x40, 0x80, 0xC0 })
        {
            _now += TimeSpan.FromMilliseconds(30);
            editor.AccentColor.Color = Color.FromRgb(0x20, green, 0x30);
        }

        Assert.Equal("#20C030", editor.Current.Colors.Accent);
        editor.UndoCommand.Execute(null);

        Assert.Equal(OverlayConfig.Default.Colors.Accent, editor.Current.Colors.Accent);
        Assert.False(editor.CanUndo);
    }

    // ---- The preview ----

    [Theory]
    [InlineData(1.0, 560)]
    [InlineData(0.5, 560)]
    [InlineData(2.0, 284)]   // 600 / 2 - 16: the source is full
    public void ThePreviewCard_IsAsWideAsThePagesCard(double scale, double expected)
    {
        using var editor = Editor();
        editor.Scale = scale;

        Assert.Equal(expected, editor.CardWidth, 3);
        Assert.Equal(600 / scale, editor.InnerWidth, 3);
    }

    [Fact]
    public void RightAligned_PutsTheCoverOnTheRight()
    {
        using var editor = Editor();
        Assert.Equal(0, editor.CoverColumn);

        editor.AlignIndex = 1;

        Assert.Equal(2, editor.CoverColumn);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, editor.CardAlignment);
        Assert.Equal(TextAlignment.Right, editor.TextAlign);
    }

    [Fact]
    public void ThePreviewsIdleState_FollowsWhenIdle()
    {
        using var editor = Editor();
        editor.PreviewIdle = true;
        Assert.True(editor.ShowsIdleCard);
        Assert.False(editor.ShowsHiddenNote);

        editor.IdleIndex = 1;

        Assert.False(editor.ShowsIdleCard);
        Assert.True(editor.ShowsHiddenNote);
        Assert.False(editor.ShowsTrack);
    }

    [Fact]
    public void NothingPlaying_DrawsStillSampleBars()
    {
        using var editor = Editor(new AppearanceSettings());

        Assert.False(editor.LiveBars);
        Assert.True(editor.StillBars);
        Assert.Null(editor.MotionNote);
    }

    [Fact]
    public void ReducedMotion_StillsTheBars_AndSaysWhy()
    {
        var appearance = new AppearanceSettings { Motion = MotionPreference.Reduced };
        using var editor = Editor(appearance);

        Assert.False(editor.LiveBars);
        Assert.True(editor.StillBars);
        Assert.NotNull(editor.MotionNote);

        editor.ShowVisualizer = false;
        Assert.False(editor.StillBars);
        Assert.Null(editor.MotionNote);
    }

    [Fact]
    public void ThePreviewColors_ReadTheStyleAsCss()
    {
        using var editor = Editor();
        editor.BackgroundColor.Hex = "#11223380";

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(editor.BackgroundBrush);
        Assert.Equal(Color.FromArgb(0x80, 0x11, 0x22, 0x33), brush.Color);
    }

    // ---- Bars ----

    [Fact]
    public void OverlayBars_At32_UseEveryBand_TheTopQuarterToo()
    {
        var bands = new float[32];
        bands[31] = 0.7f;
        var bars = new float[32];

        VisualizerControl.GroupOverlayBands(bands, bars);

        Assert.Equal(0.7f, bars[31]);
    }

    [Fact]
    public void OverlayBars_BelowThe32_GroupAsTheMeterDoes()
    {
        var bands = new float[32];
        bands[31] = 0.7f;
        bands[0] = 0.4f;
        var bars = new float[8];

        VisualizerControl.GroupOverlayBands(bands, bars);

        Assert.Equal(0.4f, bars[0]);
        Assert.All(bars.Skip(1), level => Assert.Equal(0f, level));
    }

    [Theory]
    [InlineData(600, 32, 4)]
    [InlineData(100, 32, 2)]
    [InlineData(240, 20, 3)]
    public void OverlayGap_IsAQuarterSlot_From2To4(double width, int count, double expected) =>
        Assert.Equal(expected, VisualizerControl.OverlayGap(width, count));

    [Fact]
    public void TheStillSample_HasABarForEachBarAsked()
    {
        Assert.Equal(32, OverlayBarsSample.Levels(32).Length);
        Assert.Equal(OverlayBarsSample.SampleLevels, OverlayBarsSample.Levels(32));
        Assert.Equal(4, OverlayBarsSample.Levels(4).Length);
        Assert.All(OverlayBarsSample.Levels(4), level => Assert.InRange(level, 0.01f, 1f));
    }

    // ---- The dialog ----

    [Fact]
    public void TheDialog_NamesEveryInput_ForScreenReaders() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        using var editor = Editor();
        var dialog = new OverlayStyleEditorDialog(editor);
        dialog.Show();
        dialog.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        static string? Spoken(Control c) =>
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName();

        var inputs = dialog.GetVisualDescendants()
            .Where(v => v is Slider or ToggleSwitch or ComboBox or TextBox or CheckBox or Button)
            .Cast<Control>()
            .Where(c => c.IsEffectivelyVisible && c.TemplatedParent is null)
            .ToArray();

        Assert.Equal(3, inputs.OfType<Slider>().Count());
        Assert.Equal(4, inputs.OfType<TextBox>().Count());
        Assert.Empty(inputs.Where(c => string.IsNullOrWhiteSpace(Spoken(c)))
            .Select(c => $"{c.GetType().Name} {c.Name}"));
        Assert.Contains("Accent (bars, progress) color, hex", inputs.OfType<TextBox>().Select(Spoken));
        Assert.Contains("Choose the Card background color", inputs.OfType<Button>().Select(Spoken));
        dialog.Close();
    });

    [Fact]
    public void ASwatchsPicker_OpensFromTheKeyboard_OnItsSpectrum_AndEscapeClosesOnlyIt() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        using var editor = Editor();
        var dialog = new OverlayStyleEditorDialog(editor);
        dialog.Show();
        dialog.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var swatch = dialog.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == editor.AccentColor.PickerName);
        swatch.Focus(Avalonia.Input.NavigationMethod.Tab);
        dialog.KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None,
            Avalonia.Input.PhysicalKey.Space, " ");
        dialog.KeyRelease(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None,
            Avalonia.Input.PhysicalKey.Space, " ");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var flyout = (Flyout)swatch.Flyout!;
        Assert.True(flyout.IsOpen);
        var spectrum = ((Panel)flyout.Content!).Children.OfType<Avalonia.Controls.Primitives.ColorSpectrum>().Single();
        Assert.True(spectrum.IsKeyboardFocusWithin);
        // The flyout focuses it without a navigation method, so :focus-visible
        // never comes; the ring still has to say where the arrows go.
        Assert.True(spectrum.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "FocusEllipse").IsVisible);

        var focusedTop = TopLevel.GetTopLevel(spectrum)!;
        focusedTop.KeyPress(Avalonia.Input.Key.Right, Avalonia.Input.RawInputModifiers.None,
            Avalonia.Input.PhysicalKey.ArrowRight, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(OverlayConfig.Default.Colors.Accent, editor.Current.Colors.Accent);
        // Avalonia's colour names are English only; the app's strings go through i18n.
        var marker = spectrum.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_SelectionEllipsePanel");
        Assert.False(ToolTip.GetIsOpen(marker));

        // Tab stays inside the picker: three parts, then back to the spectrum.
        var parts = ((Panel)flyout.Content!).Children;
        Assert.All(parts, part => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(part))));
        for (var i = 0; i < parts.Count; i++)
            focusedTop.KeyPress(Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None,
                Avalonia.Input.PhysicalKey.Tab, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen);
        Assert.True(spectrum.IsKeyboardFocusWithin);

        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        focusedTop.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None,
            Avalonia.Input.PhysicalKey.Escape, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(flyout.IsOpen);
        Assert.False(closed);
        dialog.Close();
    });

    [Fact]
    public void Tab_ReachesEveryField_AndEscapeCloses() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        using var editor = Editor();
        var dialog = new OverlayStyleEditorDialog(editor);
        dialog.Show();
        dialog.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        editor.ShowAlbum = true;   // something to undo, so the footer's buttons are live
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var first = dialog.GetVisualDescendants().OfType<CheckBox>().First();
        first.Focus(Avalonia.Input.NavigationMethod.Tab);
        var reached = new List<Control>();
        for (var i = 0; i < 80; i++)
        {
            if (dialog.FocusManager?.GetFocusedElement() is Control focused && !reached.Contains(focused))
                reached.Add(focused);
            dialog.KeyPress(Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None,
                Avalonia.Input.PhysicalKey.Tab, null);
        }

        // A control's own template part (a slider's thumb) counts as the control.
        bool Reached(Control c) => reached.Any(r => ReferenceEquals(r, c) || r.GetVisualAncestors().Contains(c));
        var fields = dialog.GetVisualDescendants()
            .Where(v => v is CheckBox or ComboBox or Slider or TextBox or ToggleSwitch or Button)
            .Cast<Control>()
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.TemplatedParent is null)
            .ToArray();
        Assert.Empty(fields.Where(c => !Reached(c)).Select(c => $"{c.GetType().Name} {AutomationProperties.GetName(c)}"));

        // Esc is the Close button; with the edit unsaved it asks, so save first.
        editor.SaveCommand.Execute(null);
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None,
            Avalonia.Input.PhysicalKey.Escape, null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
    });

    /// <summary>The window's Ctrl+Z binding runs before a focused text box sees the
    /// key, and the undo's reload clears the box's own history.</summary>
    [Fact]
    public void CtrlZ_InAHexField_IsTheEditorsUndo() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        using var editor = Editor();
        var dialog = new OverlayStyleEditorDialog(editor);
        dialog.Show();
        dialog.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var accent = dialog.GetVisualDescendants().OfType<TextBox>()
            .Single(t => AutomationProperties.GetName(t) == "Accent (bars, progress) color, hex");
        accent.Focus();
        accent.SelectAll();
        dialog.KeyTextInput("#00ff88");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("#00FF88", editor.Current.Colors.Accent);
        // The latest step is elsewhere; the text box's own undo would revert the colour instead.
        var album = !editor.ShowAlbum;
        editor.ShowAlbum = album;

        void CtrlZ()
        {
            dialog.KeyPress(Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control,
                Avalonia.Input.PhysicalKey.Z, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        // One editor step at a time, latest first, with the hex field still focused.
        CtrlZ();
        Assert.Equal(!album, editor.ShowAlbum);
        Assert.Equal("#00FF88", editor.Current.Colors.Accent);
        CtrlZ();
        Assert.Equal(OverlayConfig.Default.Colors.Accent, editor.Current.Colors.Accent);
        Assert.Equal(OverlayConfig.Default.Colors.Accent, accent.Text);
        Assert.False(editor.CanUndo);
        dialog.Close();
    });

    [Fact]
    public void TheDialog_DrawsStillBars_WhenMotionIsReduced() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        using var editor = Editor(new AppearanceSettings { Motion = MotionPreference.Reduced });
        var dialog = new OverlayStyleEditorDialog(editor);
        dialog.Show();
        dialog.UpdateLayout();

        var still = dialog.GetVisualDescendants().OfType<OverlayBarsSample>().Single();
        var live = dialog.GetVisualDescendants().OfType<VisualizerControl>().Single();
        Assert.True(still.IsEffectivelyVisible);
        Assert.False(live.IsVisible);
        Assert.False(live.IsActive);
        dialog.Close();
    });
}
