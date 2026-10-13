using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Rambutan.App.Tests;

/// <summary>Everything that acts on a click shows the hand; text keeps its
/// I-beam and a disabled control the arrow.</summary>
public class CursorTests
{
    private static string? CursorOf(Control control)
    {
        ShellResources.Ensure();
        var window = new Window { Content = new StackPanel { Children = { control } } };
        window.Show();
        try
        {
            window.UpdateLayout();
            return control.Cursor?.ToString();
        }
        finally
        {
            window.Close();
        }
    }

    private static readonly string Hand = new Cursor(StandardCursorType.Hand).ToString();

    [Fact]
    public void ClickableControls_ShowTheHand() => Headless.Run(() =>
    {
        Assert.Equal(Hand, CursorOf(new Button { Content = "Play all" }));
        Assert.Equal(Hand, CursorOf(new Button { Classes = { "card" } }));
        Assert.Equal(Hand, CursorOf(new Button { Classes = { "link" } }));
        Assert.Equal(Hand, CursorOf(new ToggleButton()));
        Assert.Equal(Hand, CursorOf(new CheckBox()));
        Assert.Equal(Hand, CursorOf(new ComboBox()));
        Assert.Equal(Hand, CursorOf(new ListBoxItem()));
        Assert.Equal(Hand, CursorOf(new Slider()));
    });

    [Fact]
    public void TextAndDisabledControls_DontShowTheHand() => Headless.Run(() =>
    {
        Assert.NotEqual(Hand, CursorOf(new TextBox()));
        Assert.NotEqual(Hand, CursorOf(new Button { IsEnabled = false }));
        Assert.NotEqual(Hand, CursorOf(new Slider { IsEnabled = false }));
    });
}
