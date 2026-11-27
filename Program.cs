using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Debug.Assert(Taskbar.ToAbgr(unchecked((int)0x80112233)) == unchecked((int)0x80332211));
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System); // follow Windows light/dark setting
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
    static bool? lastBackground, lastPinned;
    static int lastTrayMask = -1;

    // Class name carries a version: an older TaskbarTap.dll still loaded in explorer does not know newer messages.
    static IntPtr TapWindow() => FindWindowEx(new IntPtr(-3), IntPtr.Zero, "TaskbarTweakerTap3", null); // HWND_MESSAGE

    // hideBackground: fade XAML background; trayMask: tray parts; hidePinned: show pinned-only buttons on demand.
    public static void Tap(bool hideBackground, int trayMask, bool hidePinned)
    {
        var w = TapWindow();
        if (w != IntPtr.Zero)
        {
            if (w != lastTapWindow) { lastTapWindow = w; lastBackground = lastPinned = null; lastTrayMask = -1; }
            if (lastBackground != hideBackground) { PostMessage(w, 0x8000, hideBackground ? 1 : 0, IntPtr.Zero); lastBackground = hideBackground; }
            if (lastTrayMask != trayMask) { PostMessage(w, 0x8001, trayMask, IntPtr.Zero); lastTrayMask = trayMask; }
            if (lastPinned != hidePinned) { PostMessage(w, 0x8002, hidePinned ? 1 : 0, IntPtr.Zero); lastPinned = hidePinned; }
            return;
        }
        lastTapWindow = IntPtr.Zero;
        if ((!hideBackground && trayMask == 0 && !hidePinned) || Environment.OSVersion.Version.Build < 22000 || !File.Exists(TapDll)) return;
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
    public bool HidePinnedUntilAlt { get; set; }
    public bool ContextStyle { get; set; }
    public int MaximizedStyle { get; set; } = 2;
    public CleanBackup? CleanBackup { get; set; }
    public List<string> HiddenPrograms { get; set; } = [];

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
            return s;
        }
        catch { return new(); }
    }
    public void Save() { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
}

class CleanBackup
{
    public Dictionary<string, int> Values { get; set; } = [];
    public int TrayHide { get; set; }
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
    static readonly int[] AccentFor = [0, 2, 2, 3, 4];
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
    readonly ProgramHider programHider = new();
    readonly NotifyIcon tray;
    readonly TableLayoutPanel grid = new() { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(16, 4, 24, 16) };
    bool startHidden, exiting, reverting, openHotkeyWasDown, pinnedRevealed, lastPinnedHidden, lastMaximized;
    long nextProgramScan, nextVisualRefresh, nextClockRefresh;
    int lastAppliedStyle = -1;

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        Text = "winBar · Einstellungen";
        ClientSize = new Size(LogicalToDeviceUnits(580), Math.Min(LogicalToDeviceUnits(760), Screen.PrimaryScreen!.WorkingArea.Height * 9 / 10));
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(400));
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        // Percent, not AutoSize: spanning checkboxes would otherwise widen the caption column.
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        Controls.Add(grid);
        altButton = new AltSettingsButton(OpenWindow);

        Header("Aussehen");
        var style = Combo("Stil", ["Standard", "Komplett transparent", "Farbe (getönt)", "Blur", "Acrylic"], s.Style);
        var colorBtn = new Button { FlatStyle = FlatStyle.Flat, AutoSize = true };
        colorBtn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = Color.FromArgb(s.Color | unchecked((int)0xFF000000)), FullOpen = true };
            if (dlg.ShowDialog() == DialogResult.OK) { s.Color = (s.Color & unchecked((int)0xFF000000)) | (dlg.Color.ToArgb() & 0xFFFFFF); s.Save(); ShowColor(); nextVisualRefresh = 0; }
        };
        var colorLbl = Caption("Farbe");
        Add(colorBtn, colorLbl);
        var alphaLbl = Caption("");
        var alpha = new TrackBar { Minimum = 0, Maximum = 255, TickFrequency = 32, Value = (s.Color >> 24) & 0xFF };
        alpha.ValueChanged += (_, _) => { s.Color = (alpha.Value << 24) | (s.Color & 0xFFFFFF); s.Save(); ShowAlpha(); nextVisualRefresh = 0; };
        Add(alpha, alphaLbl);
        style.SelectedIndexChanged += (_, _) => { s.Style = style.SelectedIndex; s.Save(); ShowStyle(); nextVisualRefresh = 0; };
        ShowColor(); ShowAlpha(); ShowStyle();

        void ShowColor()
        {
            var c = Color.FromArgb(s.Color | unchecked((int)0xFF000000));
            colorBtn.BackColor = c;
            colorBtn.ForeColor = c.GetBrightness() > 0.5f ? Color.Black : Color.White;
            colorBtn.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}  –  ändern…";
        }
        void ShowAlpha() => alphaLbl.Text = $"Deckkraft  {alpha.Value * 100 / 255} %";
        // Only Farbe/Blur/Acrylic use the color; "transparent" always sends 0.
        void ShowStyle() => colorBtn.Enabled = colorLbl.Enabled = alpha.Enabled = alphaLbl.Enabled = s.Style >= 2;

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
                ShowColor(); ShowAlpha(); ShowStyle();
                nextVisualRefresh = 0;
            };
            looks.Controls.Add(button);
        }
        Add(looks, Caption("Schnell-Looks"));
        var contextStyle = Check("Bei maximierten Fenstern anderen Stil verwenden", s.ContextStyle);
        var maximizedStyle = Combo("Stil bei maximiertem Fenster", ["Standard", "Transparent", "Farbe (getönt)", "Blur", "Acrylic"], s.MaximizedStyle);
        maximizedStyle.Enabled = s.ContextStyle;
        contextStyle.CheckedChanged += (_, _) => { s.ContextStyle = contextStyle.Checked; maximizedStyle.Enabled = s.ContextStyle; s.Save(); nextVisualRefresh = 0; };
        maximizedStyle.SelectedIndexChanged += (_, _) => { s.MaximizedStyle = maximizedStyle.SelectedIndex; s.Save(); nextVisualRefresh = 0; };

        Header("Verhalten");
        var autoHide = Check("Automatisch ausblenden", Taskbar.AutoHide);
        autoHide.CheckedChanged += (_, _) => Taskbar.AutoHide = autoHide.Checked;
        var hide = Check("Taskleiste komplett verstecken (solange App läuft)", s.HideTaskbar);
        hide.CheckedChanged += (_, _) => { s.HideTaskbar = hide.Checked; if (!hide.Checked) Taskbar.Show(true); s.Save(); };
        var auto = Check("Mit Windows starten (im Tray)", Autostart);
        auto.CheckedChanged += (_, _) => Autostart = auto.Checked;
        var cleanControls = new List<Control>();
        var refreshControls = new List<Action>();
        var cleanButton = new Button { AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        cleanButton.Click += (_, _) => ToggleClean();
        Add(cleanButton, Caption("Clean-Profil"));
        Add(new Label { Text = "Blendet Suche, Widgets, Aufgabenansicht, Badges und Tray-Symbole gemeinsam aus. Ein zweiter Klick stellt deine vorherigen Werte wieder her.", AutoSize = true, ForeColor = SystemColors.GrayText });

        Header("Uhr und Programme");
        var clockLayout = Combo("Design-Uhr", ["Aus", "Nur Uhrzeit", "Uhrzeit und Datum"], s.ClockLayout);
        clockLayout.SelectedIndexChanged += (_, _) =>
        {
            s.ClockLayout = clockLayout.SelectedIndex;
            s.ShowDesignClock = s.ClockLayout != 0;
            s.Save();
            clock.Update(s.ClockLayout, s.ClockOnPrimaryOnly);
        };
        var primaryClock = Check("Design-Uhr nur auf dem Hauptmonitor", s.ClockOnPrimaryOnly);
        primaryClock.CheckedChanged += (_, _) => { s.ClockOnPrimaryOnly = primaryClock.Checked; s.Save(); clock.Update(s.ClockLayout, s.ClockOnPrimaryOnly); };
        Add(new Label { Text = "Die Uhr liegt über der Windows-Uhr. Klicks gehen weiterhin an Windows.", AutoSize = true, ForeColor = SystemColors.GrayText });
        var pinned = Check("Angeheftete Apps erst mit Alt zeigen (experimentell)", s.HidePinnedUntilAlt);
        pinned.CheckedChanged += (_, _) => { s.HidePinnedUntilAlt = pinned.Checked; s.Save(); nextVisualRefresh = 0; };
        Add(new Label { Text = "Laufende Apps sollen sichtbar bleiben. Nach dem Loslassen von Alt bleiben die angehefteten Apps sichtbar, bis die Maus die Taskleiste verlässt.", AutoSize = true, ForeColor = SystemColors.GrayText });
        var choosePrograms = new Button { Text = "Programme auf der Taskleiste ausblenden…", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        choosePrograms.Click += (_, _) =>
        {
            using var dialog = new HiddenProgramsDialog(s.HiddenPrograms ?? []);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            s.HiddenPrograms = dialog.SelectedPaths;
            s.Save();
            programHider.Update(s.HiddenPrograms);
        };
        Add(choosePrograms);
        Add(new Label { Text = "Fenster bleiben geöffnet; nur ihre Taskleisten-Schaltflächen werden ausgeblendet. Nicht jede App unterstützt dies.", AutoSize = true, ForeColor = SystemColors.GrayText });
        programHider.Update(s.HiddenPrograms ?? []);

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

        Header("Experimentell: Infobereich ausblenden");
        (string Label, int Bit)[] trayParts =
        [
            ("Uhr und Datum", 1),
            ("Schnelleinstellungen (WLAN, Lautstärke, Akku)", 2),
            ("Tray-Icons und ^-Pfeil", 4),
            ("Sprachanzeige", 8),
            ("Gesamten Infobereich", 16),
        ];
        foreach (var (label, bit) in trayParts)
        {
            var c = Check(label, (s.TrayHide & bit) != 0);
            c.CheckedChanged += (_, _) => { if (reverting) return; s.TrayHide = c.Checked ? s.TrayHide | bit : s.TrayHide & ~bit; s.Save(); nextVisualRefresh = 0; };
            resets.Add(() => c.Checked = false);
            refreshControls.Add(() => c.Checked = (s.TrayHide & bit) != 0);
            if (bit is 4 or 8) cleanControls.Add(c);
        }
        UpdateCleanUi();
        void UpdateCleanUi()
        {
            bool active = s.CleanBackup != null;
            cleanButton.Text = active ? "Vorherige Ansicht wiederherstellen" : "Clean-Profil aktivieren";
            foreach (var control in cleanControls) control.Enabled = !active;
        }
        void ToggleClean()
        {
            var targets = Tweaks.Where(t => CleanValues.ContainsKey(t.Name)).ToArray();
            try
            {
                if (s.CleanBackup == null)
                {
                    var backup = new CleanBackup { TrayHide = s.TrayHide };
                    foreach (var t in targets) backup.Values[t.Name] = t.Get();
                    try { foreach (var t in targets) t.Set(CleanValues[t.Name]); }
                    catch
                    {
                        foreach (var t in targets) try { t.Set(backup.Values[t.Name]); } catch { }
                        throw;
                    }
                    s.TrayHide |= 4 | 8;
                    s.CleanBackup = backup;
                }
                else
                {
                    foreach (var t in targets)
                        if (s.CleanBackup.Values.TryGetValue(t.Name, out int value)) t.Set(value);
                    s.TrayHide = s.CleanBackup.TrayHide;
                    s.CleanBackup = null;
                }
                Taskbar.Notify();
                s.Save();
                reverting = true;
                foreach (var refresh in refreshControls) refresh();
                reverting = false;
                UpdateCleanUi();
                nextVisualRefresh = 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                reverting = false;
                MessageBox.Show($"Clean-Profil konnte nicht angewendet werden:\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        Add(new Label { Text = "Greift direkt in die Windows-Oberfläche ein und kann nach Windows-Updates nicht mehr funktionieren. Nach einem App-Update einmal Explorer neu starten.", AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(520), 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 0) });

        Header("Aktionen");
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
            autoHide.Checked = false;
            hide.Checked = false;
            clockLayout.SelectedIndex = 0;
            primaryClock.Checked = false;
            pinned.Checked = false;
            contextStyle.Checked = false;
            maximizedStyle.SelectedIndex = 2;
            s.CleanBackup = null;
            UpdateCleanUi();
            s.HiddenPrograms = [];
            programHider.Update(s.HiddenPrograms);
            s.Color = new Settings().Color; s.Save();
            alpha.Value = (s.Color >> 24) & 0xFF;
            ShowColor();
            foreach (var r in resets) r();
            await Taskbar.RestartExplorer();
        };
        buttons.Controls.AddRange([restart, reset, unpin]);
        Add(buttons);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => OpenWindow());
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "winBar", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };

        // Poll only the Alt interaction frequently. The Explorer style and window list need less frequent work.
        timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            altButton.Update(!s.HideTaskbar);
            if (AltSettingsButton.AltDown) pinnedRevealed = true;
            else if (pinnedRevealed && !Taskbar.VisibleBounds().Any(r => r.Contains(Cursor.Position))) pinnedRevealed = false;
            bool pinnedHidden = s.HidePinnedUntilAlt && !pinnedRevealed;
            bool maximized = s.ContextStyle && Taskbar.ActiveWindowMaximized;
            if (now >= nextVisualRefresh || pinnedHidden != lastPinnedHidden || maximized != lastMaximized)
            {
                nextVisualRefresh = now + 1000;
                lastPinnedHidden = pinnedHidden;
                lastMaximized = maximized;
                int activeStyle = maximized ? s.MaximizedStyle : s.Style;
                if (activeStyle != 0 || lastAppliedStyle > 0)
                    Taskbar.SetAccent(AccentFor[activeStyle], activeStyle == 1 ? 0 : Taskbar.ToAbgr(s.Color));
                lastAppliedStyle = activeStyle;
                Taskbar.Tap(activeStyle > 0, s.TrayHide, pinnedHidden);
                if (s.HideTaskbar) Taskbar.Show(false);
            }
            if (now >= nextClockRefresh)
            {
                nextClockRefresh = now + 500;
                clock.Update(s.HideTaskbar ? 0 : s.ClockLayout, s.ClockOnPrimaryOnly);
            }
            bool openHotkeyDown = AltSettingsButton.OpenHotkeyDown;
            if (openHotkeyDown && !openHotkeyWasDown) OpenWindow();
            openHotkeyWasDown = openHotkeyDown;
            if (now >= nextProgramScan)
            {
                nextProgramScan = now + 2000;
                programHider.Refresh();
            }
        };
        timer.Start();
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(!startHidden && value);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    void OpenWindow() { startHidden = false; Show(); WindowState = FormWindowState.Normal; Activate(); }

    void ExitApp()
    {
        exiting = true;
        timer.Stop();
        altButton.Close();
        clock.Dispose();
        programHider.Dispose();
        Taskbar.SetAccent(0, 0);
        Taskbar.Tap(false, 0, false);
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

    // Without a caption the control spans both columns; with one, caption left and control stretched right.
    void Add(Control c, Label? caption = null)
    {
        if (caption == null) { grid.Controls.Add(c); grid.SetColumnSpan(c, 2); return; }
        grid.Controls.Add(caption);
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(c);
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 16, 6) };

    void Header(string text) =>
        Add(new Label { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, 13, FontStyle.Bold), Margin = new Padding(0, 18, 3, 6) });

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
