using System.Text.Json;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.LocalApi.Tests;

/// <summary>The overlay style's file format: versioned from the first
/// release, lenient about values, strict about what it is.</summary>
public class OverlayConfigTests
{
    [Fact]
    public void TheDefault_SurvivesAFileRoundTrip()
    {
        var json = OverlayConfigFormat.ToFileJson(OverlayConfig.Default);
        var result = OverlayConfigFormat.Parse(json);

        Assert.True(result.Ok);
        Assert.Equal(OverlayConfig.Default, result.Config);
    }

    [Fact]
    public void ACustomStyle_SurvivesAFileRoundTrip()
    {
        var custom = new OverlayConfig
        {
            Show = new OverlayShow { Artwork = false, Album = true, Times = false, Visualizer = false },
            WhenIdle = OverlayIdle.Hide,
            Align = OverlayAlign.Right,
            Scale = 1.5,
            CornerRadius = 0,
            Colors = new OverlayColors { Text = "#112233", SecondaryText = "#ABC", Accent = "#00FF00CC", Background = "#0000" },
            Visualizer = new OverlayVisualizer { Bars = 12, Peaks = false },
        };

        var result = OverlayConfigFormat.Parse(OverlayConfigFormat.ToFileJson(custom));

        Assert.Equal(custom, result.Config);
    }

    [Fact]
    public void TheFile_SaysWhatItIsAndItsVersion_InCamelCase()
    {
        var root = JsonDocument.Parse(OverlayConfigFormat.ToFileJson(OverlayConfig.Default)).RootElement;

        Assert.Equal("rambutan-overlay", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("message", root.GetProperty("whenIdle").GetString());
        Assert.Equal("left", root.GetProperty("align").GetString());
        Assert.True(root.GetProperty("show").GetProperty("visualizer").GetBoolean());
        Assert.Equal("#FF4E45", root.GetProperty("colors").GetProperty("accent").GetString());
        Assert.Equal(32, root.GetProperty("visualizer").GetProperty("bars").GetInt32());
    }

    [Fact]
    public void TheWireForm_IsOneLine()
    {
        // It travels as one server-sent event's data line.
        Assert.DoesNotContain('\n', OverlayConfigFormat.ToWireJson(OverlayConfig.Default));
    }

    [Fact]
    public void AMinimalDocument_TakesTheDefaults()
    {
        var result = OverlayConfigFormat.Parse("""{"format":"rambutan-overlay","version":1}""");

        Assert.Equal(OverlayConfig.Default, result.Config);
    }

    [Fact]
    public void AHandEditedFile_WithCommentsAndTrailingCommas_IsRead()
    {
        var result = OverlayConfigFormat.Parse("""
            {
              // my stream's look
              "format": "rambutan-overlay",
              "version": 1,
              "align": "Right",
              "show": { "album": true, },
            }
            """);

        Assert.True(result.Ok);
        Assert.Equal(OverlayAlign.Right, result.Config!.Align);
        Assert.True(result.Config.Show.Album);
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        var result = OverlayConfigFormat.Parse(
            """{"format":"rambutan-overlay","version":1,"sparkles":true,"show":{"lyrics":true}}""");

        Assert.Equal(OverlayConfig.Default, result.Config);
    }

    [Fact]
    public void WrongValues_TakeTheirDefaults_AndNumbersAreClamped()
    {
        var result = OverlayConfigFormat.Parse("""
            {
              "format": "rambutan-overlay", "version": 1,
              "scale": 40, "cornerRadius": -3, "align": "middle", "whenIdle": 1,
              "show": { "artwork": "yes", "progress": null },
              "colors": { "text": "red", "accent": "#12345", "background": "url(x)", "secondaryText": "#abcdef" },
              "visualizer": { "bars": 2, "peaks": 0 }
            }
            """);

        var config = result.Config!;
        Assert.Equal(OverlayConfig.MaxScale, config.Scale);
        Assert.Equal(0, config.CornerRadius);
        Assert.Equal(OverlayAlign.Left, config.Align);
        Assert.Equal(OverlayIdle.Message, config.WhenIdle);
        Assert.True(config.Show.Artwork);
        Assert.True(config.Show.Progress);
        Assert.Equal("#FFFFFF", config.Colors.Text);
        Assert.Equal("#FF4E45", config.Colors.Accent);
        Assert.Equal("#000000A6", config.Colors.Background);
        Assert.Equal("#ABCDEF", config.Colors.SecondaryText);
        Assert.Equal(OverlayVisualizer.MinBars, config.Visualizer.Bars);
        Assert.True(config.Visualizer.Peaks);
    }

    [Fact]
    public void ANumericEnum_IsNotTakenForAName()
    {
        var result = OverlayConfigFormat.Parse("""{"format":"rambutan-overlay","version":1,"align":"1"}""");

        Assert.Equal(OverlayAlign.Left, result.Config!.Align);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"rambutan-overlay\"")]
    public void NotJsonOrNotAnObject_IsRefused(string text) =>
        Assert.Equal(OverlayConfigProblem.NotJson, OverlayConfigFormat.Parse(text).Problem);

    [Fact]
    public void A_style_padded_past_the_cap_is_too_large_not_invalid()
    {
        var padded = """{"format":"rambutan-overlay","version":1}""";
        padded += new string(' ', OverlayConfigFormat.MaxBytes - padded.Length);
        Assert.True(OverlayConfigFormat.Parse(padded).Ok);

        Assert.Equal(OverlayConfigProblem.TooLarge, OverlayConfigFormat.Parse(padded + " ").Problem);
        Assert.Equal(OverlayConfigProblem.TooLarge, OverlayConfigFormat.Parse(new string('x', OverlayConfigFormat.MaxBytes + 1)).Problem);
    }

    [Fact]
    public void The_cap_counts_UTF8_bytes()
    {
        // "é" is two bytes, so this is under the cap in characters and over it in bytes.
        var text = "{\"format\":\"rambutan-overlay\",\"version\":1,\"x\":\""
            + new string('é', OverlayConfigFormat.MaxBytes / 2) + "\"}";

        Assert.True(text.Length < OverlayConfigFormat.MaxBytes);
        Assert.Equal(OverlayConfigProblem.TooLarge, OverlayConfigFormat.Parse(text).Problem);
    }

    [Theory]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"format":"something-else","version":1}""")]
    [InlineData("""{"format":"rambutan-overlay"}""")]
    [InlineData("""{"format":"rambutan-overlay","version":0}""")]
    [InlineData("""{"format":"rambutan-overlay","version":"1"}""")]
    [InlineData("""{"format":"rambutan-overlay","version":1.5}""")]
    public void ADocumentThatIsNotAStyle_IsRefused(string text)
    {
        var result = OverlayConfigFormat.Parse(text);

        Assert.Equal(OverlayConfigProblem.NotAnOverlayStyle, result.Problem);
        Assert.Null(result.Config);
    }

    [Fact]
    public void ANewerVersion_IsRefused_NotGuessedAt()
    {
        var result = OverlayConfigFormat.Parse($$"""{"format":"rambutan-overlay","version":{{OverlayConfig.CurrentVersion + 1}}}""");

        Assert.Equal(OverlayConfigProblem.NewerVersion, result.Problem);
        Assert.Null(result.Config);
    }

    [Theory]
    [InlineData("#fff", true)]
    [InlineData("#ffff", true)]
    [InlineData("#a1b2c3", true)]
    [InlineData("#a1b2c3d4", true)]
    [InlineData("#a1b2c", false)]
    [InlineData("fff", false)]
    [InlineData("#ggg", false)]
    [InlineData("#fff;background:url(x)", false)]
    [InlineData(null, false)]
    public void Colours_AreHexOnly(string? text, bool valid) =>
        Assert.Equal(valid, OverlayConfigFormat.IsHexColor(text));
}
