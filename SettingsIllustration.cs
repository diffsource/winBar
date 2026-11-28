using System.Drawing.Drawing2D;

internal enum SettingsIllustrationKind { Appearance, Taskbar, Clock, Apps, SystemArea, Layouts }

// Tiny vector explanations, drawn only when a settings page repaints. No image
// files, animations, timers, or background work are added to the taskbar app.
internal sealed class SettingsIllustration : Control
{
    readonly SettingsIllustrationKind kind;
    readonly Settings settings;
    static readonly Color Ink = Color.FromArgb(29, 45, 68);
    static readonly Color Muted = Color.FromArgb(91, 111, 137);
    static readonly Color Accent = Color.FromArgb(65, 113, 184);

    public SettingsIllustration(SettingsIllustrationKind kind, Settings settings)
    {
        this.kind = kind;
        this.settings = settings;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 128;
        MinimumSize = new Size(300, 128);
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Margin = new Padding(3, 9, 3, 16);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = kind switch
        {
            SettingsIllustrationKind.Appearance => Localization.T("Normal and maximized taskbar preview", "Vorschau der normalen und maximierten Taskleiste"),
            SettingsIllustrationKind.Taskbar => Localization.T("Windows and clean taskbar comparison", "Vergleich von Windows- und Clean-Taskleiste"),
            SettingsIllustrationKind.Clock => Localization.T("Clock layout preview", "Vorschau der Uhrdarstellung"),
            SettingsIllustrationKind.Apps => Localization.T("Pinned apps with and without Alt", "Angeheftete Apps mit und ohne Alt"),
            SettingsIllustrationKind.SystemArea => Localization.T("System area with and without Alt", "Rechter Bereich mit und ohne Alt"),
            _ => Localization.T("Two monitor layouts", "Zwei Monitor-Layouts")
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 200 || Height < 90) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var frame = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 13);
        using var surface = new SolidBrush(Color.FromArgb(246, 249, 253));
        using var border = new Pen(Color.FromArgb(220, 230, 242));
        g.FillPath(surface, frame);
        g.DrawPath(border, frame);

        int gap = 18, side = 16;
        int boxWidth = (Width - side * 2 - gap) / 2;
        var left = new Rectangle(side, 42, boxWidth, Height - 59);
        var right = new Rectangle(left.Right + gap, 42, boxWidth, Height - 59);
        switch (kind)
        {
            case SettingsIllustrationKind.Appearance:
                Label(g, Localization.T("Normal window", "Normales Fenster"), left.X, 12, left.Width);
                Label(g, Localization.T("Maximized window", "Maximiertes Fenster"), right.X, 12, right.Width);
                Taskbar(g, left, settings.Style, settings.Color, 4, true, true, true);
                Taskbar(g, right, settings.ContextStyle ? settings.MaximizedStyle : settings.Style,
                    settings.Color, 4, true, true, true);
                break;
            case SettingsIllustrationKind.Taskbar:
                Label(g, Localization.T("Windows default", "Windows-Standard"), left.X, 12, left.Width);
                Label(g, Localization.T("Clean profile", "Clean-Profil"), right.X, 12, right.Width);
                Taskbar(g, left, 0, unchecked((int)0xD9DAE5F2), 5, true, true, true, search: true);
                Taskbar(g, right, 4, unchecked((int)0xA02A405A), 3, true, false, false);
                break;
            case SettingsIllustrationKind.Clock:
                Label(g, Localization.T("Time", "Uhrzeit"), left.X, 12, left.Width);
                Label(g, Localization.T("Time and date", "Uhrzeit und Datum"), right.X, 12, right.Width);
                Clock(g, left, false);
                Clock(g, right, true);
                break;
            case SettingsIllustrationKind.Apps:
                Label(g, Localization.T("Usual view", "Normale Ansicht"), left.X, 12, left.Width);
                Label(g, Localization.T("Hold Alt", "Alt halten"), right.X, 12, right.Width);
                Taskbar(g, left, 4, unchecked((int)0xA02A405A), settings.HidePinnedUntilAlt ?
                    Math.Clamp(settings.KeepPinnedCount, 1, 3) : settings.FocusLens ? 2 : 5, true, true, true);
                Taskbar(g, right, 4, unchecked((int)0xA02A405A), 5, true, true, true);
                break;
            case SettingsIllustrationKind.SystemArea:
                Label(g, Localization.T("Usual view", "Normale Ansicht"), left.X, 12, left.Width);
                Label(g, Localization.T("Hold Alt", "Alt halten"), right.X, 12, right.Width);
                SystemArea(g, left, false);
                SystemArea(g, right, true);
                break;
            case SettingsIllustrationKind.Layouts:
                Label(g, Localization.T("Display 1", "Bildschirm 1"), left.X, 12, left.Width);
                Label(g, Localization.T("Display 2", "Bildschirm 2"), right.X, 12, right.Width);
                Taskbar(g, left, 4, unchecked((int)0xA02A405A), 4, true, true, true);
                Taskbar(g, right, 2, unchecked((int)0xDA364A65), 3, true, false, true);
                break;
        }
    }

    void SystemArea(Graphics g, Rectangle box, bool alt)
    {
        Card(g, box);
        var modes = settings.RightModes ?? [0, 0, 0, 0];
        string[] symbols = ["10:24", "◉", "▦", "A"];
        int slotWidth = Math.Min(74, Math.Max(36, (box.Width - 24) / 4));
        int start = box.Right - 12 - slotWidth * 4;
        for (int i = 0; i < 4; i++)
        {
            bool shown = i >= modes.Length || modes[i] == RightAreaModes.Always ||
                alt && modes[i] == RightAreaModes.Peek;
            int x = start + i * slotWidth;
            using var pill = Rounded(new Rectangle(x + 2, box.Bottom - 37, slotWidth - 5, 25), 7);
            using var fill = new SolidBrush(shown ? Color.FromArgb(172, 193, 222) : Color.FromArgb(46, 64, 90));
            g.FillPath(fill, pill);
            TextRenderer.DrawText(g, shown ? symbols[i] : "·", Font,
                new Rectangle(x, box.Bottom - 36, slotWidth - 2, 23), shown ? Ink : Color.FromArgb(112, 137, 169),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    static void Clock(Graphics g, Rectangle box, bool date)
    {
        Card(g, box);
        int pillWidth = Math.Min(date ? 130 : 110, box.Width - 30);
        var pill = new Rectangle(box.Right - pillWidth - 13, box.Bottom - 53, pillWidth, 40);
        using var shape = Rounded(pill, 11);
        using var fill = new SolidBrush(Color.FromArgb(29, 44, 65));
        using var line = new Pen(Color.FromArgb(123, 152, 190));
        g.FillPath(fill, shape);
        g.DrawPath(line, shape);
        TextRenderer.DrawText(g, "10:24", SystemFonts.MessageBoxFont,
            new Rectangle(pill.X + 8, pill.Y + (date ? 2 : 10), pill.Width - 16, 21), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (date)
        {
            using var dateFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "Sat 28/11", dateFont,
                new Rectangle(pill.X + 6, pill.Y + 23, pill.Width - 12, 15), Color.FromArgb(176, 203, 235),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    static void Taskbar(Graphics g, Rectangle box, int style, int argb, int iconCount,
        bool clock, bool quick, bool tray, bool search = false)
    {
        Card(g, box);
        int top = box.Bottom - 31;
        Color tint = Color.FromArgb(argb);
        Color barColor = style switch
        {
            0 => Color.FromArgb(219, 225, 235, 246),
            1 => Color.FromArgb(63, 213, 228, 245),
            2 => Color.FromArgb(Math.Max(115, (int)tint.A), tint.R, tint.G, tint.B),
            _ => Color.FromArgb(Math.Max(146, (int)tint.A), tint.R, tint.G, tint.B)
        };
        using var bar = Rounded(new Rectangle(box.X + 8, top, box.Width - 16, 23), 7);
        using var barFill = new SolidBrush(barColor);
        g.FillPath(barFill, bar);
        int x = box.X + 21;
        if (search)
        {
            using var searchShape = Rounded(new Rectangle(x, top + 5, 54, 13), 6);
            using var searchFill = new SolidBrush(Color.FromArgb(190, 226, 236, 249));
            g.FillPath(searchFill, searchShape);
            x += 64;
        }
        using var iconFill = new SolidBrush(Color.FromArgb(160, 203, 242));
        for (int i = 0; i < iconCount && x + 11 < box.Right - 108; i++, x += 21)
            g.FillEllipse(iconFill, x, top + 7, 10, 10);
        if (tray) Dot(g, box.Right - 105, top + 12);
        if (quick) Dot(g, box.Right - 85, top + 12);
        if (clock)
            TextRenderer.DrawText(g, "10:24", SystemFonts.SmallCaptionFont,
                new Rectangle(box.Right - 65, top + 3, 52, 18), Color.White,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    static void Card(Graphics g, Rectangle box)
    {
        using var shape = Rounded(box, 9);
        using var fill = new LinearGradientBrush(box, Color.FromArgb(39, 57, 83), Color.FromArgb(99, 132, 171), 0f);
        using var border = new Pen(Color.FromArgb(72, 103, 142));
        g.FillPath(fill, shape);
        g.DrawPath(border, shape);
    }

    static void Dot(Graphics g, int x, int y)
    {
        using var fill = new SolidBrush(Color.FromArgb(218, 231, 249));
        g.FillEllipse(fill, x - 5, y - 5, 10, 10);
    }

    static void Label(Graphics g, string label, int x, int y, int width) =>
        TextRenderer.DrawText(g, label, SystemFonts.SmallCaptionFont,
            new Rectangle(x, y, width, 25), Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

    static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        path.AddRoundedRectangle(rectangle, new Size(radius, radius));
        return path;
    }
}
