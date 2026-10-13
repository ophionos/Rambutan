using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rambutan.LocalApi.Overlay;

/// <summary>
/// How the OBS overlay looks: a small JSON document the user can export,
/// share and import. It holds no token, port or path, so sharing it gives away
/// nothing. Versioned from its first release: <see cref="Version"/> changes only
/// when a field's meaning changes, and <see cref="OverlayConfigFormat.Parse"/>
/// migrates older versions. New fields with defaults keep the version.
/// </summary>
public sealed record OverlayConfig
{
    /// <summary>What <see cref="Format"/> says, so another program's JSON isn't taken for a style.</summary>
    public const string FormatName = "rambutan-overlay";

    public const int CurrentVersion = 1;

    public static readonly OverlayConfig Default = new();

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;

    public OverlayShow Show { get; init; } = new();

    /// <summary>What the overlay shows when nothing plays.</summary>
    public OverlayIdle WhenIdle { get; init; } = OverlayIdle.Message;

    public OverlayAlign Align { get; init; } = OverlayAlign.Left;

    /// <summary>Size of everything, relative to the default (<see cref="MinScale"/>..<see cref="MaxScale"/>).</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>The card's corner radius in CSS pixels, before <see cref="Scale"/>.</summary>
    public double CornerRadius { get; init; } = 12;

    public OverlayColors Colors { get; init; } = new();

    public OverlayVisualizer Visualizer { get; init; } = new();

    public const double MinScale = 0.5, MaxScale = 3;
    public const double MaxCornerRadius = 48;
}

public sealed record OverlayShow
{
    public bool Artwork { get; init; } = true;
    public bool Album { get; init; }
    public bool Progress { get; init; } = true;
    public bool Times { get; init; } = true;

    /// <summary>Spectrum bars from the analyzer's raw bands, smoothed by the page.
    /// The page leaves them out when the viewer's system asks for reduced motion.</summary>
    public bool Visualizer { get; init; } = true;
}

/// <summary>CSS hex colours: #RGB, #RGBA, #RRGGBB or #RRGGBBAA.</summary>
public sealed record OverlayColors
{
    public string Text { get; init; } = "#FFFFFF";
    public string SecondaryText { get; init; } = "#FFFFFFB3";
    public string Accent { get; init; } = "#FF4E45";
    public string Background { get; init; } = "#000000A6";
}

public sealed record OverlayVisualizer
{
    /// <summary>Bars drawn; the analyzer's bands are grouped into this many.</summary>
    public int Bars { get; init; } = 32;

    /// <summary>A thin mark that holds each bar's recent peak and falls slowly.</summary>
    public bool Peaks { get; init; } = true;

    public const int MinBars = 4, MaxBars = 32;
}

[JsonConverter(typeof(JsonStringEnumConverter<OverlayIdle>))]
public enum OverlayIdle
{
    /// <summary>A quiet "Nothing playing" card.</summary>
    [JsonStringEnumMemberName("message")] Message,

    /// <summary>Nothing at all: the source is empty until a track plays.</summary>
    [JsonStringEnumMemberName("hide")] Hide,
}

[JsonConverter(typeof(JsonStringEnumConverter<OverlayAlign>))]
public enum OverlayAlign
{
    [JsonStringEnumMemberName("left")] Left,
    [JsonStringEnumMemberName("right")] Right,
}

public enum OverlayConfigProblem
{
    None,

    /// <summary>Not JSON, or JSON that isn't an object.</summary>
    NotJson,

    /// <summary>JSON, but not an overlay style: no <c>"format": "rambutan-overlay"</c>,
    /// or no usable version.</summary>
    NotAnOverlayStyle,

    /// <summary>Made by a newer Rambutan, whose fields may mean something else.</summary>
    NewerVersion,

    /// <summary>Larger than <see cref="OverlayConfigFormat.MaxBytes"/>, so not read.</summary>
    TooLarge,
}

public sealed record OverlayConfigResult(OverlayConfig? Config, OverlayConfigProblem Problem)
{
    public bool Ok => Problem == OverlayConfigProblem.None;
}

/// <summary>
/// Reads and writes <see cref="OverlayConfig"/>. Reading is lenient where it can
/// be: unknown fields are ignored (a newer minor addition, or a typo), and a
/// value of the wrong type or out of range takes its default or is clamped, so a
/// hand-edited file with one mistake still works. What it refuses is a document
/// that isn't a style, or one from a newer version.
/// </summary>
public static class OverlayConfigFormat
{
    /// <summary>A style is a few hundred bytes; anything far larger isn't one.</summary>
    public const int MaxBytes = 64 * 1024;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        // Hand-edited files: allow what people commonly leave in them.
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    public static OverlayConfigResult Parse(string json)
    {
        if (json.Length > MaxBytes || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            return new(null, OverlayConfigProblem.TooLarge);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException)
        {
            return new(null, OverlayConfigProblem.NotJson);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(null, OverlayConfigProblem.NotJson);
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                format.GetString() != OverlayConfig.FormatName)
            {
                return new(null, OverlayConfigProblem.NotAnOverlayStyle);
            }
            if (!root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var version) || version < 1)
            {
                return new(null, OverlayConfigProblem.NotAnOverlayStyle);
            }
            if (version > OverlayConfig.CurrentVersion)
                return new(null, OverlayConfigProblem.NewerVersion);

            // Migrations go here, oldest first: when version 2 changes a field's
            // meaning, read a version-1 document's field and convert it. Version 1
            // is the first, so there is nothing to convert yet.
            return new(Read(root), OverlayConfigProblem.None);
        }
    }

    /// <summary>The style as the overlay page receives it: compact, one line.</summary>
    public static string ToWireJson(OverlayConfig config) =>
        JsonSerializer.Serialize(Current(config), OverlayWireJsonContext.Default.OverlayConfig);

    /// <summary>The style as a file: indented, for people to read and edit.</summary>
    public static string ToFileJson(OverlayConfig config) =>
        JsonSerializer.Serialize(Current(config), OverlayFileJsonContext.Default.OverlayConfig);

    /// <summary>Whatever was read, it is written as the current format.</summary>
    private static OverlayConfig Current(OverlayConfig config) =>
        config with { Format = OverlayConfig.FormatName, Version = OverlayConfig.CurrentVersion };

    private static OverlayConfig Read(JsonElement root)
    {
        var d = OverlayConfig.Default;
        var show = Object(root, "show");
        var colors = Object(root, "colors");
        var visualizer = Object(root, "visualizer");
        return new OverlayConfig
        {
            Show = new OverlayShow
            {
                Artwork = Bool(show, "artwork", d.Show.Artwork),
                Album = Bool(show, "album", d.Show.Album),
                Progress = Bool(show, "progress", d.Show.Progress),
                Times = Bool(show, "times", d.Show.Times),
                Visualizer = Bool(show, "visualizer", d.Show.Visualizer),
            },
            WhenIdle = Enum(root, "whenIdle", d.WhenIdle),
            Align = Enum(root, "align", d.Align),
            Scale = Number(root, "scale", d.Scale, OverlayConfig.MinScale, OverlayConfig.MaxScale),
            CornerRadius = Number(root, "cornerRadius", d.CornerRadius, 0, OverlayConfig.MaxCornerRadius),
            Colors = new OverlayColors
            {
                Text = Color(colors, "text", d.Colors.Text),
                SecondaryText = Color(colors, "secondaryText", d.Colors.SecondaryText),
                Accent = Color(colors, "accent", d.Colors.Accent),
                Background = Color(colors, "background", d.Colors.Background),
            },
            Visualizer = new OverlayVisualizer
            {
                Bars = (int)Math.Round(Number(visualizer, "bars", d.Visualizer.Bars,
                    OverlayVisualizer.MinBars, OverlayVisualizer.MaxBars)),
                Peaks = Bool(visualizer, "peaks", d.Visualizer.Peaks),
            },
        };
    }

    private static JsonElement? Object(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static bool Bool(JsonElement? parent, string name, bool fallback) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static double Number(JsonElement? parent, string name, double fallback, double min, double max) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Clamp(number, min, max)
            : fallback;

    private static T Enum<T>(JsonElement? parent, string name, T fallback) where T : struct, Enum
    {
        if (parent is not { } p || !p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return fallback;
        var text = value.GetString();
        // By name only: a number would parse as any value, defined or not.
        if (string.IsNullOrEmpty(text) || char.IsAsciiDigit(text[0]) || text[0] == '-')
            return fallback;
        return System.Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed
            : fallback;
    }

    private static string Color(JsonElement? parent, string name, string fallback) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        IsHexColor(value.GetString())
            ? value.GetString()!.ToUpperInvariant()
            : fallback;

    /// <summary>#RGB, #RGBA, #RRGGBB or #RRGGBBAA. Nothing else reaches the page's CSS.</summary>
    public static bool IsHexColor(string? text) =>
        text is ['#', .. var hex] && hex.Length is 3 or 4 or 6 or 8 && hex.All(char.IsAsciiHexDigit);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OverlayConfig))]
internal sealed partial class OverlayWireJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(OverlayConfig))]
internal sealed partial class OverlayFileJsonContext : JsonSerializerContext;
