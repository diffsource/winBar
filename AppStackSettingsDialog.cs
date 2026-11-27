using System.Drawing.Drawing2D;

sealed class StackSettingsDialog : Form
{
    readonly AppStackConfig draft;
    readonly FlowLayoutPanel cards;
    readonly StackPreview preview;
    readonly Button add;
    readonly Button save;
    readonly Label status;

    public AppStackConfig Config { get; private set; }

    public StackSettingsDialog(AppStackConfig current)
    {
        draft = (current ?? new AppStackConfig()).Copy();
        Config = draft.Copy();
        Text = Localization.T("winBar · App stacks", "winBar · App-Stapel");
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 680);
        MinimumSize = new Size(620, 580);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(242, 246, 251);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 188));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        Controls.Add(layout);

        var hero = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 34, 53) };
        layout.Controls.Add(hero, 0, 0);
        hero.Controls.Add(new Label
        {
            Text = Localization.T("App stacks", "App-Stapel"), Font = new Font("Segoe UI Semibold", 22, FontStyle.Bold),
            ForeColor = Color.White, Location = new Point(26, 14), Size = new Size(420, 54)
        });
        hero.Controls.Add(new Label
        {
            Text = Localization.T("More apps. Fewer icons.", "Mehr Apps. Weniger Symbole."), Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(170, 194, 227), AutoSize = true, Location = new Point(29, 64)
        });
        preview = new StackPreview(draft) { Location = new Point(26, 105), Size = new Size(650, 56), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        hero.Controls.Add(preview);
        hero.Resize += (_, _) => preview.Width = Math.Max(400, hero.ClientSize.Width - 52);

        var options = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(242, 246, 251) };
        layout.Controls.Add(options, 0, 1);
        var enabled = new CheckBox { Text = Localization.T("Use app stacks", "App-Stapel verwenden"), Checked = draft.Enabled, AutoSize = true, Location = new Point(25, 14) };
        enabled.CheckedChanged += (_, _) => { draft.Enabled = enabled.Checked; preview.Invalidate(); ValidateDraft(); };
        options.Controls.Add(enabled);
        var auto = new CheckBox { Text = Localization.T("Compact when the taskbar fills up", "Bei voller Taskleiste kompakt"), Checked = draft.AutoCompact, AutoSize = true, Location = new Point(25, 47) };
        options.Controls.Add(auto);
        var thresholdLabel = new Label { Text = Localization.T("At", "Ab"), AutoSize = true, Location = new Point(368, 49) };
        options.Controls.Add(thresholdLabel);
        var threshold = new NumericUpDown
        {
            Minimum = 45, Maximum = 95, Increment = 5, Value = Math.Clamp(draft.CompactAtPercent, 45, 95),
            Location = new Point(400, 45), Width = 70, Enabled = draft.AutoCompact
        };
        options.Controls.Add(threshold);
        options.Controls.Add(new Label { Text = "%", AutoSize = true, Location = new Point(477, 49) });
        auto.CheckedChanged += (_, _) => { draft.AutoCompact = auto.Checked; threshold.Enabled = auto.Checked; };
        threshold.ValueChanged += (_, _) => draft.CompactAtPercent = (int)threshold.Value;
        options.Controls.Add(new Label
        {
            Text = Localization.T("Position 1 is the first pinned app. The first pin stays visible.\nAlt or hover reveals the others; running apps stay visible.",
                "Position 1 ist die erste angeheftete App. Der erste Pin bleibt sichtbar.\nAlt oder Hover zeigt die übrigen; laufende Apps bleiben sichtbar."),
            ForeColor = Color.FromArgb(80, 97, 122), Location = new Point(28, 76), Size = new Size(650, 72),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
        });

        cards = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(21, 10, 21, 12), BackColor = Color.FromArgb(242, 246, 251)
        };
        cards.Resize += (_, _) => ResizeCards();
        layout.Controls.Add(cards, 0, 2);

        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 250, 253) };
        layout.Controls.Add(footer, 0, 3);
        add = new Button { Text = Localization.T("+ Stack", "+ Stapel"), Location = new Point(24, 17), Size = new Size(104, 36), FlatStyle = FlatStyle.Flat };
        add.FlatAppearance.BorderColor = Color.FromArgb(171, 190, 218);
        add.Click += (_, _) => AddGroup();
        footer.Controls.Add(add);
        status = new Label { ForeColor = Color.FromArgb(82, 98, 120), Location = new Point(139, 15), Size = new Size(280, 44), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        footer.Controls.Add(status);
        var cancel = new Button { Text = Localization.T("Cancel", "Abbrechen"), Size = new Size(98, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(cancel);
        save = new Button
        {
            Text = Localization.T("Save", "Speichern"), Size = new Size(105, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.FromArgb(50, 95, 164), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) =>
        {
            if (!draft.IsValid(out _)) return;
            Config = draft.Copy();
            DialogResult = DialogResult.OK;
            Close();
        };
        footer.Controls.Add(save);
        void ArrangeFooter()
        {
            save.Left = footer.ClientSize.Width - save.Width - 24;
            cancel.Left = save.Left - cancel.Width - 8;
            status.Width = Math.Max(100, cancel.Left - status.Left - 12);
            save.Top = cancel.Top = add.Top = 17;
        }
        footer.Resize += (_, _) => ArrangeFooter();
        Shown += (_, _) => ArrangeFooter();
        AcceptButton = save;
        CancelButton = cancel;
        RenderCards();
    }

    void AddGroup()
    {
        if (draft.Groups.Count >= 4) return;
        int start = 1;
        while (start <= 31 && draft.Groups.Any(group => start <= group.Start + group.Count - 1 && start + 1 >= group.Start)) start++;
        if (start > 31) { status.Text = Localization.T("No free range for two apps.", "Kein freier Bereich für zwei Apps."); return; }
        draft.Groups.Add(new AppStackGroup { Name = $"{Localization.T("Stack", "Stapel")} {draft.Groups.Count + 1}", Start = start });
        RenderCards();
    }

    void RenderCards()
    {
        cards.SuspendLayout();
        try
        {
            cards.Controls.Clear();
            for (int i = 0; i < draft.Groups.Count; i++) cards.Controls.Add(CreateCard(draft.Groups[i], i));
            ResizeCards();
        }
        finally { cards.ResumeLayout(); }
        preview.Invalidate();
        ValidateDraft();
    }

    void ResizeCards()
    {
        int width = Math.Max(500, cards.ClientSize.Width - cards.Padding.Horizontal - 23);
        foreach (Control card in cards.Controls) card.Width = width;
    }

    Control CreateCard(AppStackGroup group, int index)
    {
        var card = new StackCard { Height = 174, Width = Math.Max(500, cards.ClientSize.Width - 65), Margin = new Padding(0, 0, 0, 11) };
        card.Controls.Add(new Label
        {
            Text = $"{Localization.T("Stack", "Stapel")} {index + 1}", Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(34, 51, 77), AutoSize = true, Location = new Point(16, 11)
        });
        var active = new CheckBox { Text = Localization.T("Active", "Aktiv"), Checked = group.Enabled, AutoSize = true, Location = new Point(250, 13) };
        active.CheckedChanged += (_, _) => { group.Enabled = active.Checked; preview.Invalidate(); ValidateDraft(); };
        card.Controls.Add(active);
        var remove = new Button { Text = "×", AccessibleName = Localization.T("Delete stack", "Stapel löschen"), Size = new Size(36, 29), FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        remove.FlatAppearance.BorderSize = 0;
        remove.Location = new Point(card.Width - remove.Width - 10, 9);
        remove.Click += (_, _) => { draft.Groups.Remove(group); RenderCards(); };
        card.Controls.Add(remove);

        card.Controls.Add(Caption("Name", 18, 43));
        card.Controls.Add(Caption(Localization.T("Start position", "Startposition"), 247, 43));
        card.Controls.Add(Caption(Localization.T("Count", "Anzahl"), 371, 43));
        var name = new TextBox { Text = group.Name, Location = new Point(18, 65), Width = 210 };
        name.TextChanged += (_, _) => { group.Name = name.Text; preview.Invalidate(); ValidateDraft(); };
        card.Controls.Add(name);
        var start = new NumericUpDown { Minimum = 1, Maximum = 32, Value = Math.Clamp(group.Start, 1, 32), Location = new Point(247, 65), Width = 92 };
        start.ValueChanged += (_, _) => { group.Start = (int)start.Value; preview.Invalidate(); ValidateDraft(); };
        card.Controls.Add(start);
        var count = new NumericUpDown { Minimum = 2, Maximum = 31, Value = Math.Clamp(group.Count, 2, 31), Location = new Point(371, 65), Width = 92 };
        count.ValueChanged += (_, _) => { group.Count = (int)count.Value; preview.Invalidate(); ValidateDraft(); };
        card.Controls.Add(count);

        group.LaunchPaths ??= [];
        var paths = new Label { ForeColor = Color.FromArgb(70, 92, 122), Location = new Point(18, 108), Width = 270, Height = 24 };
        void UpdatePaths() => paths.Text = Localization.T($"Launch all · {group.LaunchPaths.Count} files", $"Alle starten · {group.LaunchPaths.Count} Dateien");
        UpdatePaths();
        card.Controls.Add(paths);
        var choose = new Button { Text = Localization.T("Choose…", "Wählen…"), Location = new Point(292, 103), Size = new Size(145, 34), FlatStyle = FlatStyle.Flat };
        choose.FlatAppearance.BorderColor = Color.FromArgb(167, 190, 221);
        choose.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = Localization.T("Choose programs for Launch all", "Programme für Alle starten auswählen"),
                Filter = Localization.T("Programs or shortcuts (*.exe;*.lnk)|*.exe;*.lnk", "Programme oder Verknüpfungen (*.exe;*.lnk)|*.exe;*.lnk"),
                Multiselect = true, CheckFileExists = true
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            group.LaunchPaths = picker.FileNames
                .Where(path => Path.IsPathFullyQualified(path) && File.Exists(path) &&
                    (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            UpdatePaths();
        };
        card.Controls.Add(choose);
        var clear = new Button { Text = Localization.T("Clear", "Leeren"), Location = new Point(443, 103), Size = new Size(85, 34), FlatStyle = FlatStyle.Flat };
        clear.FlatAppearance.BorderSize = 0;
        clear.Click += (_, _) => { group.LaunchPaths.Clear(); UpdatePaths(); };
        card.Controls.Add(clear);
        card.Controls.Add(new Label
        {
            Text = Localization.T("Only selected files are launched together.", "Nur ausgewählte Dateien werden zusammen gestartet."),
            ForeColor = Color.FromArgb(107, 122, 142), Location = new Point(18, 143), Size = new Size(500, 23)
        });
        card.Resize += (_, _) => remove.Left = card.Width - remove.Width - 10;
        return card;
    }

    static Label Caption(string text, int x, int y) => new()
    {
        Text = text, AutoSize = true, Location = new Point(x, y),
        ForeColor = Color.FromArgb(96, 111, 135), Font = new Font("Segoe UI", 8.5f)
    };

    void ValidateDraft()
    {
        bool valid = draft.IsValid(out var reason);
        save.Enabled = valid;
        status.Text = valid ? Localization.T($"{draft.Groups.Count} of 4 stacks", $"{draft.Groups.Count} von 4 Stapeln") : reason;
        status.ForeColor = valid ? Color.FromArgb(82, 98, 120) : Color.FromArgb(176, 57, 70);
        add.Enabled = draft.Groups.Count < 4;
    }

    sealed class StackCard : Panel
    {
        public StackCard()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(250, 252, 255);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new Pen(Color.FromArgb(204, 217, 234));
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(new Rectangle(1, 1, Width - 3, Height - 3), new Size(14, 14));
            e.Graphics.DrawPath(border, path);
        }
    }

    sealed class StackPreview : Control
    {
        readonly AppStackConfig config;
        public StackPreview(AppStackConfig config)
        {
            this.config = config;
            DoubleBuffered = true;
            AccessibleName = Localization.T("App stack preview", "Vorschau der App-Stapel");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var bar = new GraphicsPath();
            bar.AddRoundedRectangle(new Rectangle(1, 1, Width - 3, Height - 3), new Size(18, 18));
            using var fill = new LinearGradientBrush(ClientRectangle, Color.FromArgb(64, 84, 113), Color.FromArgb(42, 59, 85), LinearGradientMode.Vertical);
            e.Graphics.FillPath(fill, bar);
            using var outline = new Pen(Color.FromArgb(117, 151, 191));
            e.Graphics.DrawPath(outline, bar);
            using var icon = new SolidBrush(Color.FromArgb(183, 209, 242));
            e.Graphics.FillRectangle(icon, 20, 18, 13, 13);
            e.Graphics.FillRectangle(icon, 35, 18, 13, 13);
            int x = 79;
            var groups = config.Groups ?? [];
            if (groups.Count == 0)
            {
                for (int i = 0; i < 4; i++) { e.Graphics.FillEllipse(icon, x, 14, 23, 23); x += 40; }
            }
            else
            {
                foreach (var group in groups.Take(4))
                {
                    using var pill = new GraphicsPath();
                    pill.AddRoundedRectangle(new Rectangle(x, 9, 59, 38), new Size(13, 13));
                    using var pillFill = new SolidBrush(group.Enabled ? Color.FromArgb(90, 117, 153) : Color.FromArgb(72, 86, 110));
                    e.Graphics.FillPath(pillFill, pill);
                    using var pillEdge = new Pen(Color.FromArgb(141, 175, 217));
                    e.Graphics.DrawPath(pillEdge, pill);
                    var initial = string.IsNullOrWhiteSpace(group.Name) ? "•" : group.Name.Trim()[0].ToString().ToUpperInvariant();
                    using var font = new Font("Segoe UI Semibold", 12, FontStyle.Bold);
                    TextRenderer.DrawText(e.Graphics, initial, font, new Rectangle(x + 5, 13, 29, 27), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    using var count = new Font("Segoe UI", 9, FontStyle.Bold);
                    TextRenderer.DrawText(e.Graphics, Math.Clamp(group.Count, 2, 31).ToString(), count,
                        new Rectangle(x + 36, 16, 19, 22), Color.FromArgb(195, 221, 255),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += 72;
                }
            }
            using var clock = new Font("Segoe UI Semibold", 10, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, "10:24", clock, new Rectangle(Width - 83, 13, 66, 28), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
