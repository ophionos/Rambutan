using Avalonia.Controls;
using Avalonia.Media;
using Rambutan.Core.Appearance;

namespace Rambutan.App.Services;

/// <summary>
/// Puts an <see cref="AccentPalette"/> into the app's resources (Tokens.axaml's
/// accent keys), on the UI thread.
///
/// The accent brushes are changed in place: the styles reach them through
/// StaticResource, which holds the brush itself, so a new colour on the same
/// brush shows everywhere at once. The Color entries (the Fluent ramp, which
/// FluentTheme's own brushes read through DynamicResource) are replaced in
/// <paramref name="resources"/>, which overrides the merged Tokens dictionary.
/// </summary>
public static class AccentResources
{
    private const byte DisabledFillAlpha = 0x59;
    private const byte DisabledTextAlpha = 0x8C;

    public static void Apply(IResourceDictionary resources, AccentPalette palette)
    {
        resources["Token.Color.Accent"] = ToColor(palette.Base);
        resources["SystemAccentColor"] = ToColor(palette.Base);
        resources["SystemAccentColorLight1"] = ToColor(palette.Light1);
        resources["SystemAccentColorLight2"] = ToColor(palette.Light2);
        resources["SystemAccentColorLight3"] = ToColor(palette.Light3);
        resources["SystemAccentColorDark1"] = ToColor(palette.Dark1);
        resources["SystemAccentColorDark2"] = ToColor(palette.Dark2);
        resources["SystemAccentColorDark3"] = ToColor(palette.Dark3);

        Paint(resources, "Token.Brush.Accent", ToColor(palette.Base));
        Paint(resources, "AccentButtonBackground", ToColor(palette.Base));
        Paint(resources, "AccentButtonBackgroundPointerOver", ToColor(palette.Hover));
        Paint(resources, "AccentButtonBackgroundPressed", ToColor(palette.Pressed));
        Paint(resources, "AccentButtonBackgroundDisabled", ToColor(palette.Base, DisabledFillAlpha));
        Paint(resources, "Token.Brush.OnAccent", ToColor(palette.OnAccent));
        Paint(resources, "AccentButtonForeground", ToColor(palette.OnAccent));
        Paint(resources, "AccentButtonForegroundPointerOver", ToColor(palette.OnAccent));
        Paint(resources, "AccentButtonForegroundPressed", ToColor(palette.OnAccent));
        Paint(resources, "AccentButtonForegroundDisabled", ToColor(palette.OnAccent, DisabledTextAlpha));
    }

    private static void Paint(IResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetResource(key, null, out var value) && value is SolidColorBrush brush)
            brush.Color = color;
    }

    private static Color ToColor(Rgb c, byte alpha = 0xFF) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
