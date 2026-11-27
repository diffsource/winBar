using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

// DWM renders live previews directly. No screenshots, frame loop, or idle polling.
sealed class StackWindowPreviewPopup : Form
{
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] struct ThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

    const int TileWidth = 180;
    const int TileHeight = 132;
    const int Gap = 10;
    readonly AppWindow[] windows;
    readonly List<IntPtr> thumbnails = [];
    readonly string title;
    readonly string emptyMessage;
    Rectangle anchor;
    int closeGeneration;

    public Guid GroupId { get; }

    public static StackWindowPreviewPopup ShowFor(AppStackGroup group, Rectangle anchor)
    {
        var selected = (group.LaunchPaths ?? []).Select(ResolvePath).Where(path => path != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matching = selected.Count == 0 ? [] : AppWindows.List().Where(window => selected.Contains(window.Path)).ToArray();
        var popup = new StackWindowPreviewPopup(group.Id, group.Name, matching.Take(6).ToArray(),
            selected.Count == 0
                ? Localization.T("Choose programs for ‘Launch all’ in the stack editor to preview their windows here.",
                    "Wähle im Stapel-Editor Programme für ‚Alle starten‘ aus, um ihre Fenster hier zu sehen.")
                : Localization.T("No matching windows are open.", "Keine passenden Fenster geöffnet."), matching.Length);
        popup.PositionAt(anchor);
        popup.Show();
        return popup;
    }

    static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return path;
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            var target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or COMException or ArgumentException or UnauthorizedAccessException) { return null; }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    StackWindowPreviewPopup(Guid groupId, string name, AppWindow[] windows, string emptyMessage, int total)
    {
        GroupId = groupId;
        this.windows = windows;
        this.emptyMessage = emptyMessage;
        title = total > windows.Length
            ? Localization.T($"{name} · {total} windows (first {windows.Length})", $"{name} · {total} Fenster (erste {windows.Length})")
            : Localization.T($"{name} · {total} windows", $"{name} · {total} Fenster");
        Text = $"winBar · {name}";
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(26, 37, 55);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        int columns = Math.Min(3, windows.Length);
        int rows = windows.Length == 0 ? 0 : (windows.Length + 2) / 3;
        ClientSize = windows.Length == 0 ? new Size(370, 145)
            : new Size(28 + columns * TileWidth + (columns - 1) * Gap, 62 + rows * TileHeight + (rows - 1) * Gap);
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), new Size(17, 17));
        Region = new Region(shape);
    }

    void PositionAt(Rectangle anchor)
    {
        this.anchor = anchor;
        var area = Screen.FromRectangle(anchor).WorkingArea;
        int x = Math.Clamp(anchor.Left + (anchor.Width - Width) / 2, area.Left + 8, area.Right - Width - 8);
        int y = anchor.Top >= area.Top + area.Height / 2 ? anchor.Top - Height - 8 : anchor.Bottom + 8;
        Location = new Point(x, Math.Clamp(y, area.Top + 8, area.Bottom - Height - 8));
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        for (int i = 0; i < windows.Length; i++)
        {
            if (!IsWindow(windows[i].Handle) || DwmRegisterThumbnail(Handle, windows[i].Handle, out var thumbnail) != 0)
                continue;
            thumbnails.Add(thumbnail);
            var tile = TileBounds(i);
            var box = new Rectangle(tile.Left + 7, tile.Top + 7, tile.Width - 14, 92);
            if (DwmQueryThumbnailSourceSize(thumbnail, out var source) == 0 && source.Width > 0 && source.Height > 0)
            {
                double scale = Math.Min((double)box.Width / source.Width, (double)box.Height / source.Height);
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                box = new Rectangle(box.Left + (box.Width - width) / 2, box.Top + (box.Height - height) / 2, width, height);
            }
            var properties = new ThumbnailProperties
            {
                Flags = 0x1 | 0x8 | 0x4, Destination = new NativeRect { Left = box.Left, Top = box.Top, Right = box.Right, Bottom = box.Bottom },
                Opacity = 255, Visible = true
            };
            if (DwmUpdateThumbnailProperties(thumbnail, ref properties) != 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                thumbnails.RemoveAt(thumbnails.Count - 1);
            }
        }
    }

    Rectangle TileBounds(int index) => new(14 + (index % 3) * (TileWidth + Gap), 48 + (index / 3) * (TileHeight + Gap), TileWidth, TileHeight);

    public void CancelClose() => closeGeneration++;

    public void ScheduleClose()
    {
        int generation = ++closeGeneration;
        _ = CloseAfterGraceAsync(generation);
    }

    async Task CloseAfterGraceAsync(int generation)
    {
        await Task.Delay(260);
        if (generation == closeGeneration && !IsDisposed &&
            !Bounds.Contains(Cursor.Position) && !anchor.Contains(Cursor.Position)) Close();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); CancelClose(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ScheduleClose(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        for (int i = 0; i < windows.Length; i++)
        {
            if (!TileBounds(i).Contains(e.Location)) continue;
            var target = windows[i].Handle;
            if (IsWindow(target)) { ShowWindowAsync(target, 9); SetForegroundWindow(target); }
            Close();
            return;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Color.FromArgb(104, 135, 176));
        using var outline = new GraphicsPath();
        outline.AddRoundedRectangle(new Rectangle(1, 1, Width - 3, Height - 3), new Size(17, 17));
        e.Graphics.DrawPath(border, outline);
        using var header = new Font("Segoe UI Semibold", 11, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, title, header, new Rectangle(16, 11, Width - 32, 28), Color.White,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        if (windows.Length == 0)
        {
            TextRenderer.DrawText(e.Graphics, emptyMessage, Font, new Rectangle(18, 49, Width - 36, 80),
                Color.FromArgb(184, 204, 231), TextFormatFlags.WordBreak);
            return;
        }
        for (int i = 0; i < windows.Length; i++)
        {
            var tile = TileBounds(i);
            using var fill = new SolidBrush(Color.FromArgb(43, 58, 80));
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(tile, new Size(11, 11));
            e.Graphics.FillPath(fill, shape);
            TextRenderer.DrawText(e.Graphics, windows[i].Title, Font,
                new Rectangle(tile.Left + 9, tile.Bottom - 30, tile.Width - 18, 23), Color.White,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closeGeneration++;
        foreach (var thumbnail in thumbnails) DwmUnregisterThumbnail(thumbnail);
        thumbnails.Clear();
        base.OnFormClosed(e);
        Dispose();
    }
}
