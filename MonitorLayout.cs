// A layout belongs to one Windows display name. Missing/disconnected displays keep the global layout.
// Native tray and pinned-app overrides are stored here but must not be applied until the Explorer helper
// can map each XAML item to an unambiguous taskbar HWND.
internal sealed class MonitorLayout
{
    public int Version { get; set; } = 1;
    public string DeviceName { get; set; } = "";
    public bool AppearanceEnabled { get; set; }
    public int Style { get; set; }
    public int Color { get; set; }
    public bool AutoContrast { get; set; }
    public bool AppAccent { get; set; }
    public bool ContextStyle { get; set; }
    public int MaximizedStyle { get; set; } = 2;
    public bool ClockEnabled { get; set; }
    public int ClockLayout { get; set; }
    public string? SecondaryTimeZoneId { get; set; }
    public bool NativeVisibilityEnabled { get; set; }
    public int[]? RightModes { get; set; }
    public bool NativeStacksEnabled { get; set; }
    public List<string>? VisibleStackIds { get; set; }
    public bool NativePinnedEnabled { get; set; }
    public bool HidePinnedUntilAlt { get; set; }
    public int KeepPinnedCount { get; set; }

    public static MonitorLayout Capture(Screen screen, Settings settings)
    {
        return new MonitorLayout
        {
            DeviceName = screen.DeviceName,
            // Starting with a copy of the global values is useful for editing, but must not
            // turn them into an override until the user explicitly enables that section.
            AppearanceEnabled = false,
            Style = settings.Style,
            Color = settings.Color,
            AutoContrast = settings.AutoContrast,
            AppAccent = settings.AppAccent,
            ContextStyle = settings.ContextStyle,
            MaximizedStyle = settings.MaximizedStyle,
            ClockEnabled = false,
            ClockLayout = settings.ClockLayout,
            SecondaryTimeZoneId = settings.SecondaryTimeZoneId,
            RightModes = SceneSnapshot.NormalizeModes(settings.RightModes),
            VisibleStackIds = [.. settings.AppStacks.Groups.Where(group => group.Enabled).Select(group => group.Id.ToString("N"))],
            HidePinnedUntilAlt = settings.HidePinnedUntilAlt,
            KeepPinnedCount = settings.KeepPinnedCount
        };
    }

    public MonitorLayout Copy() => new()
    {
        Version = Version,
        DeviceName = DeviceName,
        AppearanceEnabled = AppearanceEnabled,
        Style = Style,
        Color = Color,
        AutoContrast = AutoContrast,
        AppAccent = AppAccent,
        ContextStyle = ContextStyle,
        MaximizedStyle = MaximizedStyle,
        ClockEnabled = ClockEnabled,
        ClockLayout = ClockLayout,
        SecondaryTimeZoneId = SecondaryTimeZoneId,
        NativeVisibilityEnabled = NativeVisibilityEnabled,
        RightModes = SceneSnapshot.NormalizeModes(RightModes),
        NativeStacksEnabled = NativeStacksEnabled,
        VisibleStackIds = VisibleStackIds?.ToList(),
        NativePinnedEnabled = NativePinnedEnabled,
        HidePinnedUntilAlt = HidePinnedUntilAlt,
        KeepPinnedCount = KeepPinnedCount
    };
}

internal static class MonitorLayouts
{
    public static MonitorLayout? ForScreen(Screen screen, IEnumerable<MonitorLayout>? layouts)
    {
        if (layouts == null) return null;
        // Two layouts with the same display name are ambiguous. Do not guess which one to apply.
        var matches = layouts.Where(layout => layout.Version == 1 &&
            string.Equals(layout.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static MonitorVisual Resolve(Screen screen, Settings settings, IEnumerable<MonitorLayout>? layouts,
        bool nativePerMonitorReady = false)
    {
        var layout = ForScreen(screen, layouts);
        bool appearance = nativePerMonitorReady && layout?.AppearanceEnabled == true;
        return new MonitorVisual(
            appearance ? Math.Clamp(layout!.Style, 0, 4) : settings.Style,
            appearance ? layout!.Color : settings.Color,
            appearance ? layout!.AutoContrast : settings.AutoContrast,
            appearance ? layout!.AppAccent : settings.AppAccent,
            appearance ? layout!.ContextStyle : settings.ContextStyle,
            appearance ? Math.Clamp(layout!.MaximizedStyle, 0, 4) : settings.MaximizedStyle,
            layout?.ClockEnabled == true ? Math.Clamp(layout.ClockLayout, 0, 2) : settings.ClockLayout,
            layout?.ClockEnabled == true ? layout.SecondaryTimeZoneId : settings.SecondaryTimeZoneId,
            layout?.ClockEnabled == true || (!settings.ClockOnPrimaryOnly || screen.Primary));
    }
}

internal sealed record MonitorVisual(int Style, int Color, bool AutoContrast, bool AppAccent,
    bool ContextStyle, int MaximizedStyle, int ClockLayout, string? SecondaryTimeZoneId, bool ClockAllowed);

internal readonly record struct MonitorClock(int Layout, string? SecondaryTimeZoneId, string? SecondaryTimeZoneLabel = null);
