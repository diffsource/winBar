using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (ReleaseUpdater.LaunchStagedUpdate(args)) return;
        Debug.Assert(Taskbar.ToAbgr(unchecked((int)0x80112233)) == unchecked((int)0x80332211));
        ApplicationConfiguration.Initialize();
        if (args.Contains("--folders")) { FolderStackTaskbarLauncher.RunPopup(); return; }
        Application.Run(new MainForm(args.Contains("--tray")));
    }
}

static class Taskbar
{
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WinCompAttrData d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string? w);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string c, string? w);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out NativeRect rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint color, out bool opaqueBlend);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr res);
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("Windows.UI.Xaml.dll", CharSet = CharSet.Unicode)] static extern int InitializeXamlDiagnosticsEx(string endPoint, uint pid, string? diagDll, string tapDll, Guid tapClsid, string? initData);

    public static int ToAbgr(int argb) => (int)((uint)argb & 0xFF00FF00) | ((argb & 0xFF) << 16) | ((argb >> 16) & 0xFF);

    static IEnumerable<IntPtr> Handles()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        var h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) yield return h;
    }

    // Undocumented API (same as TranslucentTB). state: 0 off, 1 gradient, 2 transparent gradient, 3 blur, 4 acrylic.
    public static void SetAccent(int state, int abgr)
    {
        var policy = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = abgr };
        int size = Marshal.SizeOf(policy);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WinCompAttrData { Attribute = 19, Data = ptr, SizeOfData = size };
            foreach (var h in Handles()) SetWindowCompositionAttribute(h, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // Windows 11 draws the taskbar background in XAML over the window, hiding the accent above.
    // tap\TaskbarTap.dll (loaded into explorer like TranslucentTB does) fades that XAML background out.
    static readonly Guid TapClsid = new("3F0F7A5E-8E3B-4C1D-9B2A-6D1E5C7A9B42");
    static readonly string TapDll = Path.Combine(AppContext.BaseDirectory, "TaskbarTap.dll");
    static int injecting;
    static uint injectedPid;
    static long nextInject;
    static IntPtr lastTapWindow;
    static IntPtr previewReceiver, lastPreviewRegisteredWindow;
    static bool? lastBackground, lastPinned;
    static int lastTrayMask = -1;
    static int lastKeepPinned = -1;
    static ulong lastStackRanges = ulong.MaxValue;
    static int lastStackFlags = -1;
    static long nextPinnedRefresh;

    // Class name carries a version: an older TaskbarTap.dll still loaded in explorer does not know newer messages.
    static IntPtr TapWindow() => FindWindowEx(new IntPtr(-3), IntPtr.Zero, "TaskbarTweakerTap6", null); // HWND_MESSAGE
    public static IntPtr NativeTapWindow => TapWindow();
    public static bool SupportsMonitorLayouts => Environment.OSVersion.Version.Build >= 22000 && File.Exists(TapDll);

    public static void SetPreviewReceiver(IntPtr handle)
    {
        if (previewReceiver == handle) return;
        previewReceiver = handle;
        lastPreviewRegisteredWindow = IntPtr.Zero;
    }

    // hideBackground: fade XAML background; trayMask: tray parts; hidePinned: show pinned-only buttons on demand.
    public static void Tap(bool hideBackground, int trayMask, bool hidePinned, int keepPinned, ulong stackRanges = 0, int stackFlags = 0,
        bool requireHelper = false)
    {
        var w = TapWindow();
        if (w != IntPtr.Zero)
        {
            if (w != lastTapWindow) { lastTapWindow = w; lastPreviewRegisteredWindow = IntPtr.Zero; lastBackground = lastPinned = null; lastTrayMask = lastKeepPinned = lastStackFlags = -1; lastStackRanges = ulong.MaxValue; }
            if (previewReceiver != IntPtr.Zero && w != lastPreviewRegisteredWindow)
            {
                PostMessage(w, 0x8004, previewReceiver, IntPtr.Zero);
                lastPreviewRegisteredWindow = w;
            }
            if (lastBackground != hideBackground) { PostMessage(w, 0x8000, hideBackground ? 1 : 0, IntPtr.Zero); lastBackground = hideBackground; }
            if (lastTrayMask != trayMask) { PostMessage(w, 0x8001, trayMask, IntPtr.Zero); lastTrayMask = trayMask; }
            if (lastPinned != hidePinned || lastKeepPinned != keepPinned || (hidePinned && Environment.TickCount64 >= nextPinnedRefresh))
            {
                PostMessage(w, 0x8002, (keepPinned << 1) | (hidePinned ? 1 : 0), IntPtr.Zero);
                lastPinned = hidePinned;
                lastKeepPinned = keepPinned;
                nextPinnedRefresh = Environment.TickCount64 + 5000;
            }
            if (lastStackRanges != stackRanges || lastStackFlags != stackFlags)
            {
                PostMessage(w, 0x8003, (IntPtr)unchecked((long)stackRanges), (IntPtr)stackFlags);
                lastStackRanges = stackRanges;
                lastStackFlags = stackFlags;
            }
            return;
        }
        if (lastTapWindow != IntPtr.Zero) MonitorTapProtocol.Invalidate();
        lastTapWindow = IntPtr.Zero;
        lastPreviewRegisteredWindow = IntPtr.Zero;
        if ((!requireHelper && !hideBackground && trayMask == 0 && !hidePinned && stackRanges == 0) ||
            Environment.OSVersion.Version.Build < 22000 || !File.Exists(TapDll)) return;
        if (Environment.TickCount64 < nextInject || Interlocked.Exchange(ref injecting, 1) == 1) return;
        nextInject = Environment.TickCount64 + 5000; // explorer may still be starting; retry later
        GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out var pid);
        // Injected fine but no window: an old DLL from the same path is loaded. Re-injecting would only stack it up.
        if (pid == 0 || pid == injectedPid) { injecting = 0; return; }
        Task.Run(() =>
        {
            // Explorer only accepts its own endpoint names (VisualDiagConnection1, 2, ...) and rejects others with
            // ERROR_NOT_FOUND; names are reusable, so scan from 1 instead of counting up across explorer restarts.
            try
            {
                for (int i = 1; i <= 100; i++)
                {
                    int hr = InitializeXamlDiagnosticsEx($"VisualDiagConnection{i}", pid, null, TapDll, TapClsid, null);
                    if (hr == 0) injectedPid = pid;
                    if (hr != unchecked((int)0x80070490)) break;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            finally { injecting = 0; }
        });
    }

    public static void Show(bool visible) { foreach (var h in Handles()) ShowWindow(h, visible ? 5 : 0); }

    public static IEnumerable<Rectangle> VisibleBounds()
    {
        foreach (var h in Handles())
            if (IsWindowVisible(h) && GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top)
                yield return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static bool CoveredByFullscreenWindow(Rectangle bar)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == FindWindow("Shell_TrayWnd", null) || !GetWindowRect(foreground, out var r)) return false;
        var screen = Screen.FromRectangle(bar).Bounds;
        return r.Left <= screen.Left + 2 && r.Top <= screen.Top + 2 && r.Right >= screen.Right - 2 && r.Bottom >= screen.Bottom - 2;
    }

    public static bool ActiveWindowMaximized => IsZoomed(GetForegroundWindow());
    public static IntPtr ForegroundWindow => GetForegroundWindow();

    static long nextTintRead;
    static int cachedTintRgb;
    static bool tintAvailable;
    public static void InvalidateSystemTint() => nextTintRead = 0;

    public static int SystemTint(int manualColor)
    {
        if (Environment.TickCount64 >= nextTintRead)
        {
            nextTintRead = Environment.TickCount64 + 30000;
            tintAvailable = DwmGetColorizationColor(out uint accent, out _) == 0;
            if (tintAvailable)
            {
                bool light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int mode && mode != 0;
                int baseColor = light ? 0xF2F4F7 : 0x1A2232;
                int Blend(int shift)
                {
                    int a = (int)(accent >> shift) & 0xFF;
                    int b = (baseColor >> shift) & 0xFF;
                    return (a * 35 + b * 65) / 100;
                }
                cachedTintRgb = (Blend(16) << 16) | (Blend(8) << 8) | Blend(0);
            }
        }
        return tintAvailable ? (manualColor & unchecked((int)0xFF000000)) | cachedTintRgb : manualColor;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }

    static APPBARDATA Abd() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };
    public static bool AutoHide
    {
        get { var d = Abd(); return ((long)SHAppBarMessage(4, ref d) & 1) != 0; }       // ABM_GETSTATE
        set { var d = Abd(); d.lParam = value ? 1 : 2; SHAppBarMessage(10, ref d); }    // ABM_SETSTATE
    }

    public static void Notify() => SendMessageTimeout(0xFFFF, 0x1A, IntPtr.Zero, "TraySettings", 2, 1000, out _);

    public static async Task RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { /* other user's explorer */ } }
        await Task.Delay(2500);
        if (Process.GetProcessesByName("explorer").Length == 0) Process.Start("explorer.exe");
    }
}

record Tweak(string Label, string Key, string Name, int Default, string[]? Options = null)
{
    public int Get() { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) is int v ? v : Default; }
    public void Set(int v) { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue(Name, v, RegistryValueKind.DWord); }
}

class Settings
{
    public int Style { get; set; }
    public int Color { get; set; } = 0x40000000;
    public bool HideTaskbar { get; set; }
    public int TrayHide { get; set; } // bit mask, see TaskbarTap.cpp kParts
    public bool ShowDesignClock { get; set; } // compatibility with settings saved before ClockLayout existed
    public int ClockLayout { get; set; } // 0 off, 1 time, 2 time and date
    public bool ClockOnPrimaryOnly { get; set; }
    public string? SecondaryTimeZoneId { get; set; }
    public bool ShowMediaRail { get; set; }
    public bool HidePinnedUntilAlt { get; set; }
    public bool FocusLens { get; set; }
    public int KeepPinnedCount { get; set; }
    public bool TrayOnDemand { get; set; }
    public int[]? RightModes { get; set; }
    public bool AutoContrast { get; set; }
    public bool AppAccent { get; set; }
    public bool ContextStyle { get; set; }
    public int MaximizedStyle { get; set; } = 2;
    public CleanBackup? CleanBackup { get; set; }
    public List<string> HiddenPrograms { get; set; } = [];
    public List<string> PresentationHiddenPrograms { get; set; } = [];
    public List<SavedProfile?> Profiles { get; set; } = [null, null];
    public bool WelcomeSeen { get; set; }
    public bool AutoUpdates { get; set; } = true;
    public string Language { get; set; } = Localization.English;
    public AppStackConfig AppStacks { get; set; } = new();
    public List<string> FolderStackPaths { get; set; } = [];
    public List<string> SharedThemes { get; set; } = [];
    public List<SceneSnapshot> Scenes { get; set; } = [];
    public List<SceneRule> SceneRules { get; set; } = [];
    public List<WorkspaceScene> Workspaces { get; set; } = [];
    public List<MonitorLayout> MonitorLayouts { get; set; } = [];

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarTweaker");
    static string FilePath => Path.Combine(Dir, "settings.json");
    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            if (s.ShowDesignClock && s.ClockLayout == 0) s.ClockLayout = 2;
            s.ClockLayout = Math.Clamp(s.ClockLayout, 0, 2);
            s.MaximizedStyle = Math.Clamp(s.MaximizedStyle, 0, 4);
            s.KeepPinnedCount = Math.Clamp(s.KeepPinnedCount, 0, 5);
            s.RightModes = RightAreaModes.Normalize(s.RightModes, s.TrayHide, s.TrayOnDemand);
            s.AppStacks ??= new();
            s.FolderStackPaths ??= [];
            s.PresentationHiddenPrograms ??= [];
            s.SharedThemes ??= [];
            s.Language ??= Localization.English;
            s.Scenes ??= [];
            s.SceneRules ??= [];
            s.Workspaces ??= [];
            s.MonitorLayouts ??= [];
            s.Profiles ??= [null, null];
            while (s.Profiles.Count < 2) s.Profiles.Add(null);
            if (s.Profiles.Count > 2) s.Profiles.RemoveRange(2, s.Profiles.Count - 2);
            return s;
        }
        catch { return new() { RightModes = RightAreaModes.Default() }; }
    }
    public void Save() { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
}

class CleanBackup
{
    public Dictionary<string, int> Values { get; set; } = [];
    public int TrayHide { get; set; }
    public bool TrayOnDemand { get; set; }
    public int[]? RightModes { get; set; }
}

class SavedProfile
{
    public int Version { get; set; } = 1;
    public int Style { get; set; }
    public int Color { get; set; }
    public bool AutoContrast { get; set; }
    public bool AppAccent { get; set; }
    public bool ContextStyle { get; set; }
    public int MaximizedStyle { get; set; } = 2;
    public int ClockLayout { get; set; }
    public bool ClockOnPrimaryOnly { get; set; }
    public string? SecondaryTimeZoneId { get; set; }
    public bool ShowMediaRail { get; set; }
    public bool HidePinnedUntilAlt { get; set; }
    public bool FocusLens { get; set; }
    public AppStackConfig? AppStacks { get; set; }
    public int KeepPinnedCount { get; set; }
    public int TrayHide { get; set; }
    public bool TrayOnDemand { get; set; }
    public int[]? RightModes { get; set; }
    public bool AutoHide { get; set; }
    public Dictionary<string, int> Tweaks { get; set; } = [];
    public List<string> HiddenPrograms { get; set; } = [];
}

class MainForm : Form
{
    const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
    const string Taskband = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static readonly Tweak[] Tweaks =
    [
        new("Ausrichtung", Adv, "TaskbarAl", 1, ["Links", "Mitte"]),
        new("Suche", Search, "SearchboxTaskbarMode", 1, ["Ausgeblendet", "Nur Symbol", "Suchfeld", "Schaltfläche"]),
        new("Gruppieren / Beschriftung", Adv, "TaskbarGlomLevel", 0, ["Immer gruppieren, keine Beschriftung", "Wenn Taskleiste voll", "Nie gruppieren"]),
        new("Aufgabenansicht-Button", Adv, "ShowTaskViewButton", 1),
        new("Widgets", Adv, "TaskbarDa", 1),
        new("Copilot-Button", Adv, "ShowCopilotButton", 1),
        new("Chat/Teams-Button", Adv, "TaskbarMn", 1),
        new("Badges (Zähler auf Icons)", Adv, "TaskbarBadges", 1),
        new("Blinkende Apps", Adv, "TaskbarFlashing", 1),
        new("Sekunden in der Uhr", Adv, "ShowSecondsInSystemClock", 0),
        new("Desktop-anzeigen-Ecke ganz rechts", Adv, "TaskbarSd", 1),
        new("Taskleiste auf allen Monitoren", Adv, "MMTaskbarEnabled", 1),
        new("\"Task beenden\" im Rechtsklickmenü", Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 0),
    ];
    static readonly Dictionary<string, int> CleanValues = new()
    {
        ["SearchboxTaskbarMode"] = 0,
        ["ShowTaskViewButton"] = 0,
        ["TaskbarDa"] = 0,
        ["ShowCopilotButton"] = 0,
        ["TaskbarMn"] = 0,
        ["TaskbarBadges"] = 0,
        ["TaskbarFlashing"] = 0,
    };

    readonly Settings s = Settings.Load();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    readonly AltSettingsButton altButton;
    readonly DesignClock clock = new();
    readonly MediaRail mediaRail;
    StackWindowPreviewPopup? stackPreview;
    IntPtr stackPreviewBar;
    int stackPreviewSlot = -1;
    readonly CancellationTokenSource workspaceStop = new();
    readonly ProgramHider programHider = new();
    ReleaseUpdater? updater;
    readonly NotifyIcon tray;
    readonly SettingsShell settingsShell;
    readonly HashSet<string> builtPages = new(StringComparer.Ordinal);
    readonly List<SettingsIllustration> illustrations = [];
    TableLayoutPanel grid = null!;
    bool startHidden, exiting, reverting, openHotkeyWasDown, pinnedRevealed, rightRevealed, lastPinnedHidden, lastMaximized, updaterStarted;
    bool presentationMode, presentationHotkeyWasDown, folderHotkeyWasDown;
    CheckBox? presentationToggle;
    ToolStripMenuItem? presentationMenu;
    long nextProgramScan, nextVisualRefresh, nextClockRefresh, rightRevealUntil, nextStackDensity;
    int lastEffectiveTrayMask = -1;
    ulong appliedStackRanges = ulong.MaxValue;
    int appliedStackFlags = -1;
    bool stackCollapseActive = true;
    bool nativeLayoutReady;
    long nextMonitorRefresh;
    long nextSceneCheck;
    string? activeAutoSceneId;
    SceneSnapshot? beforeAutoScene;
    int lastProfileHotkey;
    void UpdateClocks() => clock.Update(screen =>
    {
        var visual = MonitorLayouts.Resolve(screen, s, s.MonitorLayouts, nativeLayoutReady);
        var overrideLayout = nativeLayoutReady ? MonitorLayouts.ForScreen(screen, s.MonitorLayouts) : null;
        var modes = overrideLayout?.NativeVisibilityEnabled == true && overrideLayout.RightModes is { Length: 4 }
            ? overrideLayout.RightModes : s.RightModes!;
        var bar = Taskbar.VisibleBounds().FirstOrDefault(bounds =>
            string.Equals(Screen.FromRectangle(bounds).DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase));
        var pointer = Cursor.Position;
        bool revealed = AltSettingsButton.AltDown ||
            (bar != Rectangle.Empty && RightAreaModes.HasPeek(modes) && NearRightArea(bar, pointer)) ||
            (rightRevealed && bar != Rectangle.Empty && InRightFlyoutArea(bar, pointer));
        var layout = !s.HideTaskbar && !presentationMode && visual.ClockAllowed &&
            RightAreaModes.ClockVisible(modes, revealed) ? visual.ClockLayout : 0;
        return new MonitorClock(layout, visual.SecondaryTimeZoneId);
    });

    bool HasNativeLayoutOverrides() => s.MonitorLayouts.Any(layout => layout.Version == 1 &&
        (layout.AppearanceEnabled || layout.NativeVisibilityEnabled || layout.NativePinnedEnabled || layout.NativeStacksEnabled));

    bool SendMonitorLayouts(bool altDown, Point pointer)
    {
        var tap = Taskbar.NativeTapWindow;
        if (tap == IntPtr.Zero) return false;
        if (!HasNativeLayoutOverrides())
        {
            MonitorTapProtocol.Send(tap, Array.Empty<MonitorTapEntry>());
            return false;
        }
        var bars = MonitorTaskbars.Enumerate();
        if (bars.Count == 0 || bars.Count > 8) return false;
        var foreground = Taskbar.ForegroundWindow;
        var foregroundScreen = foreground == IntPtr.Zero ? null : Screen.FromHandle(foreground);
        bool maximized = Taskbar.ActiveWindowMaximized;
        var entries = new List<MonitorTapEntry>(bars.Count);
        foreach (var bar in bars)
        {
            var layout = MonitorLayouts.ForScreen(bar.Screen, s.MonitorLayouts);
            var visual = MonitorLayouts.Resolve(bar.Screen, s, s.MonitorLayouts, true);
            bool activeHere = foregroundScreen != null &&
                string.Equals(foregroundScreen.DeviceName, bar.Screen.DeviceName, StringComparison.OrdinalIgnoreCase);
            int style = visual.ContextStyle && maximized && activeHere ? visual.MaximizedStyle : visual.Style;
            var modes = layout?.NativeVisibilityEnabled == true && layout.RightModes is { Length: 4 }
                ? layout.RightModes : s.RightModes!;
            bool pointerOnBar = bar.Bounds.Contains(pointer);
            bool revealRight = altDown || (RightAreaModes.HasPeek(modes) && NearRightArea(bar.Bounds, pointer)) ||
                (rightRevealed && InRightFlyoutArea(bar.Bounds, pointer));
            uint trayMask = (uint)(presentationMode ? 31 : RightAreaModes.HiddenMask(modes, revealRight));
            var stacks = s.AppStacks.Copy();
            if (layout?.NativeStacksEnabled == true)
            {
                stacks.Enabled = true;
                var visible = (layout.VisibleStackIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var group in stacks.Groups) group.Enabled = visible.Contains(group.Id.ToString("N"));
            }
            ulong spans = stacks.PackRanges();
            bool hidePinned = layout?.NativePinnedEnabled == true ? layout.HidePinnedUntilAlt && !altDown :
                s.HidePinnedUntilAlt && !pinnedRevealed;
            if (spans != 0) hidePinned = false;
            uint keepPinned = (uint)(layout?.NativePinnedEnabled == true ? Math.Clamp(layout.KeepPinnedCount, 0, 5) : s.KeepPinnedCount);
            uint flags = (uint)((style > 0 ? 1 : 0) | (hidePinned ? 2 : 0) | (altDown ? 4 : 0) |
                (pointerOnBar ? 8 : 0) | (stackCollapseActive ? 16 : 0));
            entries.Add(new MonitorTapEntry(bar.Handle, spans, trayMask, flags, keepPinned));
        }
        return MonitorTapProtocol.Send(tap, entries);
    }
    IEnumerable<string> CurrentHiddenPrograms => (s.HiddenPrograms ?? []).Concat(presentationMode ? s.PresentationHiddenPrograms ?? [] : []);

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        Localization.Configure(s.Language);
        MainSettingsTranslations.Register();
        settingsShell = new SettingsShell();
        Text = Localization.T("winBar · Settings", "winBar · Einstellungen");
        ClientSize = new Size(LogicalToDeviceUnits(960), Math.Min(LogicalToDeviceUnits(700), Screen.PrimaryScreen!.WorkingArea.Height * 9 / 10));
        MinimumSize = new Size(LogicalToDeviceUnits(740), LogicalToDeviceUnits(500));
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        Controls.Add(settingsShell);
        altButton = new AltSettingsButton(OpenWindow);
        mediaRail = new MediaRail(this);

        Action? refreshAppearance = null;
        Header("Aussehen");
        Section("Choose a theme", "Design auswählen");
        foreach (var json in s.SharedThemes.Take(16))
            try { ThemeCatalog.RegisterShared(ThemePackage.Import(json)); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { }
        var themePicker = new ThemePicker();
        themePicker.SetSelectedTheme(ThemeCatalog.Match(s)?.Id);
        themePicker.ThemeSelected += theme =>
        {
            ThemeCatalog.ApplyTo(s, theme);
            s.Save();
            refreshAppearance?.Invoke();
            RefreshIllustrations();
            nextVisualRefresh = 0;
        };
        Add(themePicker);
        var themeActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        var exportTheme = new Button { Text = "Design teilen…", AutoSize = true };
        exportTheme.Click += (_, _) =>
        {
            using var file = new SaveFileDialog { Filter = "winBar-Design (*.json)|*.json", FileName = "winbar-design.json" };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            var name = Path.GetFileNameWithoutExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(name)) name = "Eigenes Design";
            name = name[..Math.Min(name.Length, 32)];
            var color = Color.FromArgb(s.Color | unchecked((int)0xFF000000));
            var current = ThemeCatalog.Match(s) ?? new ThemeDefinition("custom", name, "Eigenes winBar-Design",
                s.Style, s.Color, s.AutoContrast, s.ContextStyle, s.MaximizedStyle, color, color, color);
            try { ThemePackage.ExportFile(file.FileName, current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show($"Design konnte nicht gespeichert werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var importTheme = new Button { Text = "Design laden…", AutoSize = true };
        importTheme.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "winBar-Design (*.json)|*.json", CheckFileExists = true };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var theme = ThemePackage.ImportFile(file.FileName);
                if (!ThemeCatalog.RegisterShared(theme)) throw new InvalidDataException("Zu viele Designs gespeichert.");
                var json = ThemePackage.Export(theme);
                s.SharedThemes.RemoveAll(saved =>
                {
                    try { return ThemePackage.Import(saved).Id == theme.Id; }
                    catch (InvalidDataException) { return false; }
                });
                s.SharedThemes.Add(json);
                ThemeCatalog.ApplyTo(s, theme);
                s.Save();
                themePicker.RefreshThemes();
                refreshAppearance?.Invoke();
                nextVisualRefresh = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { MessageBox.Show($"Design konnte nicht geladen werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        themeActions.Controls.AddRange([exportTheme, importTheme]);
        Add(themeActions);
        Section("Fine-tune your look", "Design anpassen");
        Guide(SettingsIllustrationKind.Appearance);
        var style = Combo("Stil", ["Standard", "Komplett transparent", "Farbe (getönt)", "Blur", "Acrylic"], s.Style);
        var colorBtn = new Button { FlatStyle = FlatStyle.Flat, AutoSize = true };
        colorBtn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = Color.FromArgb(s.Color | unchecked((int)0xFF000000)), FullOpen = true };
            if (dlg.ShowDialog() == DialogResult.OK) { s.Color = (s.Color & unchecked((int)0xFF000000)) | (dlg.Color.ToArgb() & 0xFFFFFF); s.Save(); ShowColor(); RefreshIllustrations(); nextVisualRefresh = 0; }
        };
        var colorLbl = Caption("Farbe");
        Add(colorBtn, colorLbl);
        var alphaLbl = Caption("");
        var alpha = new OpacitySlider { Value = (s.Color >> 24) & 0xFF };
        alpha.ValueChanged += (_, _) =>
        {
            if (reverting) return;
            s.Color = (alpha.Value << 24) | (s.Color & 0xFFFFFF);
            if (!alpha.IsDragging) s.Save();
            ShowAlpha(); RefreshIllustrations(); nextVisualRefresh = 0;
        };
        alpha.DragCompleted += (_, _) => s.Save();
        Add(alpha, alphaLbl);
        var opacityHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        Add(opacityHint);
        style.SelectedIndexChanged += (_, _) =>
        {
            if (reverting) return;
            s.Style = style.SelectedIndex;
            if (s.Style == 4) NormalizeAcrylicColor();
            s.Save(); ShowStyle(); RefreshIllustrations(); nextVisualRefresh = 0;
        };
        ShowColor(); ShowAlpha(); ShowStyle();

        void ShowColor()
        {
            var c = Color.FromArgb(s.Color | unchecked((int)0xFF000000));
            bool canChoose = s.Style >= 2 && !s.AutoContrast && !s.AppAccent;
            colorBtn.BackColor = canChoose ? c : Color.FromArgb(239, 243, 249);
            colorBtn.ForeColor = canChoose && c.GetBrightness() <= 0.5f ? Color.White : Color.FromArgb(39, 58, 83);
            colorBtn.Text = canChoose ? $"#{c.R:X2}{c.G:X2}{c.B:X2}  –  {Localization.T("change…", "ändern…")}" :
                s.Style < 2 ? Localization.T("Choose a tinted style", "Getönten Stil wählen") :
                s.AutoContrast ? Localization.T("Color follows Windows", "Farbe folgt Windows") :
                Localization.T("Color follows the active app", "Farbe folgt der aktiven App");
        }
        void ShowAlpha() => alphaLbl.Text = Localization.T("Opacity", "Deckkraft");
        void NormalizeAcrylicColor()
        {
            int color = MonitorAccent.EnsureAcrylicColor(s.Color);
            if (color == s.Color) return;
            s.Color = color;
            alpha.Value = (color >> 24) & 0xFF;
            ShowColor(); ShowAlpha();
        }
        // Only Farbe/Blur/Acrylic use the color; "transparent" always sends 0.
        void ShowStyle()
        {
            colorBtn.Enabled = colorLbl.Enabled = s.Style >= 2 && !s.AutoContrast && !s.AppAccent;
            alpha.Enabled = alphaLbl.Enabled = s.Style >= 2;
            opacityHint.Text = s.Style < 2
                ? Localization.T("Choose Tinted color, Blur or Acrylic to adjust opacity.", "Wähle Getönt, Blur oder Acrylic, um die Deckkraft einzustellen.")
                : Localization.T("Drag anywhere on the bar or use the arrow keys.", "Ziehe an einer beliebigen Stelle oder nutze die Pfeiltasten.");
            ShowColor();
        }

        var autoContrast = Check("Tönung an Windows-Akzent und hell/dunkel anpassen", s.AutoContrast);
        autoContrast.CheckedChanged += (_, _) => { if (reverting) return; s.AutoContrast = autoContrast.Checked; s.Save(); ShowStyle(); RefreshIllustrations(); nextVisualRefresh = 0; };
        var appAccent = Check("Tönung aus dem Symbol der aktiven App ableiten", s.AppAccent);
        appAccent.CheckedChanged += (_, _) => { if (reverting) return; s.AppAccent = appAccent.Checked; s.Save(); ShowStyle(); RefreshIllustrations(); nextVisualRefresh = 0; };
        Add(new Label { Text = "Bei getönten Looks. Das App-Symbol wird nur beim Programmwechsel gelesen; die Farbe bleibt dezent.", AutoSize = true, ForeColor = SystemColors.GrayText });

        var looks = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        foreach (var (label, lookStyle, lookColor) in new (string, int, int)[]
        {
            ("Clear", 1, 0),
            ("Frost", 3, unchecked((int)0xA01B2940)),
            ("Solid", 2, unchecked((int)0xE0222936)),
        })
        {
            var button = new Button { Text = label, AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
            button.Click += (_, _) =>
            {
                s.Color = lookColor;
                alpha.Value = (s.Color >> 24) & 0xFF;
                style.SelectedIndex = lookStyle;
                s.Style = lookStyle;
                s.Save();
                ShowColor(); ShowAlpha(); ShowStyle(); RefreshIllustrations();
                nextVisualRefresh = 0;
            };
            looks.Controls.Add(button);
        }
        Add(looks, Caption("Schnell-Looks"));
        var contextStyle = Check("Bei maximierten Fenstern anderen Stil verwenden", s.ContextStyle);
        var maximizedStyle = Combo("Stil bei maximiertem Fenster", ["Standard", "Transparent", "Farbe (getönt)", "Blur", "Acrylic"], s.MaximizedStyle);
        maximizedStyle.Enabled = s.ContextStyle;
        contextStyle.CheckedChanged += (_, _) => { if (reverting) return; s.ContextStyle = contextStyle.Checked; maximizedStyle.Enabled = s.ContextStyle; s.Save(); RefreshIllustrations(); nextVisualRefresh = 0; };
        maximizedStyle.SelectedIndexChanged += (_, _) =>
        {
            if (reverting) return;
            s.MaximizedStyle = maximizedStyle.SelectedIndex;
            if (s.MaximizedStyle == 4) NormalizeAcrylicColor();
            s.Save(); RefreshIllustrations(); nextVisualRefresh = 0;
        };

        Header("Verhalten");
        Guide(SettingsIllustrationKind.Taskbar);
        Section("Visibility", "Sichtbarkeit");
        var autoHide = Check("Automatisch ausblenden", Taskbar.AutoHide);
        autoHide.CheckedChanged += (_, _) => { if (!reverting) Taskbar.AutoHide = autoHide.Checked; };
        var hide = Check("Taskleiste komplett verstecken (solange App läuft)", s.HideTaskbar);
        hide.CheckedChanged += (_, _) => { if (reverting) return; s.HideTaskbar = hide.Checked; if (!hide.Checked) Taskbar.Show(true); s.Save(); };
        var auto = Check("Mit Windows starten (im Tray)", Autostart);
        auto.CheckedChanged += (_, _) => Autostart = auto.Checked;
        Section("One-click cleanup", "Aufräumen mit einem Klick");
        var cleanControls = new List<Control>();
        var refreshControls = new List<Action>();
        var cleanButton = new Button { AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        cleanButton.Click += (_, _) => ToggleClean();
        Add(cleanButton, Caption("Clean-Profil"));
        Add(new Label { Text = "Blendet Suche, Widgets, Aufgabenansicht, Badges und Tray-Symbole gemeinsam aus. Ein zweiter Klick stellt deine vorherigen Werte wieder her.", AutoSize = true, ForeColor = SystemColors.GrayText });

        Header("Uhr und Medien");
        Guide(SettingsIllustrationKind.Clock);
        Section("Clock", "Uhr");
        var clockLayout = Combo("Design-Uhr", ["Aus", "Nur Uhrzeit", "Uhrzeit und Datum"], s.ClockLayout);
        clockLayout.SelectedIndexChanged += (_, _) =>
        {
            if (reverting) return;
            s.ClockLayout = clockLayout.SelectedIndex;
            s.ShowDesignClock = s.ClockLayout != 0;
            s.Save();
            RefreshIllustrations();
            UpdateClocks();
        };
        var primaryClock = Check("Design-Uhr nur auf dem Hauptmonitor", s.ClockOnPrimaryOnly);
        primaryClock.CheckedChanged += (_, _) => { if (reverting) return; s.ClockOnPrimaryOnly = primaryClock.Checked; s.Save(); UpdateClocks(); };
        Add(new Label { Text = "Die Uhr liegt über der Windows-Uhr. Klicks gehen weiterhin an Windows.", AutoSize = true, ForeColor = SystemColors.GrayText });
        var timeZones = TimeZoneInfo.GetSystemTimeZones().ToArray();
        var timeZoneOptions = new[] { "Aus" }.Concat(timeZones.Select(zone => zone.DisplayName)).ToArray();
        int selectedZone = Array.FindIndex(timeZones, zone => zone.Id == s.SecondaryTimeZoneId) + 1;
        var secondaryZone = Combo("Zweite Zeitzone bei Hover", timeZoneOptions, selectedZone);
        secondaryZone.SelectedIndexChanged += (_, _) =>
        {
            if (reverting) return;
            s.SecondaryTimeZoneId = secondaryZone.SelectedIndex == 0 ? null : timeZones[secondaryZone.SelectedIndex - 1].Id;
            s.Save();
            UpdateClocks();
        };
        Section("Media", "Medien");
        var mediaToggle = Check("Schmale Musik-Leiste bei Wiedergabe zeigen", s.ShowMediaRail);
        mediaToggle.CheckedChanged += (_, _) => { if (reverting) return; s.ShowMediaRail = mediaToggle.Checked; s.Save(); mediaRail.Update(s.ShowMediaRail, s.HideTaskbar); };

        Header("Apps");
        Guide(SettingsIllustrationKind.Apps);
        Section("Pinned apps", "Angeheftete Apps");
        var pinned = Check("Angeheftete Apps erst mit Alt zeigen (experimentell)", s.HidePinnedUntilAlt);
        var favorites = Combo("Immer sichtbar", ["Keine", "Erstes Symbol", "Erste 2 Symbole", "Erste 3 Symbole", "Erste 4 Symbole", "Erste 5 Symbole"], s.KeepPinnedCount);
        favorites.Enabled = s.HidePinnedUntilAlt;
        pinned.CheckedChanged += (_, _) => { if (reverting) return; s.HidePinnedUntilAlt = pinned.Checked; favorites.Enabled = pinned.Checked; s.Save(); RefreshIllustrations(); nextVisualRefresh = 0; };
        favorites.SelectedIndexChanged += (_, _) => { if (reverting) return; s.KeepPinnedCount = favorites.SelectedIndex; s.Save(); RefreshIllustrations(); nextVisualRefresh = 0; };
        Add(new Label { Text = "Ordne deine Favoriten in Windows an den Anfang der angehefteten Apps. Laufende Apps bleiben sichtbar. Mit Alt erscheinen die übrigen Symbole, bis die Maus die Taskleiste verlässt.", AutoSize = true, ForeColor = SystemColors.GrayText });
        Section("Focus and privacy", "Fokus und Privatsphäre");
        var focusLens = Check("Fokus-Linse: Nur aktive App und angeheftete Favoriten zeigen", s.FocusLens);
        focusLens.CheckedChanged += (_, _) =>
        {
            if (reverting) return;
            s.FocusLens = focusLens.Checked;
            s.Save();
            RefreshIllustrations();
            programHider.SetFocusLens(s.FocusLens, AltSettingsButton.AltDown, Taskbar.ForegroundWindow);
        };
        Add(new Label { Text = "Alt zeigt alle laufenden Apps. Wenn kein App-Fenster aktiv ist, bleiben alle erreichbar.", AutoSize = true, ForeColor = SystemColors.GrayText });
        presentationToggle = Check("Bildschirmfreigabe-Modus: rechten Bereich und private Apps verbergen", false);
        presentationToggle.CheckedChanged += (_, _) => SetPresentationMode(presentationToggle.Checked);
        var choosePrivate = new Button { Text = "Private Apps auswählen…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        choosePrivate.Click += (_, _) =>
        {
            using var dialog = new HiddenProgramsDialog(s.PresentationHiddenPrograms);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            s.PresentationHiddenPrograms = dialog.SelectedPaths;
            s.Save();
            if (presentationMode) programHider.Update(CurrentHiddenPrograms);
        };
        Add(choosePrivate);
        Add(new Label { Text = "Strg+Alt+P schaltet den Modus schnell um. Alt+Tab hält alle Fenster erreichbar.", AutoSize = true, ForeColor = SystemColors.GrayText });
        Section("App stacks", "App-Stapel");
        var stackSettings = new Button { Text = "App-Stapel einrichten…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        stackSettings.Click += (_, _) =>
        {
            using var dialog = new StackSettingsDialog(s.AppStacks);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            s.AppStacks = dialog.Config;
            s.Save();
            pinned.Enabled = !s.AppStacks.Enabled;
            favorites.Enabled = !s.AppStacks.Enabled && s.HidePinnedUntilAlt;
            nextVisualRefresh = 0;
        };
        Add(stackSettings);
        Add(new Label { Text = "Bis zu vier Stapel aus benachbarten Windows-Pins. Der erste Pin öffnet den Stapel bei Hover; laufende Apps bleiben sichtbar.", AutoSize = true, ForeColor = SystemColors.GrayText });
        pinned.Enabled = !s.AppStacks.Enabled;
        favorites.Enabled = !s.AppStacks.Enabled && s.HidePinnedUntilAlt;
        Section("Folder shelf", "Ordnerstapel");
        var folderList = new ListBox { Height = LogicalToDeviceUnits(82), IntegralHeight = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        void RefreshFolders()
        {
            folderList.Items.Clear();
            foreach (var path in s.FolderStackPaths) folderList.Items.Add(path);
        }
        RefreshFolders();
        Add(folderList);
        var folderActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        var addFolder = new Button { Text = "Ordner hinzufügen…", AutoSize = true };
        addFolder.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Ordner für deinen Stapel auswählen" };
            if (dialog.ShowDialog(this) != DialogResult.OK || s.FolderStackPaths.Count >= 20 ||
                s.FolderStackPaths.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase)) return;
            s.FolderStackPaths.Add(dialog.SelectedPath);
            s.Save();
            RefreshFolders();
        };
        var removeFolder = new Button { Text = "Entfernen", AutoSize = true };
        removeFolder.Click += (_, _) =>
        {
            if (folderList.SelectedIndex < 0) return;
            s.FolderStackPaths.RemoveAt(folderList.SelectedIndex);
            s.Save();
            RefreshFolders();
        };
        var openFolders = new Button { Text = "Ordnerstapel öffnen", AutoSize = true };
        openFolders.Click += (_, _) => OpenFolderStack();
        var pinFolders = new Button { Text = "Taskleisten-Button erstellen…", AutoSize = true };
        pinFolders.Click += (_, _) =>
        {
            try
            {
                var path = FolderStackTaskbarLauncher.CreateShortcut();
                FolderStackTaskbarLauncher.RevealShortcut(path);
                MessageBox.Show(this, "Die Verknüpfung wurde erstellt. Klicke sie im Explorer mit rechts an und wähle ‚An Taskleiste anheften‘.",
                    "winBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Reflection.TargetInvocationException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { MessageBox.Show(this, $"Taskleisten-Verknüpfung konnte nicht erstellt werden:\n{ex.Message}", "winBar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        folderActions.Controls.AddRange([addFolder, removeFolder, openFolders, pinFolders]);
        Add(folderActions);
        Add(new Label { Text = "Ein kleines Fach für bis zu 20 Ordner. Strg+Alt+O öffnet es ohne Einstellungen.", AutoSize = true, ForeColor = SystemColors.GrayText });
        Section("Hidden app buttons", "Ausgeblendete App-Symbole");
        var choosePrograms = new Button { Text = "Programme auf der Taskleiste ausblenden…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        choosePrograms.Click += (_, _) =>
        {
            using var dialog = new HiddenProgramsDialog(s.HiddenPrograms ?? []);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            s.HiddenPrograms = dialog.SelectedPaths;
            s.Save();
            programHider.Update(CurrentHiddenPrograms);
        };
        Add(choosePrograms);
        Add(new Label { Text = "Fenster bleiben geöffnet; nur ihre Taskleisten-Schaltflächen werden ausgeblendet. Nicht jede App unterstützt dies.", AutoSize = true, ForeColor = SystemColors.GrayText });
        programHider.Update(CurrentHiddenPrograms);

        Header("Elemente");
        var resets = new List<Action>();
        foreach (var t in Tweaks)
        {
            if (t.Options is { } opts)
            {
                var c = Combo(t.Label, opts, t.Get());
                c.SelectedIndexChanged += (_, _) => Apply(t, c.SelectedIndex, () => c.SelectedIndex = Math.Clamp(t.Get(), 0, opts.Length - 1));
                resets.Add(() => c.SelectedIndex = Math.Clamp(t.Default, 0, opts.Length - 1));
                refreshControls.Add(() => c.SelectedIndex = Math.Clamp(t.Get(), 0, opts.Length - 1));
                if (CleanValues.ContainsKey(t.Name)) cleanControls.Add(c);
            }
            else
            {
                var c = Check(t.Label, t.Get() != 0);
                c.CheckedChanged += (_, _) => Apply(t, c.Checked ? 1 : 0, () => c.Checked = t.Get() != 0);
                resets.Add(() => c.Checked = t.Default != 0);
                refreshControls.Add(() => c.Checked = t.Get() != 0);
                if (CleanValues.ContainsKey(t.Name)) cleanControls.Add(c);
            }
        }
        Add(new Label { Text = "Manche Änderungen greifen erst nach Explorer-Neustart.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 0) });

        Header("Rechter Bereich");
        Guide(SettingsIllustrationKind.SystemArea);
        Section("Choose what stays visible", "Sichtbare Elemente wählen");
        (string Label, int Bit)[] trayParts =
        [
            ("Uhr und Datum", 1),
            ("Schnelleinstellungen (WLAN, Lautstärke, Akku)", 2),
            ("Tray-Icons und ^-Pfeil", 4),
            ("Sprachanzeige", 8),
        ];
        var rightModeControls = new ComboBox[4];
        for (int i = 0; i < trayParts.Length; i++)
        {
            int index = i;
            var c = Combo(trayParts[i].Label, ["Immer sichtbar", "Bei Alt oder Mausnähe", "Aus"], s.RightModes![i]);
            rightModeControls[i] = c;
            c.SelectedIndexChanged += (_, _) =>
            {
                if (reverting) return;
                s.RightModes![index] = c.SelectedIndex;
                s.Save();
                RefreshIllustrations();
                nextVisualRefresh = nextClockRefresh = 0;
            };
            resets.Add(() => c.SelectedIndex = RightAreaModes.Always);
            refreshControls.Add(() => c.SelectedIndex = s.RightModes![index]);
            if (i is 2 or 3) cleanControls.Add(c);
        }
        Add(new Label { Text = "Wichtige Symbole können sichtbar bleiben. Elemente auf Abruf erscheinen mit Alt oder am rechten Taskleistenrand und bleiben während der Bedienung offen.", AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(520), 0), ForeColor = SystemColors.GrayText });
        UpdateCleanUi();
        void UpdateCleanUi()
        {
            bool active = s.CleanBackup != null;
            cleanButton.Text = active
                ? Localization.T("Restore previous view", "Vorherige Ansicht wiederherstellen")
                : Localization.T("Activate clean profile", "Clean-Profil aktivieren");
            foreach (var control in cleanControls) control.Enabled = !active;
        }
        void ToggleClean()
        {
            var targets = Tweaks.Where(t => CleanValues.ContainsKey(t.Name)).ToArray();
            try
            {
                if (s.CleanBackup == null)
                {
                    var backup = new CleanBackup { TrayHide = s.TrayHide, TrayOnDemand = s.TrayOnDemand, RightModes = [.. s.RightModes!] };
                    foreach (var t in targets) backup.Values[t.Name] = t.Get();
                    try { foreach (var t in targets) t.Set(CleanValues[t.Name]); }
                    catch
                    {
                        foreach (var t in targets) try { t.Set(backup.Values[t.Name]); } catch { }
                        throw;
                    }
                    s.TrayHide |= 4 | 8;
                    s.TrayOnDemand = true;
                    s.RightModes![2] = s.RightModes[3] = RightAreaModes.Peek;
                    s.CleanBackup = backup;
                }
                else
                {
                    foreach (var t in targets)
                        if (s.CleanBackup.Values.TryGetValue(t.Name, out int value)) t.Set(value);
                    s.TrayHide = (s.TrayHide & ~(4 | 8)) | (s.CleanBackup.TrayHide & (4 | 8));
                    s.TrayOnDemand = s.CleanBackup.TrayOnDemand;
                    s.RightModes = RightAreaModes.Normalize(s.CleanBackup.RightModes, s.TrayHide, s.TrayOnDemand);
                    s.CleanBackup = null;
                }
                Taskbar.Notify();
                s.Save();
                reverting = true;
                foreach (var refresh in refreshControls) refresh();
                reverting = false;
                UpdateCleanUi(); RefreshIllustrations();
                nextVisualRefresh = 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                reverting = false;
                MessageBox.Show($"Clean-Profil konnte nicht angewendet werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        Add(new Label { Text = "Greift direkt in die Windows-Oberfläche ein und kann nach Windows-Updates nicht mehr funktionieren. Nach einem App-Update einmal Explorer neu starten.", AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(520), 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 0) });

        Header("Profile");
        Guide(SettingsIllustrationKind.Layouts);
        var profileSlot = Combo("Profil", ["Arbeit", "Privat"], 0);
        var profileStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        Add(profileStatus);
        void ShowProfileStatus() => profileStatus.Text = s.Profiles[profileSlot.SelectedIndex] == null
            ? Localization.T("Nothing saved in this slot yet.", "Noch kein Profil in diesem Platz gespeichert.")
            : Localization.T("Saved · Ctrl+Alt+1 for Work, Ctrl+Alt+2 for Personal", "Gespeichert · Strg+Alt+1 für Arbeit, Strg+Alt+2 für Privat");
        profileSlot.SelectedIndexChanged += (_, _) => ShowProfileStatus();
        ShowProfileStatus();
        var profileButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        var saveProfile = new Button { Text = "Aktuelle Ansicht speichern", AutoSize = true };
        var applyProfile = new Button { Text = "Anwenden", AutoSize = true };
        var exportProfile = new Button { Text = "Exportieren…", AutoSize = true };
        var importProfile = new Button { Text = "Importieren…", AutoSize = true };
        profileButtons.Controls.AddRange([saveProfile, applyProfile, exportProfile, importProfile]);
        Add(profileButtons);

        SavedProfile CaptureProfile() => new()
        {
            Style = s.Style, Color = s.Color, AutoContrast = s.AutoContrast,
            AppAccent = s.AppAccent,
            ContextStyle = s.ContextStyle, MaximizedStyle = s.MaximizedStyle,
            ClockLayout = s.ClockLayout, ClockOnPrimaryOnly = s.ClockOnPrimaryOnly,
            SecondaryTimeZoneId = s.SecondaryTimeZoneId, ShowMediaRail = s.ShowMediaRail,
            HidePinnedUntilAlt = s.HidePinnedUntilAlt, KeepPinnedCount = s.KeepPinnedCount,
            FocusLens = s.FocusLens, RightModes = [.. s.RightModes!],
            AppStacks = s.AppStacks.Copy(),
            TrayHide = s.TrayHide, TrayOnDemand = s.TrayOnDemand, AutoHide = Taskbar.AutoHide,
            Tweaks = Tweaks.ToDictionary(t => t.Name, t => t.Get()),
            HiddenPrograms = [.. (s.HiddenPrograms ?? [])],
        };
        void WriteProfileTweaks(SavedProfile profile)
        {
            var values = profile.Tweaks ?? [];
            foreach (var tweak in Tweaks)
                if (values.TryGetValue(tweak.Name, out int value))
                    tweak.Set(tweak.Options is { } options ? Math.Clamp(value, 0, options.Length - 1) : value == 0 ? 0 : 1);
        }
        void AssignProfile(SavedProfile profile)
        {
            s.Style = Math.Clamp(profile.Style, 0, 4);
            s.Color = profile.Color;
            s.AutoContrast = profile.AutoContrast;
            s.AppAccent = profile.AppAccent;
            s.ContextStyle = profile.ContextStyle;
            s.MaximizedStyle = Math.Clamp(profile.MaximizedStyle, 0, 4);
            s.ClockLayout = Math.Clamp(profile.ClockLayout, 0, 2);
            s.ShowDesignClock = s.ClockLayout != 0;
            s.ClockOnPrimaryOnly = profile.ClockOnPrimaryOnly;
            s.SecondaryTimeZoneId = profile.SecondaryTimeZoneId;
            s.ShowMediaRail = profile.ShowMediaRail;
            s.HidePinnedUntilAlt = profile.HidePinnedUntilAlt;
            s.KeepPinnedCount = Math.Clamp(profile.KeepPinnedCount, 0, 5);
            s.FocusLens = profile.FocusLens;
            s.AppStacks = (profile.AppStacks ?? new AppStackConfig()).Copy();
            s.RightModes = RightAreaModes.Normalize(profile.RightModes, profile.TrayHide, profile.TrayOnDemand);
            s.TrayHide = profile.TrayHide & 31;
            s.TrayOnDemand = profile.TrayOnDemand;
            s.HiddenPrograms = (profile.HiddenPrograms ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).ToList();
            s.CleanBackup = null;
        }
        void SyncProfileUi()
        {
            reverting = true;
            try
            {
                style.SelectedIndex = s.Style;
                alpha.Value = (s.Color >> 24) & 0xFF;
                autoContrast.Checked = s.AutoContrast;
                appAccent.Checked = s.AppAccent;
                contextStyle.Checked = s.ContextStyle;
                maximizedStyle.SelectedIndex = s.MaximizedStyle;
                autoHide.Checked = Taskbar.AutoHide;
                clockLayout.SelectedIndex = s.ClockLayout;
                primaryClock.Checked = s.ClockOnPrimaryOnly;
                secondaryZone.SelectedIndex = Array.FindIndex(timeZones, zone => zone.Id == s.SecondaryTimeZoneId) + 1;
                mediaToggle.Checked = s.ShowMediaRail;
                pinned.Checked = s.HidePinnedUntilAlt;
                favorites.SelectedIndex = s.KeepPinnedCount;
                focusLens.Checked = s.FocusLens;
                foreach (var refresh in refreshControls) refresh();
            }
            finally { reverting = false; }
            maximizedStyle.Enabled = s.ContextStyle;
            pinned.Enabled = !s.AppStacks.Enabled;
            favorites.Enabled = !s.AppStacks.Enabled && s.HidePinnedUntilAlt;
            ShowColor(); ShowAlpha(); ShowStyle(); UpdateCleanUi(); RefreshIllustrations();
            themePicker.SetSelectedTheme(ThemeCatalog.Match(s)?.Id);
            programHider.Update(CurrentHiddenPrograms);
            programHider.SetFocusLens(s.FocusLens, AltSettingsButton.AltDown, Taskbar.ForegroundWindow);
            UpdateClocks();
            mediaRail.Update(s.ShowMediaRail, s.HideTaskbar);
            nextVisualRefresh = 0;
        }
        refreshAppearance = SyncProfileUi;
        void ApplyProfile(int slot)
        {
            var profile = s.Profiles[slot];
            if (profile == null) return;
            var before = CaptureProfile();
            try
            {
                WriteProfileTweaks(profile);
                Taskbar.AutoHide = profile.AutoHide;
                AssignProfile(profile);
                s.Save();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                try { WriteProfileTweaks(before); Taskbar.AutoHide = before.AutoHide; AssignProfile(before); s.Save(); } catch { }
                MessageBox.Show($"Profil konnte nicht angewendet werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Taskbar.Notify();
            SyncProfileUi();
        }
        saveProfile.Click += (_, _) => { s.Profiles[profileSlot.SelectedIndex] = CaptureProfile(); s.Save(); ShowProfileStatus(); };
        applyProfile.Click += (_, _) => ApplyProfile(profileSlot.SelectedIndex);
        exportProfile.Click += (_, _) =>
        {
            var profile = s.Profiles[profileSlot.SelectedIndex];
            if (profile == null) { MessageBox.Show("Speichere zuerst die aktuelle Ansicht in diesem Profil.", Text); return; }
            using var pick = new SaveFileDialog { Filter = "winBar-Profil (*.json)|*.json", FileName = profileSlot.SelectedIndex == 0 ? "winbar-arbeit.json" : "winbar-privat.json" };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(pick.FileName, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show($"Profil konnte nicht exportiert werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        importProfile.Click += (_, _) =>
        {
            using var pick = new OpenFileDialog { Filter = "winBar-Profil (*.json)|*.json", CheckFileExists = true };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var profile = JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(pick.FileName));
                if (profile is not { Version: 1 }) throw new FormatException("Unbekannte Profil-Version.");
                s.Profiles[profileSlot.SelectedIndex] = profile;
                s.Save();
                ShowProfileStatus();
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
            {
                MessageBox.Show($"Profil konnte nicht importiert werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        void ApplyScene(SceneSnapshot scene)
        {
            var options = scene.ApplyTo(s);
            s.RightModes = RightAreaModes.Normalize(options.RightModes, s.TrayHide, s.TrayOnDemand);
            s.FocusLens = options.FocusLens;
            s.SecondaryTimeZoneId = options.SecondaryTimeZoneId;
            if (options.AppStacksEnabled is bool enabled) s.AppStacks.Enabled = enabled;
            if (options.VisibleStackIds != null)
            {
                var visible = options.VisibleStackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var group in s.AppStacks.Groups) group.Enabled = visible.Contains(group.Id.ToString("N"));
            }
            if (options.MonitorLayouts != null) { s.MonitorLayouts = options.MonitorLayouts; nextMonitorRefresh = 0; }
            Taskbar.AutoHide = options.AutoHide;
            s.Save();
            Taskbar.Notify();
            SyncProfileUi();
        }

        void EvaluateAutomaticScenes()
        {
            var localNow = DateTime.Now;
            var screens = Screen.AllScreens;
            var rule = SceneRuleEvaluator.Select(s.SceneRules, localNow, screens.Length,
                screens.Select(screen => screen.DeviceName).ToArray());
            var scene = rule == null ? null : s.Scenes.FirstOrDefault(item => item.Id == rule.SceneId);
            if (scene != null && scene.Id != activeAutoSceneId)
            {
                beforeAutoScene ??= SceneSnapshot.Capture("Vor automatischer Szene", s, s.RightModes, s.FocusLens,
                    s.SecondaryTimeZoneId, s.AppStacks.Groups.Where(group => group.Enabled).Select(group => group.Id.ToString("N")),
                    s.MonitorLayouts);
                try { ApplyScene(scene); activeAutoSceneId = scene.Id; }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or IOException) { }
            }
            else if (scene == null && activeAutoSceneId != null)
            {
                if (beforeAutoScene != null)
                    try { ApplyScene(beforeAutoScene); }
                    catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or IOException) { }
                activeAutoSceneId = null;
                beforeAutoScene = null;
            }
            var boundary = SceneRuleEvaluator.NextTimeBoundary(s.SceneRules, localNow);
            int delay = boundary == null ? 60000 : (int)Math.Clamp((boundary.Value - localNow).TotalMilliseconds + 150, 1000, 60000);
            nextSceneCheck = Environment.TickCount64 + delay;
        }

        Header("Szenen");
        var manageScenes = new Button { Text = "Szenen und automatische Regeln…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        manageScenes.Click += (_, _) =>
        {
            using var dialog = new SceneManagerDialog(s, s.Scenes, s.SceneRules, s.RightModes, s.FocusLens,
                s.SecondaryTimeZoneId, s.AppStacks.Groups.Where(group => group.Enabled).Select(group => group.Id.ToString("N")));
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            s.Scenes = dialog.Scenes;
            s.SceneRules = dialog.Rules;
            activeAutoSceneId = null;
            beforeAutoScene = null;
            if (dialog.ActivatedScene != null)
                try { ApplyScene(dialog.ActivatedScene); }
                catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or IOException)
                { MessageBox.Show($"Szene konnte nicht aktiviert werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            s.Save();
            nextSceneCheck = 0;
        };
        Add(manageScenes);
        Add(new Label { Text = "Eine Szene speichert Look, sichtbare Apps, Uhr und rechten Bereich. Regeln können auf Monitor und Uhrzeit reagieren.", AutoSize = true, ForeColor = SystemColors.GrayText });

        Header("Monitore");
        var monitorSettings = new Button { Text = "Layout pro Monitor…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        monitorSettings.Click += (_, _) =>
        {
            using var dialog = new MonitorLayoutDialog(s, s.MonitorLayouts, Taskbar.SupportsMonitorLayouts);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            s.MonitorLayouts = dialog.Layouts;
            s.Save();
            nextVisualRefresh = nextClockRefresh = nextMonitorRefresh = 0;
            UpdateClocks();
        };
        Add(monitorSettings);
        Add(new Label { Text = "Jeder Bildschirm kann eigenes Design, Uhr, rechte Seite und App-Stapel bekommen.", AutoSize = true, ForeColor = SystemColors.GrayText });

        Header("Arbeitsbereiche");
        var workspaces = new Button { Text = "Arbeitsbereiche einrichten…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        workspaces.Click += (_, _) => { using var dialog = new WorkspaceManagerDialog(s); dialog.ShowDialog(this); };
        Add(workspaces);
        Add(new Label { Text = "Apps und Fensteranordnung auf Knopfdruck wiederherstellen. Kein Hintergrund-Scan.", AutoSize = true, ForeColor = SystemColors.GrayText });

        Header("Aktionen");
        var tour = new Button { Text = "Kurzanleitung anzeigen", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        tour.Click += (_, _) => { using var welcome = new WelcomeForm(); welcome.ShowDialog(this); };
        Add(tour);
        Section("Maintenance", "Wartung");
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var restart = new Button { Text = "Explorer neu starten", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        restart.Click += async (_, _) => await Taskbar.RestartExplorer();
        var unpin = new Button { Text = "Alle angehefteten Apps lösen (mit Backup)…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        unpin.Click += async (_, _) => await UnpinAll();
        var reset = new Button { Text = "Alles zurücksetzen…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        reset.Click += async (_, _) =>
        {
            if (MessageBox.Show("Alle Einstellungen auf Windows-Standard zurücksetzen und Explorer neu starten?\nAngeheftete Apps und Autostart bleiben unverändert.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            // Setting the controls runs their normal handlers, which write registry/settings.
            style.SelectedIndex = 0;
            autoContrast.Checked = false;
            appAccent.Checked = false;
            autoHide.Checked = false;
            hide.Checked = false;
            clockLayout.SelectedIndex = 0;
            primaryClock.Checked = false;
            secondaryZone.SelectedIndex = 0;
            mediaToggle.Checked = false;
            pinned.Checked = false;
            favorites.SelectedIndex = 0;
            focusLens.Checked = false;
            contextStyle.Checked = false;
            maximizedStyle.SelectedIndex = 2;
            s.CleanBackup = null;
            UpdateCleanUi();
            foreach (var control in rightModeControls) control.SelectedIndex = RightAreaModes.Always;
            s.Profiles = [null, null];
            ShowProfileStatus();
            s.HiddenPrograms = [];
            s.AppStacks = new AppStackConfig();
            pinned.Enabled = true;
            favorites.Enabled = s.HidePinnedUntilAlt;
            programHider.Update(CurrentHiddenPrograms);
            s.Color = new Settings().Color; s.Save();
            alpha.Value = (s.Color >> 24) & 0xFF;
            ShowColor();
            foreach (var r in resets) r();
            await Taskbar.RestartExplorer();
        };
        buttons.Controls.AddRange([restart, reset, unpin]);
        Add(buttons);

        Header("Updates");
        var autoUpdates = Check("Neue Releases automatisch im Hintergrund laden", s.AutoUpdates);
        var updateStatus = new Label
        {
            Text = Localization.T($"Version {typeof(Program).Assembly.GetName().Version?.ToString(3)} · New versions start the next time you open winBar.",
                $"Version {typeof(Program).Assembly.GetName().Version?.ToString(3)} · Neue Versionen starten beim nächsten Öffnen."),
            AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(520), 0), ForeColor = SystemColors.GrayText
        };
        Add(updateStatus);
        var checkUpdates = new Button { Text = "Jetzt nach Updates suchen", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        checkUpdates.Click += async (_, _) => { if (updater != null) await updater.CheckNowAsync(true); };
        Add(checkUpdates);
        updater = new ReleaseUpdater(message =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() => updateStatus.Text = message);
        }, () => s.AutoUpdates);
        autoUpdates.CheckedChanged += (_, _) =>
        {
            if (reverting) return;
            s.AutoUpdates = autoUpdates.Checked;
            s.Save();
            if (s.AutoUpdates && updater != null) _ = updater.CheckNowAsync(false);
            else { ReleaseUpdater.DiscardReadyUpdate(); updateStatus.Text = Localization.T("Automatic updates disabled.", "Automatische Updates ausgeschaltet."); }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => OpenWindow());
        menu.Items.Add("Ordnerstapel", null, (_, _) => OpenFolderStack());
        presentationMenu = new ToolStripMenuItem("Bildschirmfreigabe-Modus", null, (_, _) => SetPresentationMode(!presentationMode));
        menu.Items.Add(presentationMenu);
        var stackMenu = new ToolStripMenuItem("App-Stapel starten");
        menu.Items.Add(stackMenu);
        var workspaceMenu = new ToolStripMenuItem("Arbeitsbereich öffnen");
        menu.Items.Add(workspaceMenu);
        menu.Opening += (_, _) =>
        {
            stackMenu.DropDownItems.Clear();
            foreach (var group in s.AppStacks.Groups.Where(group => group.Enabled && group.LaunchPaths.Count > 0))
            {
                var chosen = group;
                stackMenu.DropDownItems.Add(group.Name, null, (_, _) => LaunchStack(chosen));
            }
            stackMenu.Enabled = stackMenu.DropDownItems.Count > 0;
            workspaceMenu.DropDownItems.Clear();
            foreach (var scene in s.Workspaces)
            {
                var chosen = scene;
                workspaceMenu.DropDownItems.Add(scene.Name, null, async (_, _) =>
                {
                    try { await WorkspaceController.RestoreAsync(chosen, workspaceStop.Token); }
                    catch (OperationCanceledException) { }
                });
            }
            workspaceMenu.Enabled = workspaceMenu.DropDownItems.Count > 0;
            Localization.Apply(menu);
        };
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "winBar", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };

        settingsShell.LanguageChoice.SelectedIndex = s.Language switch
        {
            Localization.German => 1,
            Localization.System => 2,
            _ => 0
        };
        settingsShell.LanguageChoice.SelectedIndexChanged += (_, _) =>
        {
            if (reverting) return;
            s.Language = settingsShell.LanguageChoice.SelectedIndex switch
            {
                1 => Localization.German,
                2 => Localization.System,
                _ => Localization.English
            };
            s.Save();
            Localization.Configure(s.Language);
            reverting = true;
            try { Localization.Apply(this); Localization.Apply(menu); themePicker.RefreshThemes(); settingsShell.Invalidate(true); }
            finally { reverting = false; }
            ShowStyle(); ShowAlpha(); ShowProfileStatus(); UpdateCleanUi();
        };
        reverting = true;
        try { Localization.Apply(this); Localization.Apply(menu); }
        finally { reverting = false; }

        // Poll only the Alt interaction frequently. The Explorer style and window list need less frequent work.
        timer.Tick += (_, _) =>
        {
            Taskbar.SetPreviewReceiver(Handle);
            if (!updaterStarted) { updaterStarted = true; updater?.Start(); }
            long now = Environment.TickCount64;
            if (now >= nextSceneCheck) EvaluateAutomaticScenes();
            altButton.Update(!s.HideTaskbar);
            bool altDown = AltSettingsButton.AltDown;
            ulong stackRanges = s.AppStacks.PackRanges();
            var bars = (pinnedRevealed || s.HidePinnedUntilAlt || stackRanges != 0 || RightAreaModes.HasPeek(s.RightModes!)) ? Taskbar.VisibleBounds().ToArray() : [];
            var pointer = Cursor.Position;
            if (altDown) pinnedRevealed = true;
            else if (pinnedRevealed && !bars.Any(r => r.Contains(pointer))) pinnedRevealed = false;
            bool pinnedHidden = stackRanges == 0 && s.HidePinnedUntilAlt && !pinnedRevealed;
            bool nearRight = RightAreaModes.HasPeek(s.RightModes!) && bars.Any(r => NearRightArea(r, pointer));
            if (altDown || nearRight || (rightRevealed && bars.Any(r => InRightFlyoutArea(r, pointer))))
            {
                rightRevealed = true;
                rightRevealUntil = now + 1100;
            }
            else if (now >= rightRevealUntil) rightRevealed = false;
            int effectiveTrayMask = presentationMode ? 31 : RightAreaModes.HiddenMask(s.RightModes!, rightRevealed);
            programHider.SetFocusLens(s.FocusLens, altDown, Taskbar.ForegroundWindow);
            if (stackRanges != 0 && now >= nextStackDensity)
            {
                nextStackDensity = now + 2000;
                if (!s.AppStacks.AutoCompact) stackCollapseActive = true;
                else
                {
                    int width = bars.Where(bar => bar.Width > bar.Height * 3).Select(bar => bar.Width).DefaultIfEmpty(0).Max();
                    int pins = s.AppStacks.Groups.Where(group => group.Enabled).Select(group => group.Start + group.Count - 1).DefaultIfEmpty(0).Max();
                    int estimated = pins * 44 + AppWindows.List().Count * 44 + 270;
                    int percent = width > 0 ? estimated * 100 / width : 0;
                    int threshold = Math.Clamp(s.AppStacks.CompactAtPercent, 45, 95);
                    if (percent >= threshold) stackCollapseActive = true;
                    else if (percent <= threshold - 8) stackCollapseActive = false;
                }
            }
            int stackFlags = stackRanges == 0 ? 0 :
                (altDown ? 1 : 0) | (bars.Any(bar => bar.Contains(pointer)) ? 2 : 0) | (stackCollapseActive ? 4 : 0);
            bool maximized = Taskbar.ActiveWindowMaximized;
            if (now >= nextVisualRefresh || pinnedHidden != lastPinnedHidden || maximized != lastMaximized ||
                effectiveTrayMask != lastEffectiveTrayMask || stackRanges != appliedStackRanges || stackFlags != appliedStackFlags)
            {
                nextVisualRefresh = now + 1000;
                lastPinnedHidden = pinnedHidden;
                lastMaximized = maximized;
                lastEffectiveTrayMask = effectiveTrayMask;
                appliedStackRanges = stackRanges;
                appliedStackFlags = stackFlags;
                int activeStyle = s.ContextStyle && maximized ? s.MaximizedStyle : s.Style;
                Taskbar.Tap(activeStyle > 0, effectiveTrayMask, pinnedHidden, s.KeepPinnedCount,
                    stackRanges, stackFlags, HasNativeLayoutOverrides());
                var foreground = Taskbar.ForegroundWindow;
                var foregroundScreen = foreground == IntPtr.Zero ? null : Screen.FromHandle(foreground);
                int? appTint = s.AppAccent || s.MonitorLayouts.Any(layout => layout.AppAccent)
                    ? AppAccent.ForegroundColor(foreground) : null;
                MonitorAccent.Apply(MonitorTaskbars.Enumerate(),
                    screen => MonitorLayouts.Resolve(screen, s, s.MonitorLayouts, nativeLayoutReady),
                    foregroundScreen, maximized, appTint);
                if (s.HideTaskbar) Taskbar.Show(false);
            }
            if (now >= nextMonitorRefresh)
            {
                bool ready = SendMonitorLayouts(altDown, pointer);
                nextMonitorRefresh = now + (ready ? 200 : 2000);
                if (ready != nativeLayoutReady)
                {
                    nativeLayoutReady = ready;
                    nextVisualRefresh = nextClockRefresh = 0;
                }
            }
            if (now >= nextClockRefresh)
            {
                nextClockRefresh = now + 500;
                UpdateClocks();
                mediaRail.Update(s.ShowMediaRail && !presentationMode, s.HideTaskbar);
            }
            bool openHotkeyDown = AltSettingsButton.OpenHotkeyDown;
            if (openHotkeyDown && !openHotkeyWasDown) OpenWindow();
            openHotkeyWasDown = openHotkeyDown;
            bool presentationHotkeyDown = AltSettingsButton.PresentationHotkeyDown;
            if (presentationHotkeyDown && !presentationHotkeyWasDown) SetPresentationMode(!presentationMode);
            presentationHotkeyWasDown = presentationHotkeyDown;
            bool folderHotkeyDown = AltSettingsButton.FolderHotkeyDown;
            if (folderHotkeyDown && !folderHotkeyWasDown) OpenFolderStack();
            folderHotkeyWasDown = folderHotkeyDown;
            int profileHotkey = AltSettingsButton.ProfileHotkey;
            if (profileHotkey != 0 && profileHotkey != lastProfileHotkey) ApplyProfile(profileHotkey - 1);
            lastProfileHotkey = profileHotkey;
            if (now >= nextProgramScan)
            {
                nextProgramScan = now + 2000;
                programHider.Refresh();
            }
        };
        timer.Start();
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(!startHidden && value);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!s.WelcomeSeen)
            BeginInvoke(() =>
            {
                if (s.WelcomeSeen || IsDisposed) return;
                using var welcome = new WelcomeForm();
                welcome.ShowDialog(this);
                s.WelcomeSeen = true;
                s.Save();
            });
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x8005)
        {
            int payload = message.LParam.ToInt32();
            OnStackPreviewHover(message.WParam, payload >> 1, (payload & 1) != 0);
            return;
        }
        if (message.Msg == 0x007E) nextSceneCheck = 0; // WM_DISPLAYCHANGE
        if (message.Msg is 0x0320 or 0x031A or 0x001A)
        {
            Taskbar.InvalidateSystemTint();
            nextVisualRefresh = 0;
        }
        base.WndProc(ref message);
    }

    void OnStackPreviewHover(IntPtr ownerBar, int slot, bool entered)
    {
        if (!entered)
        {
            if (stackPreviewBar == ownerBar && stackPreviewSlot == slot) stackPreview?.ScheduleClose();
            return;
        }
        var config = s.AppStacks.Copy();
        var mappedBar = ownerBar == IntPtr.Zero ? null : MonitorTaskbars.Enumerate().FirstOrDefault(bar => bar.Handle == ownerBar);
        var layout = nativeLayoutReady && mappedBar != null ? MonitorLayouts.ForScreen(mappedBar.Screen, s.MonitorLayouts) : null;
        if (layout?.NativeStacksEnabled == true)
        {
            config.Enabled = true;
            var visible = (layout.VisibleStackIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in config.Groups) item.Enabled = visible.Contains(item.Id.ToString("N"));
        }
        var groups = config.ValidPackedGroups();
        if ((uint)slot >= (uint)groups.Count || !config.Enabled || s.HideTaskbar || presentationMode)
        {
            stackPreview?.ScheduleClose();
            return;
        }
        var group = groups[slot];
        if (stackPreview is { IsDisposed: false } && stackPreview.GroupId == group.Id &&
            stackPreviewBar == ownerBar && stackPreviewSlot == slot)
        {
            stackPreview.CancelClose();
            return;
        }
        stackPreview?.Close();
        var pointer = Cursor.Position;
        var bar = Taskbar.VisibleBounds().FirstOrDefault(bounds => bounds.Contains(pointer));
        var anchor = bar == Rectangle.Empty ? new Rectangle(pointer, new Size(1, 1)) :
            new Rectangle(Math.Clamp(pointer.X - 24, bar.Left, Math.Max(bar.Left, bar.Right - 48)), bar.Top, 48, bar.Height);
        var popup = StackWindowPreviewPopup.ShowFor(group, anchor);
        stackPreview = popup;
        stackPreviewBar = ownerBar;
        stackPreviewSlot = slot;
        popup.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(stackPreview, popup)) return;
            stackPreview = null;
            stackPreviewBar = IntPtr.Zero;
            stackPreviewSlot = -1;
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    void OpenWindow() { startHidden = false; Show(); WindowState = FormWindowState.Normal; Activate(); }

    void OpenFolderStack()
    {
        var anchor = Taskbar.VisibleBounds().FirstOrDefault(bar => Screen.FromRectangle(bar).Primary);
        if (anchor == Rectangle.Empty) anchor = new Rectangle(Cursor.Position, new Size(1, 1));
        FolderStackPopup.ShowFolders(s.FolderStackPaths, anchor);
    }

    void SetPresentationMode(bool enabled)
    {
        if (presentationMode == enabled) return;
        presentationMode = enabled;
        if (presentationToggle != null && presentationToggle.Checked != enabled) presentationToggle.Checked = enabled;
        if (presentationMenu != null) presentationMenu.Checked = enabled;
        programHider.Update(CurrentHiddenPrograms);
        nextVisualRefresh = nextClockRefresh = 0;
    }

    void ExitApp()
    {
        exiting = true;
        updater?.Stop();
        timer.Stop();
        altButton.Close();
        clock.Dispose();
        mediaRail.Dispose();
        stackPreview?.Close();
        Taskbar.SetPreviewReceiver(IntPtr.Zero);
        workspaceStop.Cancel();
        programHider.Dispose();
        if (Taskbar.NativeTapWindow != IntPtr.Zero)
            MonitorTapProtocol.Send(Taskbar.NativeTapWindow, Array.Empty<MonitorTapEntry>());
        MonitorAccent.Reset();
        Taskbar.Tap(false, 0, false, 0);
        Taskbar.Show(true);
        tray.Visible = false;
        Application.Exit();
    }

    void Apply(Tweak t, int v, Action revert)
    {
        if (reverting) return;
        try { t.Set(v); Taskbar.Notify(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show($"Windows blockiert \"{t.Label}\":\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            reverting = true; revert(); reverting = false;
        }
    }

    static bool Autostart
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("TaskbarTweaker") != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("TaskbarTweaker", $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue("TaskbarTweaker", false);
        }
    }

    async Task UnpinAll()
    {
        if (MessageBox.Show("Alle angehefteten Apps von der Taskleiste lösen?\nVorher wird ein Backup erstellt.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        var backup = Path.Combine(Settings.Dir, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backup);
        if (Directory.Exists(pinned))
            foreach (var f in Directory.GetFiles(pinned)) File.Copy(f, Path.Combine(backup, Path.GetFileName(f)));
        using (var reg = Process.Start(new ProcessStartInfo("reg.exe", $"export \"HKCU\\{Taskband}\" \"{Path.Combine(backup, "Taskband.reg")}\" /y") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            await reg.WaitForExitAsync();
            if (reg.ExitCode != 0) { MessageBox.Show("Registry-Backup fehlgeschlagen – nichts gelöscht.", Text); return; }
        }
        if (Directory.Exists(pinned))
            foreach (var f in Directory.GetFiles(pinned)) File.Delete(f);
        using (var k = Registry.CurrentUser.OpenSubKey(Taskband, true))
        {
            k?.DeleteValue("Favorites", false);
            k?.DeleteValue("FavoritesResolve", false);
        }
        await Taskbar.RestartExplorer();
        MessageBox.Show($"Erledigt. Backup: {backup}\n\nWiederherstellen: Taskband.reg doppelklicken, .lnk-Dateien zurück nach\n{pinned}\nkopieren, Explorer neu starten.", Text);
    }

    static bool NearRightArea(Rectangle bar, Point pointer)
    {
        if (!bar.Contains(pointer)) return false;
        return bar.Width > bar.Height * 3
            ? pointer.X >= bar.Right - Math.Min(260, bar.Width / 3)
            : pointer.Y >= bar.Bottom - Math.Min(260, bar.Height / 3);
    }

    async void LaunchStack(AppStackGroup group)
    {
        var result = await AppStackLauncher.LaunchAllAsync(group);
        if (result.Failed.Count > 0)
            MessageBox.Show($"{result.Failed.Count} Apps konnten nicht gestartet werden. Prüfe die Pfade im App-Stapel.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    static bool InRightFlyoutArea(Rectangle bar, Point pointer)
    {
        var screen = Screen.FromRectangle(bar).Bounds;
        Rectangle area;
        if (bar.Width > bar.Height * 3)
        {
            int x = bar.Right - Math.Min(440, bar.Width);
            area = bar.Top > screen.Top + screen.Height / 2
                ? Rectangle.FromLTRB(x, Math.Max(screen.Top, bar.Top - 680), bar.Right, bar.Bottom)
                : Rectangle.FromLTRB(x, bar.Top, bar.Right, Math.Min(screen.Bottom, bar.Bottom + 680));
        }
        else
        {
            int y = bar.Bottom - Math.Min(440, bar.Height);
            area = bar.Left > screen.Left + screen.Width / 2
                ? Rectangle.FromLTRB(Math.Max(screen.Left, bar.Left - 680), y, bar.Right, bar.Bottom)
                : Rectangle.FromLTRB(bar.Left, y, Math.Min(screen.Right, bar.Right + 680), bar.Bottom);
        }
        return area.Contains(pointer);
    }

    // Without a caption the control spans both columns; with one, caption left and control stretched right.
    void Add(Control c, Label? caption = null)
    {
        if (caption == null)
        {
            if (c is Label label && label.MaximumSize.Width == 0) label.MaximumSize = new Size(LogicalToDeviceUnits(640), 0);
            if (c is CheckBox check) check.MaximumSize = new Size(LogicalToDeviceUnits(640), 0);
            grid.Controls.Add(c); grid.SetColumnSpan(c, 2); return;
        }
        caption.MaximumSize = new Size(LogicalToDeviceUnits(240), 0);
        grid.Controls.Add(caption);
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(c);
    }

    void Guide(SettingsIllustrationKind kind)
    {
        var illustration = new SettingsIllustration(kind, s);
        illustrations.Add(illustration);
        Add(illustration);
    }

    void RefreshIllustrations()
    {
        foreach (var illustration in illustrations) illustration.Invalidate();
    }

    void Section(string english, string german) => Add(new Label
    {
        Text = Localization.T(english, german), AutoSize = true,
        Font = new Font("Segoe UI Semibold", 12, FontStyle.Bold),
        ForeColor = Color.FromArgb(40, 72, 120),
        Margin = new Padding(3, 18, 3, 7)
    });

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 16, 6) };

    void Header(string text)
    {
        var (key, pageEn, pageDe, descriptionEn, descriptionDe, sectionEn) = text switch
        {
            "Aussehen" => ("appearance", "Appearance", "Aussehen", "Themes, color and glass effects", "Designs, Farben und Glaseffekte", "Appearance"),
            "Verhalten" => ("taskbar", "Taskbar", "Taskleiste", "Visibility and Windows controls", "Sichtbarkeit und Windows-Elemente", "Behavior"),
            "Elemente" => ("taskbar", "Taskbar", "Taskleiste", "Visibility and Windows controls", "Sichtbarkeit und Windows-Elemente", "Windows elements"),
            "Uhr und Medien" => ("clock", "Clock and media", "Uhr und Medien", "Time, time zones and playback", "Uhrzeit, Zeitzonen und Wiedergabe", "Clock and media"),
            "Apps" => ("apps", "Apps and focus", "Apps und Fokus", "Pins, stacks, privacy and focus", "Pins, Stapel, Privatsphäre und Fokus", "Apps and focus"),
            "Rechter Bereich" => ("right", "System area", "Rechter Bereich", "Clock, Quick Settings and tray icons", "Uhr, Schnelleinstellungen und Tray-Icons", "System area"),
            "Profile" => ("layouts", "Layouts", "Layouts", "Profiles, scenes, monitors and workspaces", "Profile, Szenen, Monitore und Arbeitsbereiche", "Profiles"),
            "Szenen" => ("layouts", "Layouts", "Layouts", "Profiles, scenes, monitors and workspaces", "Profile, Szenen, Monitore und Arbeitsbereiche", "Scenes"),
            "Monitore" => ("layouts", "Layouts", "Layouts", "Profiles, scenes, monitors and workspaces", "Profile, Szenen, Monitore und Arbeitsbereiche", "Monitors"),
            "Arbeitsbereiche" => ("layouts", "Layouts", "Layouts", "Profiles, scenes, monitors and workspaces", "Profile, Szenen, Monitore und Arbeitsbereiche", "Workspaces"),
            "Aktionen" => ("system", "Help and maintenance", "Hilfe und Wartung", "Quick guide and Windows controls", "Kurzanleitung und Windows-Aktionen", "Actions"),
            "Updates" => ("updates", "Updates", "Updates", "Automatic release checks", "Automatische Release-Prüfung", "Updates"),
            _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown settings section")
        };
        grid = settingsShell.AddPage(key, pageEn, pageDe, descriptionEn, descriptionDe);
        if (!builtPages.Add(key))
            Add(new Label
            {
                Text = Localization.T(sectionEn, text), AutoSize = true,
                Font = new Font("Segoe UI Semibold", 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 72, 120), Margin = new Padding(0, 22, 3, 8)
            });
    }

    ComboBox Combo(string label, string[] items, int sel)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 4) };
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
        Add(c, Caption(label));
        return c;
    }

    CheckBox Check(string label, bool on)
    {
        var c = new CheckBox { Text = label, Checked = on, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        Add(c);
        return c;
    }
}
