using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// The small settings handle appears only while Alt is held over a visible taskbar.
sealed class AltSettingsButton : Form
{
    const int VkAlt = 0x12;
    const int VkControl = 0x11;
    const int VkS = 0x53;
    readonly Action openSettings;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }

    public static bool OpenHotkeyDown => GetAsyncKeyState(VkControl) < 0 && GetAsyncKeyState(VkAlt) < 0 && GetAsyncKeyState(VkS) < 0;
    public static bool PresentationHotkeyDown => GetAsyncKeyState(VkControl) < 0 && GetAsyncKeyState(VkAlt) < 0 && GetAsyncKeyState(0x50) < 0;
    public static bool FolderHotkeyDown => GetAsyncKeyState(VkControl) < 0 && GetAsyncKeyState(VkAlt) < 0 && GetAsyncKeyState(0x4F) < 0;
    public static bool AltDown => GetAsyncKeyState(VkAlt) < 0;
    public static int ProfileHotkey => GetAsyncKeyState(VkControl) < 0 && AltDown
        ? GetAsyncKeyState(0x31) < 0 ? 1 : GetAsyncKeyState(0x32) < 0 ? 2 : 0
        : 0;

    public AltSettingsButton(Action openSettings)
    {
        this.openSettings = openSettings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(148, 36);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        BackColor = Color.FromArgb(29, 36, 53);
        using var outline = new GraphicsPath();
        outline.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), new Size(12, 12));
        Region = new Region(outline);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; } // TOOLWINDOW | NOACTIVATE
    }

    public void Update(bool taskbarEnabled)
    {
        if (!taskbarEnabled || !GetCursorPos(out var cursor)) { Hide(); return; }
        var point = new Point(cursor.X, cursor.Y);
        var taskbar = Taskbar.VisibleBounds().FirstOrDefault(r => r.Contains(point));
        var hoverArea = Bounds;
        hoverArea.Inflate(12, 12); // bridge the small gap between taskbar and button
        if (Visible)
        {
            if (taskbar == Rectangle.Empty && !hoverArea.Contains(point)) Hide();
            return; // Keep the button still while the pointer moves onto it.
        }
        if (GetAsyncKeyState(VkAlt) >= 0 || taskbar == Rectangle.Empty) return;
        var screen = Screen.FromPoint(point).Bounds;
        int x = Math.Clamp(point.X - Width / 2, screen.Left + 8, screen.Right - Width - 8);
        int y = taskbar.Top >= screen.Top + screen.Height / 2 ? taskbar.Top - Height - 8 : taskbar.Bottom + 8;
        Location = new Point(x, y);
        Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(new Rectangle(1, 1, Width - 2, Height - 2), new Size(12, 12));
        using var fill = new SolidBrush(Color.FromArgb(29, 36, 53));
        using var border = new Pen(Color.FromArgb(111, 147, 207));
        e.Graphics.FillPath(fill, shape);
        e.Graphics.DrawPath(border, shape);
        TextRenderer.DrawText(e.Graphics, "⚙  Einstellungen", Font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Hide();
        openSettings();
    }
}

// Paints over the existing clock, preserving its taskbar space and Windows click behavior.
sealed class DesignClock : IDisposable
{
    readonly List<ClockPill> pills = [];
    readonly Dictionary<string, TimeZoneInfo?> zones = new(StringComparer.OrdinalIgnoreCase);

    public void Update(int layout, bool primaryOnly, string? secondTimeZoneId = null, string? secondTimeZoneLabel = null)
    {
        Update(screen => new MonitorClock(primaryOnly && !screen.Primary ? 0 : layout,
            secondTimeZoneId, secondTimeZoneLabel));
    }

    public void Update(Func<Screen, MonitorClock> selectClock)
    {
        var bars = Taskbar.VisibleBounds()
            .Where(r => r.Width > r.Height * 3 && r.Height >= 32 && !Taskbar.CoveredByFullscreenWindow(r))
            .Select(r => (Bounds: r, Clock: selectClock(Screen.FromRectangle(r))))
            .Where(item => item.Clock.Layout is 1 or 2).ToArray();
        while (pills.Count > bars.Length) { pills[^1].Close(); pills.RemoveAt(pills.Count - 1); }
        for (int i = 0; i < bars.Length; i++)
        {
            if (i == pills.Count) pills.Add(new ClockPill());
            var pill = pills[i];
            var bar = bars[i].Bounds;
            var config = bars[i].Clock;
            pill.SetLayout(config.Layout);
            pill.Location = new Point(bar.Right - pill.Width - 9, bar.Top + (bar.Height - pill.Height) / 2);
            pill.SetSecondZone(FindZone(config.SecondaryTimeZoneId), config.SecondaryTimeZoneLabel);
            pill.RefreshTime();
            if (!pill.Visible) pill.Show();
            pill.UpdateHover(bar);
        }
    }

    TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (zones.TryGetValue(id, out var zone)) return zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { zone = null; }
        catch (InvalidTimeZoneException) { zone = null; }
        zones[id] = zone;
        return zone;
    }

    public void Dispose() { foreach (var pill in pills) pill.Close(); pills.Clear(); }

    sealed class ClockPill : Form
    {
        static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
        string? lastMinute;
        int layout = 2;
        TimeZoneInfo? secondZone;
        string? secondZoneLabel;
        ClockHoverCard? hoverCard;

        public ClockPill()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Size = new Size(108, 40);
            DoubleBuffered = true;
            BackColor = Color.FromArgb(26, 34, 51);
            Opacity = 0.99;
            using var outline = new GraphicsPath();
            outline.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), new Size(12, 12));
            Region = new Region(outline);
        }

        public void SetLayout(int value)
        {
            if (layout == value) return;
            layout = value;
            Size = layout == 1 ? new Size(82, 36) : new Size(108, 40);
            using var outline = new GraphicsPath();
            outline.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), new Size(12, 12));
            var old = Region;
            Region = new Region(outline);
            old?.Dispose();
            lastMinute = null;
            Invalidate();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000 | 0x00080000 | 0x20; return p; }
        }

        public void RefreshTime()
        {
            var minute = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            if (minute == lastMinute) return;
            lastMinute = minute;
            Invalidate();
            hoverCard?.Invalidate();
        }

        public void SetSecondZone(TimeZoneInfo? zone, string? label)
        {
            if (secondZone == zone && secondZoneLabel == label) return;
            secondZone = zone;
            secondZoneLabel = label;
            if (zone == null) { hoverCard?.Hide(); return; }
            hoverCard?.SetZone(zone, label);
        }

        public void UpdateHover(Rectangle bar)
        {
            if (secondZone == null) { hoverCard?.Hide(); return; }
            var cursor = Cursor.Position;
            if (!Bounds.Contains(cursor) && (hoverCard == null || !hoverCard.Visible || !hoverCard.Bounds.Contains(cursor)))
            {
                hoverCard?.Hide();
                return;
            }
            hoverCard ??= new ClockHoverCard(secondZone, secondZoneLabel);
            var screen = Screen.FromRectangle(bar).Bounds;
            int x = Math.Clamp(Right - hoverCard.Width, screen.Left + 8, screen.Right - hoverCard.Width - 8);
            int y = bar.Top > screen.Top + screen.Height / 2 ? bar.Top - hoverCard.Height - 8 : bar.Bottom + 8;
            hoverCard.Location = new Point(x, y);
            if (!hoverCard.Visible) hoverCard.Show();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            hoverCard?.Close();
            base.OnFormClosed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(1, 1, Width - 2, Height - 2), new Size(12, 12));
            using var fill = new SolidBrush(Color.FromArgb(26, 34, 51));
            using var border = new Pen(Color.FromArgb(75, 106, 153));
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(border, shape);
            using var timeFont = new Font("Segoe UI Semibold", 13, FontStyle.Bold);
            using var dateFont = new Font("Segoe UI", 7.5f);
            var now = DateTime.Now;
            TextRenderer.DrawText(e.Graphics, now.ToString("HH:mm", German), timeFont,
                layout == 1 ? new Rectangle(4, 7, Width - 8, 23) : new Rectangle(4, 2, Width - 8, 23),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            if (layout == 2)
                TextRenderer.DrawText(e.Graphics, now.ToString("ddd, dd.MM.", German), dateFont, new Rectangle(4, 24, Width - 8, 13),
                    Color.FromArgb(165, 198, 245), TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    sealed class ClockHoverCard : Form
    {
        static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
        TimeZoneInfo zone;
        string? label;

        public ClockHoverCard(TimeZoneInfo zone, string? label)
        {
            this.zone = zone;
            this.label = label;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Size = new Size(220, 104);
            BackColor = Color.FromArgb(27, 37, 54);
            DoubleBuffered = true;
            Opacity = 0.98;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), new Size(16, 16));
            Region = new Region(shape);
        }

        public void SetZone(TimeZoneInfo value, string? displayLabel)
        {
            zone = value;
            label = displayLabel;
            Invalidate();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000 | 0x20; return p; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(1, 1, Width - 2, Height - 2), new Size(16, 16));
            using var fill = new SolidBrush(Color.FromArgb(27, 37, 54));
            using var border = new Pen(Color.FromArgb(94, 119, 154));
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(border, shape);
            using var line = new Pen(Color.FromArgb(62, 82, 111));
            e.Graphics.DrawLine(line, 18, 52, Width - 18, 52);
            using var nameFont = new Font("Segoe UI", 9);
            using var timeFont = new Font("Segoe UI Semibold", 13, FontStyle.Bold);
            var local = DateTimeOffset.Now;
            var remote = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(e.Graphics, "Hier", nameFont, new Rectangle(16, 12, 123, 28), Color.FromArgb(171, 193, 225), flags);
            TextRenderer.DrawText(e.Graphics, local.ToString("HH:mm", German), timeFont, new Rectangle(141, 10, 63, 29), Color.White,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, string.IsNullOrWhiteSpace(label) ? zone.StandardName : label, nameFont,
                new Rectangle(16, 62, 123, 28), Color.FromArgb(171, 193, 225), flags);
            TextRenderer.DrawText(e.Graphics, remote.ToString("HH:mm", German), timeFont, new Rectangle(141, 60, 63, 29), Color.White,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

sealed record AppWindow(IntPtr Handle, string Path, string Title)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

static class AppWindows
{
    delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    public static List<AppWindow> List()
    {
        var windows = new List<AppWindow>();
        var paths = new Dictionary<uint, string?>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
            if ((exStyle & 0x80) != 0 && (exStyle & 0x40000) == 0) return true; // tool window
            if (GetWindow(hwnd, 4) != IntPtr.Zero && (exStyle & 0x40000) == 0) return true; // owned
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return true;
            if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = ProcessPath(pid);
            if (path == null || System.IO.Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return true;
            var title = new StringBuilder(512);
            GetWindowTextW(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;
            windows.Add(new AppWindow(hwnd, path, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    static string? ProcessPath(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }
}

[ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
class TaskbarListClass { }

[ComImport, Guid("56FDF342-FD6D-11D0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITaskbarList
{
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
}

sealed class ProgramHider : IDisposable
{
    readonly ITaskbarList? taskbar;
    readonly Dictionary<IntPtr, string> hidden = [];
    HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    bool focusLens, revealFocus;
    IntPtr focusedWindow;

    public ProgramHider()
    {
        try { taskbar = (ITaskbarList)new TaskbarListClass(); taskbar.HrInit(); }
        catch (COMException) { taskbar = null; }
    }

    public void Update(IEnumerable<string> paths)
    {
        selected = new HashSet<string>(paths.Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.Path.IsPathFullyQualified(p)), StringComparer.OrdinalIgnoreCase);
        Refresh();
    }

    public void SetFocusLens(bool enabled, bool reveal, IntPtr foreground)
    {
        if (!enabled && !focusLens) return;
        if (focusLens == enabled && revealFocus == reveal && focusedWindow == foreground) return;
        focusLens = enabled;
        revealFocus = reveal;
        focusedWindow = foreground;
        Refresh();
    }

    public void Refresh()
    {
        if (taskbar == null || (selected.Count == 0 && hidden.Count == 0 && !focusLens)) return;
        var windows = AppWindows.List();
        var current = windows.ToDictionary(w => w.Handle);
        bool lensActive = focusLens && !revealFocus && current.ContainsKey(focusedWindow);
        bool ShouldHide(AppWindow window) => selected.Contains(window.Path) || (lensActive && window.Handle != focusedWindow);
        foreach (var (hwnd, path) in hidden.ToArray())
        {
            if (!current.TryGetValue(hwnd, out var window) || !window.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            { hidden.Remove(hwnd); continue; }
            if (ShouldHide(window)) continue;
            try { taskbar.AddTab(hwnd); } catch (COMException) { }
            hidden.Remove(hwnd);
        }
        foreach (var window in windows.Where(w => ShouldHide(w) && !hidden.ContainsKey(w.Handle)))
        {
            try { taskbar.DeleteTab(window.Handle); hidden[window.Handle] = window.Path; }
            catch (COMException) { }
        }
    }

    public void Dispose()
    {
        if (taskbar == null) return;
        var windows = AppWindows.List().ToDictionary(w => w.Handle);
        foreach (var (hwnd, path) in hidden)
            if (windows.TryGetValue(hwnd, out var window) && window.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                try { taskbar.AddTab(hwnd); } catch (COMException) { }
        hidden.Clear();
        Marshal.FinalReleaseComObject(taskbar);
    }
}

sealed class HiddenProgramsDialog : Form
{
    readonly ListView list = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill };
    public List<string> SelectedPaths => list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (string)i.Tag!).ToList();

    public HiddenProgramsDialog(IEnumerable<string> selectedPaths)
    {
        Text = "Programme auf der Taskleiste ausblenden";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 460);
        MinimumSize = new Size(500, 320);
        list.Columns.Add("Programm", 180);
        list.Columns.Add("Pfad", 460);
        var selected = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        var paths = AppWindows.List().Select(w => w.Path).Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(System.IO.Path.GetFileName);
        foreach (var path in paths) AddPath(path, selected.Contains(path));
        var info = new Label { Text = "Häkchen = Schaltfläche ausblenden. Laufende Programme stehen in der Liste; weitere EXE-Dateien kannst du hinzufügen.", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(12, 10, 12, 10) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
        var ok = new Button { Text = "Speichern", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        var browse = new Button { Text = "EXE hinzufügen…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var pick = new OpenFileDialog { Filter = "Programme (*.exe)|*.exe", CheckFileExists = true, Title = "Programm auswählen" };
            if (pick.ShowDialog(this) == DialogResult.OK) AddPath(pick.FileName, true);
        };
        buttons.Controls.AddRange([ok, cancel, browse]);
        Controls.Add(list);
        Controls.Add(info);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    void AddPath(string path, bool selected)
    {
        foreach (ListViewItem item in list.Items)
            if (string.Equals((string)item.Tag!, path, StringComparison.OrdinalIgnoreCase)) { item.Checked = selected; return; }
        var row = new ListViewItem(System.IO.Path.GetFileName(path)) { Tag = path, Checked = selected };
        row.SubItems.Add(path);
        list.Items.Add(row);
    }
}
