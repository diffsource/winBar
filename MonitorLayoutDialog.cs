// Edits per-display layouts on copies. The caller saves Layouts only after OK.
internal sealed class MonitorLayoutDialog : Form
{
    readonly Settings baseline;
    readonly bool nativeReady;
    readonly List<Screen> screens;
    readonly ListBox displayList = new();
    readonly CheckBox appearanceEnabled = new();
    readonly ComboBox style = new();
    readonly Button colorButton = new();
    readonly TrackBar opacity = new();
    readonly CheckBox autoContrast = new();
    readonly CheckBox appAccent = new();
    readonly CheckBox contextStyle = new();
    readonly ComboBox maximizedStyle = new();
    readonly CheckBox clockEnabled = new();
    readonly ComboBox clockLayout = new();
    readonly ComboBox secondZone = new();
    readonly CheckBox rightEnabled = new();
    readonly ComboBox[] rightModes = [new(), new(), new(), new()];
    readonly CheckBox pinnedEnabled = new();
    readonly CheckBox hidePinned = new();
    readonly NumericUpDown keepPinned = new();
    readonly CheckBox stacksEnabled = new();
    readonly CheckedListBox stacks = new();
    readonly List<TimeZoneInfo> timeZones = [.. TimeZoneInfo.GetSystemTimeZones()];
    Screen? selectedScreen;
    MonitorLayout? draft;
    bool loading;

    public List<MonitorLayout> Layouts { get; }

    public MonitorLayoutDialog(Settings baseline, IEnumerable<MonitorLayout>? layouts,
        bool nativePerMonitorReady = false)
    {
        RegisterCopy();
        this.baseline = baseline;
        nativeReady = nativePerMonitorReady;
        Layouts = (layouts ?? []).Select(layout => layout.Copy()).ToList();
        screens = [.. Screen.AllScreens.OrderByDescending(screen => screen.Primary).ThenBy(screen => screen.DeviceName)];
        Text = "winBar · Monitore";
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(680, 500);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(246, 248, 252);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        Controls.Add(shell);
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(27, 41, 61) };
        header.Controls.Add(new Label { Text = "Monitore", Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold),
            ForeColor = Color.White, AutoSize = true, Location = new Point(22, 12) });
        header.Controls.Add(new Label { Text = nativeReady ? "Design, Uhr und Elemente pro Bildschirm." : "Die Uhr auf jedem Bildschirm passend einstellen.",
            ForeColor = Color.FromArgb(180, 202, 230), AutoSize = true, Location = new Point(25, 53) });
        header.Paint += (_, e) => { using var brush = new SolidBrush(Color.FromArgb(102, 154, 219)); e.Graphics.FillRectangle(brush, 0, header.Height - 3, header.Width, 3); };
        shell.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18, 16, 18, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
        shell.Controls.Add(body, 0, 1);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Margin = new Padding(0, 0, 14, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Title("Verbunden"), 0, 0);
        displayList.Dock = DockStyle.Fill;
        displayList.IntegralHeight = false;
        displayList.BorderStyle = BorderStyle.FixedSingle;
        displayList.SelectedIndexChanged += (_, _) => SelectDisplay();
        left.Controls.Add(displayList, 0, 1);
        left.Controls.Add(new Label { Text = "Getrennte Bildschirme behalten ihre Einstellungen.", AutoSize = true,
            MaximumSize = new Size(240, 0), ForeColor = Color.FromArgb(93, 110, 133), Margin = new Padding(3, 10, 3, 2) }, 0, 2);
        body.Controls.Add(left, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        body.Controls.Add(tabs, 1, 0);
        if (nativeReady) tabs.TabPages.Add(BuildAppearancePage());
        tabs.TabPages.Add(BuildClockPage());
        if (nativeReady) tabs.TabPages.Add(BuildElementsPage());

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(20, 12, 18, 12) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label { Text = "Änderungen gelten nach dem Speichern.", AutoSize = true,
            ForeColor = Color.FromArgb(93, 110, 133), Anchor = AnchorStyles.Left }, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, MinimumSize = new Size(96, 32) };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var save = new Button { Text = "Speichern", AutoSize = true, MinimumSize = new Size(96, 32),
            BackColor = Color.FromArgb(62, 107, 174), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => { SaveCurrent(); DialogResult = DialogResult.OK; Close(); };
        actions.Controls.AddRange([save, cancel]);
        footer.Controls.Add(actions, 1, 0);
        shell.Controls.Add(footer, 0, 2);
        AcceptButton = save;
        CancelButton = cancel;

        foreach (var screen in screens) displayList.Items.Add(new DisplayChoice(screen));
        if (displayList.Items.Count > 0) displayList.SelectedIndex = 0;
        else tabs.Enabled = false;
        loading = true;
        try { Localization.Apply(this); }
        finally { loading = false; }
    }

    static void RegisterCopy()
    {
        Localization.Register("winBar · Monitors", "winBar · Monitore");
        Localization.Register("Monitors", "Monitore");
        Localization.Register("Design, clock and elements for each display.", "Design, Uhr und Elemente pro Bildschirm.");
        Localization.Register("Set the clock for each display.", "Die Uhr auf jedem Bildschirm passend einstellen.");
        Localization.Register("Connected", "Verbunden");
        Localization.Register("Disconnected displays keep their settings.", "Getrennte Bildschirme behalten ihre Einstellungen.");
        Localization.Register("Changes take effect when you save.", "Änderungen gelten nach dem Speichern.");
        Localization.Register("Cancel", "Abbrechen");
        Localization.Register("Save", "Speichern");
        Localization.Register("Appearance", "Aussehen");
        Localization.Register("Use a separate look on this monitor", "Eigenes Design auf diesem Monitor");
        Localization.Register("Windows default", "Windows-Standard");
        Localization.Register("Transparent", "Transparent");
        Localization.Register("Tinted", "Getönt");
        Localization.Register("Blur", "Blur");
        Localization.Register("Acrylic", "Acrylic");
        Localization.Register("Style", "Stil");
        Localization.Register("Color", "Farbe");
        Localization.Register("Opacity", "Deckkraft");
        Localization.Register("Windows accent as tint", "Windows-Akzent als Tönung");
        Localization.Register("Active app color", "Farbe der aktiven App");
        Localization.Register("Different style for maximized windows", "Anderer Stil bei maximiertem Fenster");
        Localization.Register("Then", "Dann");
        Localization.Register("Clock", "Uhr");
        Localization.Register("Use a separate clock on this monitor", "Eigene Uhr auf diesem Monitor");
        Localization.Register("Off", "Aus");
        Localization.Register("Time only", "Nur Uhrzeit");
        Localization.Register("Time and date", "Uhrzeit und Datum");
        Localization.Register("Display", "Anzeige");
        Localization.Register("No second time zone", "Keine zweite Zeitzone");
        Localization.Register("On hover", "Beim Darüberfahren");
        Localization.Register("This setting applies to this display. The second time zone appears on hover.", "Diese Einstellung gilt für diesen Bildschirm. Die zweite Zeitzone öffnet sich bei Mausnähe.");
        Localization.Register("Elements", "Elemente");
        Localization.Register("Configure the right side separately", "Rechte Seite getrennt einstellen");
        Localization.Register("Clock / notifications", "Uhr / Mitteilungen");
        Localization.Register("Wi-Fi / volume / battery", "WLAN / Lautstärke / Akku");
        Localization.Register("Tray icons", "Tray-Icons");
        Localization.Register("Language", "Sprache");
        Localization.Register("Always", "Immer");
        Localization.Register("With Alt or hover", "Bei Alt oder Mausnähe");
        Localization.Register("Configure pinned apps separately", "Angeheftete Apps getrennt einstellen");
        Localization.Register("Show only with Alt", "Erst mit Alt zeigen");
        Localization.Register("Always visible", "Immer sichtbar");
        Localization.Register("Choose app stacks separately", "App-Stapel getrennt auswählen");
    }

    TabPage BuildAppearancePage()
    {
        var (tab, panel) = Page("Aussehen");
        appearanceEnabled.Text = "Eigenes Design auf diesem Monitor";
        appearanceEnabled.AutoSize = true;
        appearanceEnabled.CheckedChanged += (_, _) => EnableFields();
        Full(panel, appearanceEnabled, 0);
        Options(style, ["Windows-Standard", "Transparent", "Getönt", "Blur", "Acrylic"]);
        Row(panel, 1, "Stil", style);
        colorButton.AutoSize = true;
        colorButton.FlatStyle = FlatStyle.Flat;
        colorButton.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = Color.FromArgb((draft?.Color ?? baseline.Color) | unchecked((int)0xFF000000)), FullOpen = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var alpha = (draft?.Color ?? baseline.Color) & unchecked((int)0xFF000000);
            if (draft != null) draft.Color = alpha | (picker.Color.ToArgb() & 0xFFFFFF);
            ShowColor();
        };
        Row(panel, 2, "Farbe", colorButton);
        opacity.Minimum = 0;
        opacity.Maximum = 255;
        opacity.TickFrequency = 32;
        opacity.ValueChanged += (_, _) =>
        {
            if (!loading && draft != null) draft.Color = (opacity.Value << 24) | (draft.Color & 0xFFFFFF);
        };
        Row(panel, 3, "Deckkraft", opacity);
        autoContrast.Text = "Windows-Akzent als Tönung";
        autoContrast.AutoSize = true;
        Full(panel, autoContrast, 4);
        appAccent.Text = "Farbe der aktiven App";
        appAccent.AutoSize = true;
        Full(panel, appAccent, 5);
        contextStyle.Text = "Anderer Stil bei maximiertem Fenster";
        contextStyle.AutoSize = true;
        contextStyle.CheckedChanged += (_, _) => EnableFields();
        Full(panel, contextStyle, 6);
        Options(maximizedStyle, ["Windows-Standard", "Transparent", "Getönt", "Blur", "Acrylic"]);
        Row(panel, 7, "Dann", maximizedStyle);
        return tab;
    }

    TabPage BuildClockPage()
    {
        var (tab, panel) = Page("Uhr");
        clockEnabled.Text = "Eigene Uhr auf diesem Monitor";
        clockEnabled.AutoSize = true;
        clockEnabled.CheckedChanged += (_, _) => EnableFields();
        Full(panel, clockEnabled, 0);
        Options(clockLayout, ["Aus", "Nur Uhrzeit", "Uhrzeit und Datum"]);
        Row(panel, 1, "Anzeige", clockLayout);
        secondZone.DropDownStyle = ComboBoxStyle.DropDownList;
        secondZone.Items.Add("Keine zweite Zeitzone");
        foreach (var zone in timeZones) secondZone.Items.Add(zone.DisplayName);
        secondZone.SelectedIndex = 0;
        secondZone.Dock = DockStyle.Top;
        Row(panel, 2, "Beim Darüberfahren", secondZone);
        Full(panel, new Label { Text = "Diese Einstellung gilt für diesen Bildschirm. Die zweite Zeitzone öffnet sich bei Mausnähe.",
            AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.FromArgb(93, 110, 133),
            Margin = new Padding(3, 18, 3, 3) }, 3);
        return tab;
    }

    TabPage BuildElementsPage()
    {
        var (tab, panel) = Page("Elemente");
        rightEnabled.Text = "Rechte Seite getrennt einstellen";
        rightEnabled.AutoSize = true;
        rightEnabled.CheckedChanged += (_, _) => EnableFields();
        Full(panel, rightEnabled, 0);
        string[] labels = ["Uhr / Mitteilungen", "WLAN / Lautstärke / Akku", "Tray-Icons", "Sprache"];
        for (int i = 0; i < rightModes.Length; i++)
        {
            Options(rightModes[i], ["Immer", "Bei Alt oder Mausnähe", "Aus"]);
            Row(panel, i + 1, labels[i], rightModes[i]);
        }
        pinnedEnabled.Text = "Angeheftete Apps getrennt einstellen";
        pinnedEnabled.AutoSize = true;
        pinnedEnabled.CheckedChanged += (_, _) => EnableFields();
        Full(panel, pinnedEnabled, 5);
        hidePinned.Text = "Erst mit Alt zeigen";
        hidePinned.AutoSize = true;
        Full(panel, hidePinned, 6);
        keepPinned.Minimum = 0;
        keepPinned.Maximum = 5;
        keepPinned.Width = 64;
        Row(panel, 7, "Immer sichtbar", keepPinned);
        stacksEnabled.Text = "App-Stapel getrennt auswählen";
        stacksEnabled.AutoSize = true;
        stacksEnabled.CheckedChanged += (_, _) => EnableFields();
        Full(panel, stacksEnabled, 8);
        stacks.CheckOnClick = true;
        stacks.Height = 74;
        stacks.Dock = DockStyle.Top;
        foreach (var group in baseline.AppStacks.Groups) stacks.Items.Add(new StackChoice(group.Id.ToString("N"), group.Name));
        Full(panel, stacks, 9);
        return tab;
    }

    static (TabPage Tab, TableLayoutPanel Panel) Page(string title)
    {
        var tab = new TabPage(title) { BackColor = Color.FromArgb(250, 252, 255) };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, RowCount = 11,
            Padding = new Padding(14, 18, 14, 12) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        for (int i = 0; i < 10; i++) panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tab.Controls.Add(panel);
        return (tab, panel);
    }
    static void Options(ComboBox box, string[] options)
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.Items.AddRange(options);
        box.SelectedIndex = 0;
        box.Dock = DockStyle.Top;
    }
    static void Row(TableLayoutPanel panel, int row, string label, Control input)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(52, 70, 94), Margin = new Padding(3, 8, 6, 8) }, 0, row);
        panel.Controls.Add(input, 1, row);
    }
    static void Full(TableLayoutPanel panel, Control control, int row)
    {
        control.Margin = new Padding(3, 6, 3, 8);
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }
    static Label Title(string text) => new() { Text = text, AutoSize = true,
        Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold), ForeColor = Color.FromArgb(30, 45, 67),
        Margin = new Padding(2, 0, 2, 9) };

    void SelectDisplay()
    {
        if (loading) return;
        SaveCurrent();
        selectedScreen = (displayList.SelectedItem as DisplayChoice)?.Screen;
        if (selectedScreen == null) return;
        draft = Layouts.FirstOrDefault(layout => string.Equals(layout.DeviceName, selectedScreen.DeviceName,
            StringComparison.OrdinalIgnoreCase))?.Copy() ?? MonitorLayout.Capture(selectedScreen, baseline);
        loading = true;
        try
        {
            appearanceEnabled.Checked = draft.AppearanceEnabled;
            style.SelectedIndex = Math.Clamp(draft.Style, 0, 4);
            opacity.Value = (draft.Color >> 24) & 0xFF;
            autoContrast.Checked = draft.AutoContrast;
            appAccent.Checked = draft.AppAccent;
            contextStyle.Checked = draft.ContextStyle;
            maximizedStyle.SelectedIndex = Math.Clamp(draft.MaximizedStyle, 0, 4);
            clockEnabled.Checked = draft.ClockEnabled;
            clockLayout.SelectedIndex = Math.Clamp(draft.ClockLayout, 0, 2);
            secondZone.SelectedIndex = Math.Max(0, timeZones.FindIndex(zone => zone.Id == draft.SecondaryTimeZoneId) + 1);
            rightEnabled.Checked = draft.NativeVisibilityEnabled;
            var modes = SceneSnapshot.NormalizeModes(draft.RightModes) ?? [0, 0, 0, 0];
            for (int i = 0; i < rightModes.Length; i++) rightModes[i].SelectedIndex = modes[i];
            pinnedEnabled.Checked = draft.NativePinnedEnabled;
            hidePinned.Checked = draft.HidePinnedUntilAlt;
            keepPinned.Value = Math.Clamp(draft.KeepPinnedCount, 0, 5);
            stacksEnabled.Checked = draft.NativeStacksEnabled;
            var selected = new HashSet<string>(draft.VisibleStackIds ?? [], StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < stacks.Items.Count; i++)
                stacks.SetItemChecked(i, stacks.Items[i] is StackChoice choice && selected.Contains(choice.Id));
            ShowColor();
        }
        finally { loading = false; }
        EnableFields();
    }

    void SaveCurrent()
    {
        if (selectedScreen == null || draft == null) return;
        if (nativeReady)
        {
            draft.AppearanceEnabled = appearanceEnabled.Checked;
            draft.Style = style.SelectedIndex;
            draft.Color = (opacity.Value << 24) | (draft.Color & 0xFFFFFF);
            draft.AutoContrast = autoContrast.Checked;
            draft.AppAccent = appAccent.Checked;
            draft.ContextStyle = contextStyle.Checked;
            draft.MaximizedStyle = maximizedStyle.SelectedIndex;
            draft.NativeVisibilityEnabled = rightEnabled.Checked;
            draft.RightModes = rightModes.Select(box => box.SelectedIndex).ToArray();
            draft.NativePinnedEnabled = pinnedEnabled.Checked;
            draft.HidePinnedUntilAlt = hidePinned.Checked;
            draft.KeepPinnedCount = (int)keepPinned.Value;
            draft.NativeStacksEnabled = stacksEnabled.Checked;
            draft.VisibleStackIds = stacks.CheckedItems.OfType<StackChoice>().Select(choice => choice.Id).ToList();
        }
        draft.ClockEnabled = clockEnabled.Checked;
        draft.ClockLayout = clockLayout.SelectedIndex;
        draft.SecondaryTimeZoneId = secondZone.SelectedIndex > 0 ? timeZones[secondZone.SelectedIndex - 1].Id : null;
        Layouts.RemoveAll(layout => string.Equals(layout.DeviceName, selectedScreen.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (draft.ClockEnabled || draft.AppearanceEnabled || draft.NativeVisibilityEnabled ||
            draft.NativePinnedEnabled || draft.NativeStacksEnabled) Layouts.Add(draft.Copy());
    }

    void EnableFields()
    {
        style.Enabled = colorButton.Enabled = opacity.Enabled = autoContrast.Enabled = appAccent.Enabled =
            contextStyle.Enabled = nativeReady && appearanceEnabled.Checked;
        maximizedStyle.Enabled = nativeReady && appearanceEnabled.Checked && contextStyle.Checked;
        clockLayout.Enabled = secondZone.Enabled = clockEnabled.Checked;
        foreach (var box in rightModes) box.Enabled = nativeReady && rightEnabled.Checked;
        hidePinned.Enabled = keepPinned.Enabled = nativeReady && pinnedEnabled.Checked;
        stacks.Enabled = nativeReady && stacksEnabled.Checked;
    }

    void ShowColor()
    {
        if (draft == null) return;
        var color = Color.FromArgb(draft.Color | unchecked((int)0xFF000000));
        colorButton.BackColor = color;
        colorButton.ForeColor = color.GetBrightness() > 0.5f ? Color.Black : Color.White;
        colorButton.Text = Localization.T($"#{color.R:X2}{color.G:X2}{color.B:X2}  change", $"#{color.R:X2}{color.G:X2}{color.B:X2}  ändern");
    }

    sealed record DisplayChoice(Screen Screen)
    {
        public override string ToString() => $"{(Screen.Primary ? Localization.T("Primary display", "Hauptmonitor") : "Monitor")} · {Screen.Bounds.Width}×{Screen.Bounds.Height} · {Screen.DeviceName}";
    }
    sealed record StackChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
