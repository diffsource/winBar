using System.Drawing.Drawing2D;

// A settings control, not an overlay: paints only when the settings window needs repainting.
internal sealed class ThemePicker : UserControl
{
    ThemeCard[] cards = [];
    readonly ToolTip tips = new();
    string? selectedId;

    public event Action<ThemeDefinition>? ThemeSelected;

    public ThemePicker()
    {
        Height = 270;
        MinimumSize = new Size(390, 270);
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        BackColor = SystemColors.Control;
        RefreshThemes();
    }

    public void RefreshThemes()
    {
        tips.RemoveAll();
        foreach (var card in cards) { Controls.Remove(card); card.Dispose(); }
        cards = ThemeCatalog.All.Select(theme =>
        {
            var card = new ThemeCard(theme);
            card.Click += (_, _) =>
            {
                SetSelectedTheme(theme.Id);
                ThemeSelected?.Invoke(theme);
            };
            Controls.Add(card);
            tips.SetToolTip(card, Description(theme));
            return card;
        }).ToArray();
        int rows = (cards.Length + 1) / 2;
        Height = rows * 130 + Math.Max(0, rows - 1) * 10;
        MinimumSize = new Size(390, Height);
        LayoutCards();
        SetSelectedTheme(selectedId);
    }

    public void SetSelectedTheme(string? id)
    {
        selectedId = ThemeCatalog.Find(id)?.Id;
        foreach (var card in cards) card.SetSelected(card.Theme.Id == selectedId);
    }

    private static string Description(ThemeDefinition theme) => theme.Id switch
    {
        "glass" => Localization.T("Cool smoked glass", "Kühles Rauchglas"),
        "glacier" => Localization.T("Soft frosted glass", "Weiches Milchglas"),
        "ink" => Localization.T("Dark, crisp gray", "Dunkles, klares Grau"),
        "adaptive" => Localization.T("Adapts to Windows", "Passt sich Windows an"),
        _ => theme.Description // Names and descriptions of imported themes belong to their author.
    };

    void LayoutCards()
    {
        const int gap = 10;
        int width = Math.Max(180, (ClientSize.Width - gap) / 2);
        for (int i = 0; i < cards.Length; i++)
            cards[i].Bounds = new Rectangle((i % 2) * (width + gap), (i / 2) * 140, width, 130);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCards();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }

    sealed class ThemeCard : Button
    {
        readonly ThemeDefinition theme;
        bool selected;
        bool hovered;

        public ThemeDefinition Theme => theme;

        public ThemeCard(ThemeDefinition theme)
        {
            this.theme = theme;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            Text = theme.Name;
            AccessibleName = $"{theme.Name}: {Description(theme)}";
            AccessibleDescription = Localization.T("Choose theme", "Design auswählen");
        }

        public void SetSelected(bool value)
        {
            if (selected == value) return;
            selected = value;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            using var shape = Rounded(bounds, 13);
            using var surface = new SolidBrush(selected ? Color.FromArgb(241, 247, 255) : hovered ? Color.FromArgb(250, 252, 255) : Color.White);
            using var border = new Pen(selected ? Color.FromArgb(70, 119, 187) : hovered ? Color.FromArgb(155, 176, 205) : Color.FromArgb(210, 219, 231), selected ? 2 : 1);
            e.Graphics.FillPath(surface, shape);
            e.Graphics.DrawPath(border, shape);

            var preview = new Rectangle(11, 10, Width - 22, 52);
            using var previewShape = Rounded(preview, 9);
            using var previewFill = new LinearGradientBrush(preview, theme.PreviewBase, theme.PreviewTint, 0f);
            e.Graphics.FillPath(previewFill, previewShape);
            using var glass = new SolidBrush(Color.FromArgb(theme.Style == 2 ? 190 : 95, 229, 238, 251));
            using var glassShape = Rounded(new Rectangle(preview.Left + 9, preview.Bottom - 24, preview.Width - 18, 16), 6);
            e.Graphics.FillPath(glass, glassShape);

            using var iconFill = new SolidBrush(theme.Style == 2 ? Color.FromArgb(199, 215, 233) : theme.PreviewAccent);
            for (int i = 0; i < 3; i++) e.Graphics.FillEllipse(iconFill, preview.Left + 21 + i * 19, preview.Bottom - 20, 9, 9);
            using var clockFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, "10:24", clockFont,
                new Rectangle(preview.Right - 55, preview.Bottom - 22, 44, 13), Color.White,
                TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            using var nameFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
            using var descriptionFont = new Font("Segoe UI", 8.5f);
            TextRenderer.DrawText(e.Graphics, theme.Name, nameFont, new Rectangle(13, 69, Width - 42, 23),
                Color.FromArgb(26, 41, 62), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, Description(theme), descriptionFont, new Rectangle(13, 94, Width - 26, 23),
                Color.FromArgb(82, 99, 121), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (selected)
            {
                using var dot = new SolidBrush(Color.FromArgb(70, 119, 187));
                e.Graphics.FillEllipse(dot, Width - 27, 74, 9, 9);
            }
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(6, 6, Width - 13, Height - 13));
        }

        static GraphicsPath Rounded(Rectangle rectangle, int radius)
        {
            var path = new GraphicsPath();
            path.AddRoundedRectangle(rectangle, new Size(radius, radius));
            return path;
        }
    }
}
