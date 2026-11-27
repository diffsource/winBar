using System.Drawing.Drawing2D;

sealed class WelcomeForm : Form
{
    readonly Label stepLabel = new();
    readonly Label heading = new();
    readonly Label description = new();
    readonly Label hint = new();
    readonly Button back = new();
    readonly Button next = new();
    readonly TaskbarPreview preview = new();
    int step;

    static readonly (string Heading, string Description, string Hint)[] Pages =
    [
        ("Eine ruhigere Taskleiste", "Wähle Glass, Glacier, Ink oder Adaptive. Weniger Ablenkung lässt mehr Platz für das, was du gerade tust.", "Unter Aussehen kannst du den Look jederzeit ändern."),
        ("Wichtiges bleibt griffbereit", "Halte Alt über der Taskleiste: Der Einstellungen-Button und weitere Icons erscheinen bei Bedarf.", "Strg + Alt + S öffnet die Einstellungen auch bei versteckter Taskleiste."),
        ("Dein Setup, ein Klick", "Gruppiere Apps in Stapeln, speichere Fenster als Arbeitsbereich und gestalte jeden Monitor für sich. Alles bleibt lokal.", "Starte mit einem Design. Stapel, Arbeitsbereiche und Monitore kannst du später einrichten.")
    ];

    public WelcomeForm()
    {
        Text = "Willkommen bei winBar";
        ClientSize = new Size(620, 480);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 248, 252);

        var hero = new Panel { Dock = DockStyle.Top, Height = 170, BackColor = Color.FromArgb(23, 34, 53) };
        Controls.Add(hero);
        var brand = new Label
        {
            Text = "winBar", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 22, FontStyle.Bold),
            AutoSize = true, Location = new Point(34, 22)
        };
        hero.Controls.Add(brand);
        var brandDetail = new Label
        {
            Text = "Mehr Raum für das Wesentliche.", ForeColor = Color.FromArgb(173, 195, 228),
            AutoSize = true, Location = new Point(38, 65)
        };
        hero.Controls.Add(brandDetail);
        preview.Location = new Point(34, 106);
        preview.Size = new Size(550, 48);
        hero.Controls.Add(preview);

        stepLabel.Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold);
        stepLabel.ForeColor = Color.FromArgb(68, 99, 151);
        stepLabel.Location = new Point(36, 192);
        stepLabel.Size = new Size(360, 22);
        Controls.Add(stepLabel);

        heading.Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold);
        heading.ForeColor = Color.FromArgb(28, 42, 63);
        heading.Location = new Point(34, 218);
        heading.Size = new Size(552, 46);
        Controls.Add(heading);

        description.ForeColor = Color.FromArgb(66, 79, 101);
        description.Location = new Point(38, 272);
        description.Size = new Size(540, 78);
        Controls.Add(description);

        hint.Font = new Font("Segoe UI", 9);
        hint.ForeColor = Color.FromArgb(68, 99, 151);
        hint.Location = new Point(38, 362);
        hint.Size = new Size(540, 50);
        Controls.Add(hint);

        back.Text = "Zurück";
        back.Location = new Point(36, 430);
        back.Size = new Size(92, 34);
        back.Click += (_, _) => { step--; RefreshPage(); };
        Controls.Add(back);

        var skip = new Button { Text = "Später", Location = new Point(340, 430), Size = new Size(125, 34), FlatStyle = FlatStyle.Flat };
        skip.FlatAppearance.BorderSize = 0;
        skip.Click += (_, _) => Close();
        Controls.Add(skip);

        next.Location = new Point(474, 430);
        next.Size = new Size(110, 34);
        next.BackColor = Color.FromArgb(52, 95, 164);
        next.ForeColor = Color.White;
        next.FlatStyle = FlatStyle.Flat;
        next.FlatAppearance.BorderSize = 0;
        next.Click += (_, _) => { if (step == Pages.Length - 1) Close(); else { step++; RefreshPage(); } };
        Controls.Add(next);
        AcceptButton = next;
        CancelButton = skip;
        RefreshPage();
    }

    void RefreshPage()
    {
        stepLabel.Text = $"Schritt {step + 1} von {Pages.Length}";
        heading.Text = Pages[step].Heading;
        description.Text = Pages[step].Description;
        hint.Text = Pages[step].Hint;
        back.Enabled = step > 0;
        next.Text = step == Pages.Length - 1 ? "Fertig" : "Weiter";
        preview.SetStep(step);
    }

    sealed class TaskbarPreview : Control
    {
        int step;
        public void SetStep(int value) { step = value; Invalidate(); }

        public TaskbarPreview() { DoubleBuffered = true; AccessibleName = "Vorschau einer aufgeräumten Windows-Taskleiste"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var surface = new SolidBrush(Color.FromArgb(43, 58, 81));
            using var muted = new SolidBrush(Color.FromArgb(105, 128, 160));
            using var accent = new SolidBrush(Color.FromArgb(134, 184, 250));
            using var outline = new Pen(Color.FromArgb(103, 126, 159));
            using var path = new GraphicsPath();
            path.AddRoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), new Size(14, 14));
            e.Graphics.FillPath(surface, path);
            e.Graphics.DrawPath(outline, path);
            e.Graphics.FillRectangle(accent, 18, 17, 13, 13);
            e.Graphics.FillRectangle(accent, 33, 17, 13, 13);
            using var icon = new SolidBrush(Color.FromArgb(184, 207, 237));
            e.Graphics.FillEllipse(icon, 72, 14, 19, 19);
            e.Graphics.FillEllipse(icon, 104, 14, 19, 19);
            if (step == 1)
            {
                e.Graphics.FillEllipse(accent, 136, 14, 19, 19);
                e.Graphics.FillEllipse(muted, 169, 14, 19, 19);
            }
            using var clockFont = new Font("Segoe UI Semibold", 10, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, "10:24", clockFont, new Rectangle(Width - 82, 12, 66, 25),
                Color.FromArgb(229, 239, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
