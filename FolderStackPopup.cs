using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

// A small, on-demand shelf of folders explicitly chosen by the user.
// It does not scan folder contents or keep a filesystem watcher alive.
sealed class FolderStackPopup : Form
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    readonly Rectangle anchor;

    static Bitmap FolderIcon(string path)
    {
        var info = new ShellFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            try { return Icon.FromHandle(info.Icon).ToBitmap(); }
            finally { DestroyIcon(info.Icon); }
        }
        return SystemIcons.WinLogo.ToBitmap();
    }

    public static void ShowFolders(IEnumerable<string> selectedFolders, Rectangle anchor)
    {
        var folders = selectedFolders
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        var popup = new FolderStackPopup(folders, anchor);
        popup.Show();
        popup.Activate();
    }

    FolderStackPopup(string[] folders, Rectangle anchor)
    {
        this.anchor = anchor;
        Text = "Ordnerstapel";
        ClientSize = new Size(478, 326);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(27, 37, 54);
        DoubleBuffered = true;
        Opacity = 0.98;
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), new Size(18, 18));
        Region = new Region(shape);

        Controls.Add(new Label
        {
            Text = "Ordnerstapel", Font = new Font("Segoe UI Semibold", 16, FontStyle.Bold),
            ForeColor = Color.White, Location = new Point(23, 15), Size = new Size(320, 33)
        });
        Controls.Add(new Label
        {
            Text = "Deine Ordner, ein Klick entfernt", ForeColor = Color.FromArgb(171, 193, 225),
            Location = new Point(25, 48), Size = new Size(360, 25)
        });
        var close = new Button
        {
            Text = "×", AccessibleName = "Ordnerstapel schließen", Location = new Point(431, 20), Size = new Size(28, 28),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(42, 58, 80), ForeColor = Color.White, TabStop = true
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        Controls.Add(close);

        var grid = new FlowLayoutPanel
        {
            Location = new Point(22, 85), Size = new Size(434, 220), AutoScroll = true,
            WrapContents = true, Padding = new Padding(3), BackColor = Color.Transparent
        };
        Controls.Add(grid);
        if (folders.Length == 0)
        {
            grid.Controls.Add(new Label
            {
                Text = "Noch keine Ordner ausgewählt. Füge sie in den winBar-Einstellungen hinzu.",
                ForeColor = Color.FromArgb(171, 193, 225), Size = new Size(390, 72), Margin = new Padding(10, 20, 0, 0)
            });
        }
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrWhiteSpace(name)) name = folder;
            var tile = new Button
            {
                Text = name,
                AccessibleName = $"Ordner {name} öffnen",
                Size = new Size(99, 92), Margin = new Padding(3), Padding = new Padding(4),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 55, 77), ForeColor = Color.White,
                Font = new Font("Segoe UI", 9), TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter, TextImageRelation = TextImageRelation.ImageAboveText,
                AutoEllipsis = true
            };
            tile.FlatAppearance.BorderColor = Color.FromArgb(63, 84, 114);
            tile.Image = FolderIcon(folder);
            tile.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                { MessageBox.Show(this, $"Ordner konnte nicht geöffnet werden:\n{ex.Message}", "winBar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                Close();
            };
            grid.Controls.Add(tile);
        }
        PositionAtAnchor();
    }

    void PositionAtAnchor()
    {
        var area = Screen.FromRectangle(anchor).WorkingArea;
        int x = Math.Clamp(anchor.Left + (anchor.Width - Width) / 2, area.Left + 8, area.Right - Width - 8);
        int y = anchor.Top >= area.Top + area.Height / 2 ? anchor.Top - Height - 10 : anchor.Bottom + 10;
        Location = new Point(x, Math.Clamp(y, area.Top + 8, area.Bottom - Height - 8));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = new GraphicsPath();
        shape.AddRoundedRectangle(new Rectangle(1, 1, Width - 2, Height - 2), new Size(18, 18));
        using var border = new Pen(Color.FromArgb(94, 119, 154));
        e.Graphics.DrawPath(border, shape);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var image in Controls.OfType<FlowLayoutPanel>().SelectMany(panel => panel.Controls.OfType<Button>()).Select(button => button.Image))
            image?.Dispose();
        base.OnFormClosed(e);
        Dispose();
    }
}
