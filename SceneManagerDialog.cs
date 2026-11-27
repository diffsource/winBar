using System.Text.Json;

// Edits private copies. The caller persists Scenes/Rules only after OK and applies ActivatedScene explicitly.
internal sealed class SceneManagerDialog : Form
{
    readonly Settings current;
    readonly int[]? rightModes;
    readonly bool focusLens;
    readonly string? secondaryTimeZoneId;
    readonly string[] visibleStackIds;
    readonly ListBox sceneList = new();
    readonly TextBox nameBox = new();
    readonly Button deleteButton = new();
    readonly Button overwriteButton = new();
    readonly Button activateButton = new();
    readonly TableLayoutPanel editor = new();
    readonly CheckBox automatic = new();
    readonly CheckBox monitorOnly = new();
    readonly NumericUpDown monitorCount = new();
    readonly CheckBox timed = new();
    readonly DateTimePicker startTime = new();
    readonly DateTimePicker endTime = new();
    readonly ComboBox days = new();
    SceneSnapshot? selectedScene;
    bool loading;

    public List<SceneSnapshot> Scenes { get; }
    public List<SceneRule> Rules { get; }
    public SceneSnapshot? ActivatedScene { get; private set; }

    public SceneManagerDialog(Settings current, IEnumerable<SceneSnapshot>? scenes, IEnumerable<SceneRule>? rules,
        int[]? rightModes, bool focusLens, string? secondaryTimeZoneId = null,
        IEnumerable<string>? visibleStackIds = null)
    {
        this.current = current;
        this.rightModes = SceneSnapshot.NormalizeModes(rightModes);
        this.focusLens = focusLens;
        this.secondaryTimeZoneId = secondaryTimeZoneId;
        this.visibleStackIds = [.. (visibleStackIds ?? [])];
        Scenes = JsonSerializer.Deserialize<List<SceneSnapshot>>(JsonSerializer.Serialize(scenes ?? [])) ?? [];
        Rules = JsonSerializer.Deserialize<List<SceneRule>>(JsonSerializer.Serialize(rules ?? [])) ?? [];

        Text = "winBar · Szenen";
        ClientSize = new Size(700, 560);
        MinimumSize = new Size(620, 510);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(246, 248, 252);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        Controls.Add(shell);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(27, 41, 61) };
        header.Paint += (_, e) => { using var brush = new SolidBrush(Color.FromArgb(102, 154, 219)); e.Graphics.FillRectangle(brush, 0, header.Height - 3, header.Width, 3); };
        header.Controls.Add(new Label
        {
            Text = "Szenen", Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold), ForeColor = Color.White,
            AutoSize = true, Location = new Point(22, 12)
        });
        header.Controls.Add(new Label
        {
            Text = "Eine Taskleiste für jeden Moment.", ForeColor = Color.FromArgb(180, 202, 230),
            AutoSize = true, Location = new Point(25, 53)
        });
        shell.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18, 16, 18, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        shell.Controls.Add(body, 0, 1);
        BuildSceneList(body);
        BuildEditor(body);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(20, 12, 18, 12) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label
        {
            Text = "Automatische Regeln prüfen nur bei Monitor- oder Zeitwechsel.", AutoSize = true,
            ForeColor = Color.FromArgb(93, 110, 133), Anchor = AnchorStyles.Left
        }, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var cancel = new Button { Text = "Abbrechen", AutoSize = true, MinimumSize = new Size(92, 32) };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var save = new Button { Text = "Speichern", AutoSize = true, MinimumSize = new Size(92, 32) };
        save.Click += (_, _) => Complete(false);
        activateButton.Text = "Aktivieren";
        activateButton.AutoSize = true;
        activateButton.MinimumSize = new Size(92, 32);
        activateButton.FlatStyle = FlatStyle.Flat;
        activateButton.FlatAppearance.BorderSize = 0;
        activateButton.BackColor = Color.FromArgb(62, 107, 174);
        activateButton.ForeColor = Color.White;
        activateButton.Click += (_, _) => Complete(true);
        actions.Controls.AddRange([cancel, save, activateButton]);
        footer.Controls.Add(actions, 1, 0);
        shell.Controls.Add(footer, 0, 2);
        CancelButton = cancel;
        AcceptButton = activateButton;

        foreach (var scene in Scenes) sceneList.Items.Add(scene);
        sceneList.DisplayMember = nameof(SceneSnapshot.Name);
        if (sceneList.Items.Count > 0) sceneList.SelectedIndex = 0;
        else UpdateSelection();
    }

    void BuildSceneList(TableLayoutPanel body)
    {
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(0, 0, 14, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(new Label
        {
            Text = "Deine Szenen", AutoSize = true, Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 45, 67), Margin = new Padding(2, 0, 0, 9)
        }, 0, 0);
        sceneList.Dock = DockStyle.Fill;
        sceneList.IntegralHeight = false;
        sceneList.BorderStyle = BorderStyle.FixedSingle;
        sceneList.SelectedIndexChanged += (_, _) => UpdateSelection();
        left.Controls.Add(sceneList, 0, 1);
        var listActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        var add = new Button { Text = "Neu aus aktueller Ansicht", AutoSize = true };
        add.Click += (_, _) => AddScene();
        deleteButton.Text = "Löschen";
        deleteButton.AutoSize = true;
        deleteButton.Click += (_, _) => DeleteScene();
        listActions.Controls.AddRange([add, deleteButton]);
        left.Controls.Add(listActions, 0, 2);
        body.Controls.Add(left, 0, 0);
    }

    void BuildEditor(TableLayoutPanel body)
    {
        editor.Dock = DockStyle.Fill;
        editor.ColumnCount = 2;
        editor.AutoScroll = true;
        editor.Padding = new Padding(8, 0, 0, 0);
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        body.Controls.Add(editor, 1, 0);
        int row = 0;
        AddFull(Title("Name"), row++);
        nameBox.MaxLength = 32;
        nameBox.Dock = DockStyle.Top;
        nameBox.TextChanged += (_, _) =>
        {
            if (loading || selectedScene == null || string.IsNullOrWhiteSpace(nameBox.Text)) return;
            selectedScene.Name = nameBox.Text.Trim();
            sceneList.Refresh();
        };
        AddFull(nameBox, row++);
        overwriteButton.Text = "Aktuelle Ansicht übernehmen";
        overwriteButton.AutoSize = true;
        overwriteButton.Click += (_, _) => OverwriteScene();
        AddFull(overwriteButton, row++);
        AddFull(Title("Automatisch aktivieren"), row++);
        automatic.Text = "Regel für diese Szene verwenden";
        automatic.AutoSize = true;
        automatic.CheckedChanged += (_, _) => RuleChanged();
        AddFull(automatic, row++);
        monitorOnly.Text = "Ab einer bestimmten Monitorzahl";
        monitorOnly.AutoSize = true;
        monitorOnly.CheckedChanged += (_, _) => RuleChanged();
        AddFull(monitorOnly, row++);
        Add(Label("Mindestens"), 0, row);
        monitorCount.Minimum = 2;
        monitorCount.Maximum = 8;
        monitorCount.Value = 2;
        monitorCount.Width = 64;
        monitorCount.ValueChanged += (_, _) => RuleChanged();
        Add(monitorCount, 1, row++);
        timed.Text = "Nur zu bestimmten Zeiten";
        timed.AutoSize = true;
        timed.CheckedChanged += (_, _) => RuleChanged();
        AddFull(timed, row++);
        startTime.Format = DateTimePickerFormat.Custom;
        startTime.CustomFormat = "HH:mm";
        startTime.ShowUpDown = true;
        startTime.Width = 85;
        startTime.Value = DateTime.Today.AddHours(9);
        startTime.ValueChanged += (_, _) => RuleChanged();
        Add(Label("Von"), 0, row);
        Add(startTime, 1, row++);
        endTime.Format = DateTimePickerFormat.Custom;
        endTime.CustomFormat = "HH:mm";
        endTime.ShowUpDown = true;
        endTime.Width = 85;
        endTime.Value = DateTime.Today.AddHours(17);
        endTime.ValueChanged += (_, _) => RuleChanged();
        Add(Label("Bis"), 0, row);
        Add(endTime, 1, row++);
        days.DropDownStyle = ComboBoxStyle.DropDownList;
        days.Items.AddRange(["Jeden Tag", "Mo–Fr", "Sa–So"]);
        days.SelectedIndex = 0;
        days.Width = 115;
        days.SelectedIndexChanged += (_, _) => RuleChanged();
        Add(Label("Tage"), 0, row);
        Add(days, 1, row++);
        AddFull(new Label
        {
            Text = "Ohne Regel wechselst du Szenen manuell. Zeitfenster dürfen über Mitternacht gehen.",
            AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.FromArgb(94, 109, 128),
            Margin = new Padding(3, 14, 3, 0)
        }, row);

        void AddFull(Control control, int atRow)
        {
            editor.Controls.Add(control, 0, atRow);
            editor.SetColumnSpan(control, 2);
        }
        void Add(Control control, int column, int atRow) => editor.Controls.Add(control, column, atRow);
    }

    static Label Title(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold),
        ForeColor = Color.FromArgb(30, 45, 67), Margin = new Padding(3, 0, 3, 8)
    };
    static Label Label(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(66, 82, 103),
        Margin = new Padding(3, 6, 3, 6)
    };

    void AddScene()
    {
        var name = $"Szene {Scenes.Count + 1}";
        var scene = SceneSnapshot.Capture(name, current, rightModes, focusLens, secondaryTimeZoneId, visibleStackIds);
        Scenes.Add(scene);
        sceneList.Items.Add(scene);
        sceneList.SelectedItem = scene;
        nameBox.SelectAll();
        nameBox.Focus();
    }

    void DeleteScene()
    {
        if (selectedScene == null) return;
        int index = sceneList.SelectedIndex;
        Rules.RemoveAll(rule => rule.SceneId == selectedScene.Id);
        Scenes.Remove(selectedScene);
        selectedScene = null;
        sceneList.Items.RemoveAt(index);
        if (sceneList.Items.Count > 0) sceneList.SelectedIndex = Math.Min(index, sceneList.Items.Count - 1);
        else UpdateSelection();
    }

    void OverwriteScene()
    {
        if (selectedScene == null) return;
        var original = selectedScene;
        int index = sceneList.SelectedIndex;
        var replacement = SceneSnapshot.Capture(original.Name, current, rightModes, focusLens, secondaryTimeZoneId, visibleStackIds);
        replacement.Id = original.Id;
        Scenes[index] = replacement;
        loading = true;
        sceneList.Items[index] = replacement;
        sceneList.SelectedIndex = index;
        selectedScene = replacement;
        loading = false;
        UpdateSelection();
    }

    void UpdateSelection()
    {
        if (loading) return;
        selectedScene = sceneList.SelectedItem as SceneSnapshot;
        bool hasScene = selectedScene != null;
        editor.Enabled = hasScene;
        activateButton.Enabled = deleteButton.Enabled = overwriteButton.Enabled = hasScene;
        loading = true;
        try
        {
            nameBox.Text = selectedScene?.Name ?? "";
            var rule = selectedScene == null ? null : Rules.FirstOrDefault(item => item.SceneId == selectedScene.Id);
            automatic.Checked = rule?.Enabled == true;
            monitorOnly.Checked = rule != null && rule.MinMonitorCount > 1;
            monitorCount.Value = Math.Clamp(rule?.MinMonitorCount ?? 2, 2, 8);
            timed.Checked = rule?.StartLocalTime != null && rule.EndLocalTime != null;
            startTime.Value = DateTime.Today.Add((rule?.StartLocalTime ?? new TimeOnly(9, 0)).ToTimeSpan());
            endTime.Value = DateTime.Today.Add((rule?.EndLocalTime ?? new TimeOnly(17, 0)).ToTimeSpan());
            days.SelectedIndex = rule?.DaysOfWeek switch
            {
                [1, 2, 3, 4, 5] => 1,
                [0, 6] => 2,
                _ => 0
            };
        }
        finally { loading = false; }
        EnableRuleFields();
    }

    void EnableRuleFields()
    {
        monitorOnly.Enabled = timed.Enabled = automatic.Checked;
        monitorCount.Enabled = automatic.Checked && monitorOnly.Checked;
        startTime.Enabled = endTime.Enabled = days.Enabled = automatic.Checked && timed.Checked;
    }

    void RuleChanged()
    {
        if (loading || selectedScene == null) return;
        EnableRuleFields();
        var rule = Rules.FirstOrDefault(item => item.SceneId == selectedScene.Id);
        if (!automatic.Checked)
        {
            Rules.RemoveAll(item => item.SceneId == selectedScene.Id);
            return;
        }
        if (rule == null)
        {
            rule = new SceneRule { SceneId = selectedScene.Id, Priority = Rules.Count == 0 ? 1 : Rules.Max(item => item.Priority) + 1 };
            Rules.Add(rule);
        }
        Rules.RemoveAll(item => item.SceneId == selectedScene.Id && !ReferenceEquals(item, rule));
        rule.Enabled = true;
        rule.MinMonitorCount = monitorOnly.Checked ? (int)monitorCount.Value : 1;
        rule.MaxMonitorCount = null;
        rule.StartLocalTime = timed.Checked ? TimeOnly.FromDateTime(startTime.Value) : null;
        rule.EndLocalTime = timed.Checked ? TimeOnly.FromDateTime(endTime.Value) : null;
        rule.DaysOfWeek = !timed.Checked || days.SelectedIndex == 0 ? null : days.SelectedIndex == 1 ? [1, 2, 3, 4, 5] : [0, 6];
    }

    void Complete(bool activate)
    {
        if (selectedScene != null) selectedScene.Name = SceneSnapshot.NormalizeName(nameBox.Text);
        if (activate) ActivatedScene = selectedScene;
        DialogResult = DialogResult.OK;
        Close();
    }
}
