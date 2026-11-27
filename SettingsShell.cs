// A compact navigation shell for the many taskbar controls. Pages are built once;
// switching pages does not recreate controls or restart any taskbar integration.
internal sealed class SettingsShell : UserControl
{
    static readonly Color Navy = Color.FromArgb(24, 36, 55);
    static readonly Color NavyHover = Color.FromArgb(39, 58, 84);
    static readonly Color Blue = Color.FromArgb(64, 111, 181);
    static readonly Color Ink = Color.FromArgb(29, 43, 63);
    readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    readonly FlowLayoutPanel navigation = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, Padding = new Padding(12, 4, 8, 4), BackColor = Navy
    };
    readonly Dictionary<string, (Control Page, Button Button)> pages = new(StringComparer.Ordinal);
    string? selectedKey;

    public ComboBox LanguageChoice { get; } = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 164,
        FlatStyle = FlatStyle.Flat, BackColor = Color.White
    };

    public SettingsShell()
    {
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 202));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Navy, Margin = Padding.Empty };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        layout.Controls.Add(sidebar, 0, 0);
        layout.Controls.Add(host, 1, 0);

        var brand = new Panel { Dock = DockStyle.Fill, BackColor = Navy };
        brand.Controls.Add(new Label
        {
            Text = "winBar", ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 22, FontStyle.Bold),
            AutoSize = true, Location = new Point(17, 14)
        });
        brand.Controls.Add(new Label
        {
            Text = Localization.T("A quieter taskbar", "Weniger Ablenkung"),
            ForeColor = Color.FromArgb(165, 185, 211), AutoSize = true, Location = new Point(19, 63)
        });
        sidebar.Controls.Add(brand, 0, 0);
        sidebar.Controls.Add(navigation, 0, 1);

        var language = new Panel { Dock = DockStyle.Fill, BackColor = Navy };
        language.Controls.Add(new Label
        {
            Text = Localization.T("Language", "Sprache"), ForeColor = Color.FromArgb(180, 198, 221),
            Location = new Point(17, 12), Size = new Size(174, 28)
        });
        LanguageChoice.Items.AddRange(["English", "Deutsch", "System"]);
        LanguageChoice.Location = new Point(16, 46);
        language.Controls.Add(LanguageChoice);
        sidebar.Controls.Add(language, 0, 2);
    }

    public TableLayoutPanel AddPage(string key, string englishTitle, string germanTitle,
        string englishDescription, string germanDescription)
    {
        if (pages.TryGetValue(key, out var existing))
            return (TableLayoutPanel)existing.Page.Controls[1];

        var (navigationEnglish, navigationGerman) = key switch
        {
            "clock" => ("Clock", "Uhr"),
            "apps" => ("Apps", "Apps"),
            _ => (englishTitle, germanTitle)
        };
        var button = new Button
        {
            Text = Localization.T(navigationEnglish, navigationGerman),
            Size = new Size(170, 42), Margin = new Padding(0, 0, 0, 4),
            Padding = new Padding(12, 0, 4, 0), FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(211, 223, 239),
            BackColor = Navy, Cursor = Cursors.Hand, TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = NavyHover;
        button.Click += (_, _) => ShowPage(key);
        navigation.Controls.Add(button);

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = Color.White };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 249, 252) };
        header.Controls.Add(new Label
        {
            Text = Localization.T(englishTitle, germanTitle),
            ForeColor = Ink, Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold),
            AutoSize = true, Location = new Point(24, 13)
        });
        header.Controls.Add(new Label
        {
            Text = Localization.T(englishDescription, germanDescription),
            ForeColor = Color.FromArgb(94, 108, 130),
            AutoSize = true, MaximumSize = new Size(700, 0), Location = new Point(27, 56)
        });
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(229, 234, 241));
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        page.Controls.Add(header, 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true,
            Padding = new Padding(24, 12, 28, 22), BackColor = Color.White, ForeColor = Ink
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        page.Controls.Add(content, 0, 1);
        page.Visible = false;
        host.Controls.Add(page);
        pages.Add(key, (page, button));
        if (selectedKey == null) ShowPage(key);
        return content;
    }

    public void ShowPage(string key)
    {
        if (!pages.TryGetValue(key, out var selected)) return;
        selectedKey = key;
        foreach (var (id, value) in pages)
        {
            bool active = id == key;
            value.Page.Visible = active;
            value.Button.BackColor = active ? Blue : Navy;
            value.Button.ForeColor = Color.White;
        }
        selected.Page.BringToFront();
    }
}
