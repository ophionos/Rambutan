using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Views;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>A card's texts sit at the same heights whatever the title's length,
/// so a shelf's subtitles line up.</summary>
public class EntryCardLayoutTests
{
    private const string LongTitle = "A title that is long enough to need a second line on a card";

    private static (EntryCard Card, TextBlock Title, TextBlock Subtitle) Show(string title, string? subtitle)
    {
        ShellResources.Ensure();
        var card = new EntryCard
        {
            DataContext = new AlbumEntry { Title = title, Subtitle = subtitle, BrowseId = "MPREb1" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var window = new Window { Width = 400, Height = 400, Content = card };
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("card"))
            .GetVisualDescendants().OfType<TextBlock>().ToArray();
        return (card, texts[0], texts[1]);
    }

    private static double Top(Visual visual, Visual within) => visual.TranslatePoint(default, within)!.Value.Y;

    [Fact]
    public void TheSubtitle_SitsAtTheSameHeight_UnderAOneLineTitleAndATwoLineTitle() => Headless.Run(() =>
    {
        var one = Show("Discovery", "Album • Daft Punk");
        var two = Show(LongTitle, "Album • Daft Punk");

        // The long title does take two lines, or the test shows nothing.
        Assert.True(two.Title.Bounds.Height > one.Title.TextLayout.Height + 1);
        Assert.Equal(Top(two.Subtitle, two.Card), Top(one.Subtitle, one.Card));
    });

    [Fact]
    public void ACard_IsTheSameSize_WhateverItsTexts() => Headless.Run(() =>
    {
        var plain = Show("Discovery", null);
        var full = Show(LongTitle, "A subtitle that is also long enough to need a second line of its own");

        Assert.Equal(full.Card.Bounds.Size, plain.Card.Bounds.Size);
    });
}
