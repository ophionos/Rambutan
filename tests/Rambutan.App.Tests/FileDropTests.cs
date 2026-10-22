using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.LocalLibrary;

namespace Rambutan.App.Tests;

/// <summary>Files dragged onto the main window: the hint says what a drop does
/// there, and the drop plays them or adds them to the queue.</summary>
public sealed class FileDropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-drop-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileIndex _index;
    private readonly List<IReadOnlyList<TrackInfo>> _played = [];
    private readonly List<IReadOnlyList<TrackInfo>> _queued = [];

    public FileDropTests()
    {
        Directory.CreateDirectory(_dir);
        _index = new LocalFileIndex(Path.Combine(_dir, "data"), new TitleReader(), _ => Task.CompletedTask, () => []);
    }

    public void Dispose()
    {
        _index.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private MainWindow Show()
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var toasts = new ToastHostViewModel(new ManualClock().Schedule);
        var vm = new MainViewModel
        {
            Opener = new LocalFileOpener(_index,
                tracks => { _played.Add(tracks); return Task.CompletedTask; }, _queued.Add, toasts),
            IsQueueOpen = true,
        };
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Part<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Point CentreOf(Window window, string name)
    {
        var part = Part<Border>(window, name);
        return part.TranslatePoint(new Point(part.Bounds.Width / 2, part.Bounds.Height / 2), window)!.Value;
    }

    private DataTransfer Files(Window window, params string[] names)
    {
        var data = new DataTransfer();
        foreach (var name in names)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, Path.GetFileNameWithoutExtension(name));
            var file = window.StorageProvider.TryGetFileFromPathAsync(path).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("no storage item for the file");
            data.Add(DataTransferItem.CreateFile(file));
        }
        return data;
    }

    private static void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Files_dropped_on_the_page_play_and_on_the_queue_are_queued() => Headless.Run(() =>
    {
        var window = Show();
        var page = CentreOf(window, "ContentPanel");
        var queue = CentreOf(window, "QueuePane");
        var hint = Part<Border>(window, "DropHint");
        var hintText = Part<TextBlock>(window, "DropHintText");
        Assert.False(hint.IsVisible);

        var data = Files(window, "a.mp3", "b.mp3");
        window.DragDrop(page, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        window.DragDrop(page, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        Assert.True(hint.IsVisible);
        Assert.Equal("Drop to play", hintText.Text);

        window.DragDrop(queue, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        Assert.Equal("Drop to add to the queue", hintText.Text);

        window.DragDrop(page, RawDragEventType.Drop, data, DragDropEffects.Copy);
        Assert.False(hint.IsVisible);
        WaitFor(() => _played.Count > 0);
        Assert.Equal(["a", "b"], Assert.Single(_played).Select(t => t.Title));

        var more = Files(window, "c.mp3");
        window.DragDrop(queue, RawDragEventType.DragEnter, more, DragDropEffects.Copy);
        window.DragDrop(queue, RawDragEventType.Drop, more, DragDropEffects.Copy);
        WaitFor(() => _queued.Count > 0);
        Assert.Equal(["c"], Assert.Single(_queued).Select(t => t.Title));
        Assert.Single(_played);
    });

    [Fact]
    public void The_hint_goes_when_the_drag_leaves_the_window() => Headless.Run(() =>
    {
        var window = Show();
        var page = CentreOf(window, "ContentPanel");
        var data = Files(window, "a.mp3");
        window.DragDrop(page, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        window.DragDrop(page, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        Assert.True(Part<Border>(window, "DropHint").IsVisible);

        window.DragDrop(default, RawDragEventType.DragLeave, data, DragDropEffects.None);

        Assert.False(Part<Border>(window, "DropHint").IsVisible);
        Assert.Empty(_played);
    });

    [Fact]
    public void Text_dragged_over_the_window_is_refused_and_shows_no_hint() => Headless.Run(() =>
    {
        var window = Show();
        var page = CentreOf(window, "ContentPanel");
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("not a file"));

        window.DragDrop(page, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        window.DragDrop(page, RawDragEventType.DragOver, data, DragDropEffects.Copy);

        Assert.False(Part<Border>(window, "DropHint").IsVisible);
    });

    private sealed class TitleReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path) => new("mp3", Title: File.ReadAllText(path));
    }
}
