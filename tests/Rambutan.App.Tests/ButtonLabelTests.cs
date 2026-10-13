using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Rambutan.App.Tests;

/// <summary>
/// A button's text label is semibold. The presenter makes an AccessText, not a
/// TextBlock, for a string Content, and its logical parent is the button, so a
/// selector for a TextBlock under the presenter misses it.
/// </summary>
public class ButtonLabelTests
{
    [Fact]
    public void AStringLabel_IsSemibold_OnAButtonAndAToggle() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var button = new Button { Content = "Subscribe" };
        var toggle = new ToggleButton { Content = "Details" };
        var window = new Window { Content = new StackPanel { Children = { button, toggle } } };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(FontWeight.SemiBold, Label(button).FontWeight);
        Assert.Equal(FontWeight.SemiBold, Label(toggle).FontWeight);
        window.Close();
    });

    [Fact]
    public void ACardsOwnText_KeepsItsWeight() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var text = new TextBlock { Text = "Discovery" };
        var card = new Button { Classes = { "card" }, Content = new StackPanel { Children = { text } } };
        var window = new Window { Content = card };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(FontWeight.Normal, text.FontWeight);
        window.Close();
    });

    private static TextBlock Label(Control button) =>
        button.GetVisualDescendants().OfType<TextBlock>().Single();
}
