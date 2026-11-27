using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class WorkspaceScene
{
    public string Name { get; set; } = Localization.T("Workspace", "Arbeitsbereich");
    public List<string> Apps { get; set; } = [];
    public List<WorkspaceWindowPlacement> Windows { get; set; } = [];
}

sealed class WorkspaceWindowPlacement
{
    public string AppPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Monitor { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

// Capture and restore run only after an explicit click. No persistent window scan or watcher.
static class WorkspaceController
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public POINT MinPosition, MaxPosition;
        public RECT NormalPosition;
    }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    public static WorkspaceScene Capture(string name, IEnumerable<string> selectedApps)
    {
        var apps = selectedApps.Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        var selected = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);
        var cleanName = name.Trim();
        var scene = new WorkspaceScene { Name = cleanName.Length > 48 ? cleanName[..48] : cleanName, Apps = apps };
        foreach (var window in AppWindows.List())
        {
            if (!selected.Contains(window.Path) || IsIconic(window.Handle) || !GetWindowRect(window.Handle, out var rect)) continue;
            var screen = Screen.FromHandle(window.Handle);
            var work = screen.WorkingArea;
            bool maximized = IsZoomed(window.Handle);
            if (maximized)
            {
                var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(window.Handle, ref placement)) continue;
                // GetWindowPlacement returns workspace coordinates. Convert to screen coordinates
                // before normalizing, accounting for taskbars at the top or left edge.
                rect = placement.NormalPosition;
                int dx = work.Left - screen.Bounds.Left;
                int dy = work.Top - screen.Bounds.Top;
                rect.Left += dx; rect.Right += dx; rect.Top += dy; rect.Bottom += dy;
            }
            if (work.Width == 0 || work.Height == 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
            scene.Windows.Add(new WorkspaceWindowPlacement
            {
                AppPath = window.Path, Title = window.Title, Monitor = screen.DeviceName,
                X = (double)(rect.Left - work.Left) / work.Width,
                Y = (double)(rect.Top - work.Top) / work.Height,
                Width = (double)(rect.Right - rect.Left) / work.Width,
                Height = (double)(rect.Bottom - rect.Top) / work.Height,
                Maximized = maximized
            });
        }
        return scene;
    }

    public static async Task<(int Launched, int Placed)> RestoreAsync(WorkspaceScene scene, CancellationToken token)
    {
        var apps = (scene.Apps ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var current = AppWindows.List();
        int launched = 0;
        foreach (var path in apps)
        {
            if (token.IsCancellationRequested) break;
            if (current.Any(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase)) || !File.Exists(path)) continue;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); launched++; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
        }

        var placements = (scene.Windows ?? []).Where(p => apps.Contains(p.AppPath, StringComparer.OrdinalIgnoreCase)).Take(40).ToArray();
        var placed = new HashSet<int>();
        var assignedHandles = new HashSet<IntPtr>();
        for (int attempt = 0; attempt < 20 && !token.IsCancellationRequested; attempt++)
        {
            current = AppWindows.List();
            for (int i = 0; i < placements.Length; i++)
            {
                if (placed.Contains(i)) continue;
                var target = placements[i];
                var candidates = current.Where(w => !assignedHandles.Contains(w.Handle) &&
                    string.Equals(w.Path, target.AppPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                var window = candidates.FirstOrDefault(w => w.Title == target.Title) ?? candidates.FirstOrDefault();
                if (window == null) continue;
                assignedHandles.Add(window.Handle);
                if (!Move(window.Handle, target)) continue;
                placed.Add(i);
            }
            if (placed.Count == placements.Length || attempt == 19) break;
            await Task.Delay(500, token);
        }
        return (launched, placed.Count);
    }

    static bool Move(IntPtr hwnd, WorkspaceWindowPlacement saved)
    {
        var screen = Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, saved.Monitor, StringComparison.OrdinalIgnoreCase))
            ?? Screen.PrimaryScreen;
        if (screen == null) return false;
        var work = screen.WorkingArea;
        var bounds = TargetBounds(saved, work);
        if (bounds == Rectangle.Empty) return false;
        ShowWindow(hwnd, 9); // SW_RESTORE before moving a previously maximized window
        if (!SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                0x0004 | 0x0010 | 0x4000)) return false; // NOZORDER | NOACTIVATE | ASYNCWINDOWPOS
        if (saved.Maximized) ShowWindow(hwnd, 3);
        return true;
    }

    internal static Rectangle TargetBounds(WorkspaceWindowPlacement saved, Rectangle work)
    {
        if (work.Width < 180 || work.Height < 120 ||
            !double.IsFinite(saved.X) || !double.IsFinite(saved.Y) ||
            !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)) return Rectangle.Empty;
        int width = Math.Clamp((int)(work.Width * Math.Clamp(saved.Width, 0.1, 1.5)), 180, work.Width);
        int height = Math.Clamp((int)(work.Height * Math.Clamp(saved.Height, 0.1, 1.5)), 120, work.Height);
        int x = Math.Clamp(work.Left + (int)(work.Width * Math.Clamp(saved.X, -1, 2)), work.Left, work.Right - width);
        int y = Math.Clamp(work.Top + (int)(work.Height * Math.Clamp(saved.Y, -1, 2)), work.Top, work.Bottom - height);
        return new Rectangle(x, y, width, height);
    }
}

sealed class WorkspaceManagerDialog : Form
{
    readonly Settings settings;
    readonly ListBox list = new();
    readonly Label emptyState = new();
    readonly Label status = new();
    readonly CancellationTokenSource stop = new();

    public WorkspaceManagerDialog(Settings settings)
    {
        this.settings = settings;
        Text = Localization.T("winBar · Workspaces", "winBar · Arbeitsbereiche");
        ClientSize = new Size(540, 380);
        MinimumSize = new Size(500, 340);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);
        Controls.Add(new Label
        {
            Text = Localization.T("Workspaces", "Arbeitsbereiche"), Font = new Font("Segoe UI Semibold", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 42, 63), Location = new Point(20, 17), Size = new Size(400, 40)
        });
        Controls.Add(new Label
        {
            Text = Localization.T("Launch selected apps and restore their windows to their places.", "Starte ausgewählte Apps und stelle ihre Fenster wieder an ihren Platz."),
            ForeColor = Color.FromArgb(68, 82, 105), Location = new Point(23, 58), Size = new Size(480, 44)
        });
        list.Location = new Point(22, 108);
        list.Size = new Size(492, 180);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.DisplayMember = nameof(WorkspaceScene.Name);
        Controls.Add(list);
        emptyState.Text = Localization.T("No workspaces yet.\nChoose ‘New from open apps’ to create one.", "Noch keine Arbeitsbereiche.\nKlicke auf „Neu aus offenen Apps“, um einen anzulegen.");
        emptyState.TextAlign = ContentAlignment.MiddleCenter;
        emptyState.ForeColor = Color.FromArgb(105, 119, 142);
        emptyState.BackColor = Color.White;
        emptyState.Location = new Point(60, 163);
        emptyState.Size = new Size(412, 64);
        Controls.Add(emptyState);
        var create = Button(Localization.T("New from open apps…", "Neu aus offenen Apps…"), 22, 302, 172);
        create.Click += (_, _) => CreateScene();
        var launch = Button(Localization.T("Open", "Öffnen"), 204, 302, 90);
        launch.Click += async (_, _) => await LaunchSelectedAsync();
        var remove = Button(Localization.T("Remove", "Entfernen"), 304, 302, 100);
        remove.Click += (_, _) => RemoveSelected();
        status.Location = new Point(23, 345);
        status.Size = new Size(490, 24);
        status.ForeColor = Color.FromArgb(68, 99, 151);
        status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(status);
        foreach (var scene in settings.Workspaces ?? []) list.Items.Add(scene);
        RefreshEmptyState();
    }

    void RefreshEmptyState() => emptyState.Visible = list.Items.Count == 0;

    Button Button(string text, int x, int y, int width)
    {
        var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        Controls.Add(button);
        return button;
    }

    void CreateScene()
    {
        if (list.Items.Count >= 12) { status.Text = Localization.T("Up to twelve workspaces.", "Maximal zwölf Arbeitsbereiche."); return; }
        using var capture = new WorkspaceCaptureDialog();
        if (capture.ShowDialog(this) != DialogResult.OK) return;
        var scene = WorkspaceController.Capture(capture.SceneName, capture.SelectedApps);
        settings.Workspaces ??= [];
        settings.Workspaces.Add(scene);
        settings.Save();
        list.Items.Add(scene);
        list.SelectedItem = scene;
        RefreshEmptyState();
        status.Text = Localization.T($"Saved {scene.Apps.Count} apps and {scene.Windows.Count} windows.", $"{scene.Apps.Count} Apps und {scene.Windows.Count} Fenster gespeichert.");
    }

    async Task LaunchSelectedAsync()
    {
        if (list.SelectedItem is not WorkspaceScene scene) return;
        status.Text = Localization.T("Opening workspace…", "Arbeitsbereich wird geöffnet…");
        try
        {
            var result = await WorkspaceController.RestoreAsync(scene, stop.Token);
            if (!IsDisposed) status.Text = Localization.T($"Launched {result.Launched} apps · placed {result.Placed} windows.", $"{result.Launched} Apps gestartet · {result.Placed} Fenster platziert.");
        }
        catch (OperationCanceledException) { }
    }

    void RemoveSelected()
    {
        if (list.SelectedItem is not WorkspaceScene scene) return;
        settings.Workspaces?.Remove(scene);
        settings.Save();
        list.Items.Remove(scene);
        RefreshEmptyState();
        status.Text = Localization.T("Workspace removed.", "Arbeitsbereich entfernt.");
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { stop.Cancel(); base.OnFormClosed(e); stop.Dispose(); }
}

sealed class WorkspaceCaptureDialog : Form
{
    readonly TextBox name = new();
    readonly CheckedListBox apps = new();
    readonly List<string> paths = [];
    readonly ToolTip pathTip = new();
    public string SceneName => name.Text;
    public IEnumerable<string> SelectedApps => apps.CheckedIndices.Cast<int>().Select(index => paths[index]);

    public WorkspaceCaptureDialog()
    {
        Text = Localization.T("Save workspace", "Arbeitsbereich speichern");
        ClientSize = new Size(590, 450);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);
        Controls.Add(new Label { Text = "Name", Location = new Point(20, 18), Size = new Size(100, 25) });
        name.Location = new Point(20, 45);
        name.Size = new Size(545, 29);
        name.MaxLength = 48;
        name.Text = Localization.T("New workspace", "Neuer Arbeitsbereich");
        Controls.Add(name);
        Controls.Add(new Label
        {
            Text = Localization.T("Choose apps to launch when you open this workspace. Their current window positions are saved too.",
                "Wähle Apps, die beim Öffnen gestartet werden sollen. Ihre aktuellen Fensterpositionen werden mitgespeichert."),
            Location = new Point(20, 90), Size = new Size(540, 52)
        });
        apps.Location = new Point(20, 146);
        apps.Size = new Size(545, 230);
        apps.CheckOnClick = true;
        apps.MouseMove += (_, e) =>
        {
            int index = apps.IndexFromPoint(e.Location);
            pathTip.SetToolTip(apps, index >= 0 && index < paths.Count ? paths[index] : "");
        };
        Controls.Add(apps);
        foreach (var app in AppWindows.List().GroupBy(w => w.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())) AddPath(app.Path);
        var browse = new Button { Text = Localization.T("Add EXE…", "EXE hinzufügen…"), Location = new Point(20, 390), Size = new Size(145, 34) };
        browse.Click += (_, _) =>
        {
            using var pick = new OpenFileDialog { Filter = Localization.T("Programs (*.exe)|*.exe", "Programme (*.exe)|*.exe"), CheckFileExists = true };
            if (pick.ShowDialog(this) == DialogResult.OK) AddPath(pick.FileName, true);
        };
        Controls.Add(browse);
        var save = new Button { Text = Localization.T("Save", "Speichern"), Location = new Point(455, 390), Size = new Size(110, 34), DialogResult = DialogResult.OK };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || !apps.CheckedIndices.Cast<int>().Any())
            { DialogResult = DialogResult.None; MessageBox.Show(this, Localization.T("Choose a name and at least one app.", "Bitte einen Namen und mindestens eine App auswählen.")); }
        };
        Controls.Add(save);
        AcceptButton = save;
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { pathTip.Dispose(); base.OnFormClosed(e); }

    void AddPath(string path, bool select = false)
    {
        if (paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        paths.Add(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        if (parent.Length > 28) parent = parent[..27] + "…";
        int index = apps.Items.Add($"{Path.GetFileName(path)}    ·    {parent}");
        if (select) apps.SetItemChecked(index, true);
    }
}
