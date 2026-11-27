// Scenes save a whole taskbar arrangement. They are applied only when selected or when a rule changes state.
internal sealed class SceneSnapshot
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Neue Szene";
    public string? ThemeId { get; set; }
    public int Style { get; set; }
    public int Color { get; set; }
    public bool AutoContrast { get; set; }
    public bool AppAccent { get; set; }
    public bool ContextStyle { get; set; }
    public int MaximizedStyle { get; set; } = 2;
    public int ClockLayout { get; set; }
    public bool ShowMediaRail { get; set; }
    public bool ClockOnPrimaryOnly { get; set; }
    public string? SecondaryTimeZoneId { get; set; }
    public bool HidePinnedUntilAlt { get; set; }
    public bool? AppStacksEnabled { get; set; }
    public int KeepPinnedCount { get; set; }
    public int TrayHide { get; set; }
    public bool TrayOnDemand { get; set; }
    public int[]? RightModes { get; set; } // clock, quick settings, tray icons, language: 0 always, 1 peek, 2 off
    public bool FocusLens { get; set; }
    public bool AutoHide { get; set; }
    public List<string> HiddenPrograms { get; set; } = [];
    public List<string>? VisibleStackIds { get; set; }

    public static SceneSnapshot Capture(string name, Settings settings, int[]? rightModes, bool focusLens,
        string? secondaryTimeZoneId = null, IEnumerable<string>? visibleStackIds = null)
    {
        return new SceneSnapshot
        {
            Name = NormalizeName(name),
            ThemeId = ThemeCatalog.Match(settings)?.Id,
            Style = settings.Style,
            Color = settings.Color,
            AutoContrast = settings.AutoContrast,
            AppAccent = settings.AppAccent,
            ContextStyle = settings.ContextStyle,
            MaximizedStyle = settings.MaximizedStyle,
            ClockLayout = settings.ClockLayout,
            ShowMediaRail = settings.ShowMediaRail,
            ClockOnPrimaryOnly = settings.ClockOnPrimaryOnly,
            SecondaryTimeZoneId = secondaryTimeZoneId,
            HidePinnedUntilAlt = settings.HidePinnedUntilAlt,
            AppStacksEnabled = settings.AppStacks.Enabled,
            KeepPinnedCount = settings.KeepPinnedCount,
            TrayHide = settings.TrayHide,
            TrayOnDemand = settings.TrayOnDemand,
            RightModes = NormalizeModes(rightModes),
            FocusLens = focusLens,
            AutoHide = Taskbar.AutoHide,
            HiddenPrograms = [.. (settings.HiddenPrograms ?? []).Where(Path.IsPathFullyQualified)],
            VisibleStackIds = [.. (visibleStackIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)]
        };
    }

    // Root can apply the returned options to features implemented outside Settings.
    public SceneOptions ApplyTo(Settings settings)
    {
        if (Version != 1) throw new InvalidDataException("Unbekannte Szenen-Version.");
        settings.Style = Math.Clamp(Style, 0, 4);
        settings.Color = Color;
        settings.AutoContrast = AutoContrast;
        settings.AppAccent = AppAccent;
        settings.ContextStyle = ContextStyle;
        settings.MaximizedStyle = Math.Clamp(MaximizedStyle, 0, 4);
        settings.ClockLayout = Math.Clamp(ClockLayout, 0, 2);
        settings.ShowMediaRail = ShowMediaRail;
        settings.ShowDesignClock = settings.ClockLayout != 0;
        settings.ClockOnPrimaryOnly = ClockOnPrimaryOnly;
        settings.HidePinnedUntilAlt = HidePinnedUntilAlt;
        settings.KeepPinnedCount = Math.Clamp(KeepPinnedCount, 0, 5);
        settings.TrayHide = TrayHide & 31;
        settings.TrayOnDemand = TrayOnDemand;
        settings.HiddenPrograms = (HiddenPrograms ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.CleanBackup = null;
        return new SceneOptions(NormalizeModes(RightModes), FocusLens, SecondaryTimeZoneId,
            VisibleStackIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            AppStacksEnabled, AutoHide);
    }

    public static string NormalizeName(string? name)
    {
        var value = name?.Trim() ?? "";
        return value.Length switch
        {
            0 => "Neue Szene",
            > 32 => value[..32],
            _ => value
        };
    }

    public static int[]? NormalizeModes(int[]? modes) =>
        modes is { Length: 4 } ? modes.Select(value => Math.Clamp(value, 0, 2)).ToArray() : null;
}

internal sealed record SceneOptions(int[]? RightModes, bool FocusLens, string? SecondaryTimeZoneId,
    string[]? VisibleStackIds, bool? AppStacksEnabled, bool AutoHide);
