using System.Drawing;

// Themes describe appearance only. App visibility, clock choices and saved scenes stay independent.
internal sealed record ThemeDefinition(
    string Id,
    string Name,
    string Description,
    int Style,
    int Color,
    bool AutoContrast,
    bool ContextStyle,
    int MaximizedStyle,
    Color PreviewBase,
    Color PreviewTint,
    Color PreviewAccent);

internal static class ThemeCatalog
{
    static readonly List<ThemeDefinition> themes =
    [
        new("glass", "Glass", "Cool smoked glass", 4, unchecked((int)0xA02A405A), false, true, 2,
            Color.FromArgb(39, 56, 78), Color.FromArgb(134, 165, 197), Color.FromArgb(154, 198, 246)),
        new("glacier", "Glacier", "Soft frosted glass", 3, unchecked((int)0xB05D7895), false, true, 2,
            Color.FromArgb(70, 91, 116), Color.FromArgb(169, 192, 215), Color.FromArgb(217, 235, 250)),
        new("ink", "Ink", "Dark, crisp gray", 2, unchecked((int)0xE8172435), false, false, 2,
            Color.FromArgb(26, 35, 49), Color.FromArgb(55, 72, 95), Color.FromArgb(176, 204, 239)),
        new("adaptive", "Adaptive", "Adapts to Windows", 3, unchecked((int)0xA02A405A), true, true, 2,
            Color.FromArgb(42, 58, 83), Color.FromArgb(91, 124, 169), Color.FromArgb(183, 214, 255)),
    ];

    public static IReadOnlyList<ThemeDefinition> All => themes;

    public static bool RegisterShared(ThemeDefinition theme)
    {
        if (!theme.Id.StartsWith("shared-", StringComparison.Ordinal)) return false;
        var index = themes.FindIndex(item => item.Id == theme.Id);
        if (index >= 0) { themes[index] = theme; return true; }
        if (themes.Count >= 20) return false;
        themes.Add(theme);
        return true;
    }

    public static ThemeDefinition? Find(string? id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));

    public static ThemeDefinition? Match(Settings settings) => All.FirstOrDefault(theme =>
        !settings.AppAccent &&
        settings.Style == theme.Style &&
        settings.Color == theme.Color &&
        settings.AutoContrast == theme.AutoContrast &&
        settings.ContextStyle == theme.ContextStyle &&
        settings.MaximizedStyle == theme.MaximizedStyle);

    public static void ApplyTo(Settings settings, ThemeDefinition theme)
    {
        settings.Style = theme.Style;
        settings.Color = theme.Color;
        settings.AutoContrast = theme.AutoContrast;
        settings.AppAccent = false;
        settings.ContextStyle = theme.ContextStyle;
        settings.MaximizedStyle = theme.MaximizedStyle;
    }
}
