using System.ComponentModel;
using System.Drawing.Drawing2D;

// A click anywhere on the track starts a drag. The native TrackBar requires
// grabbing its tiny thumb, which is awkward at high DPI and with touch input.
internal class OpacitySlider : Control
{
    const int TrackInset = 16;
    const int ValueWidth = 58;
    int value;
    bool dragging;

    public event EventHandler? ValueChanged;
    public event EventHandler? DragCompleted;
    public bool IsDragging => dragging;

    [DefaultValue(0)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int Value
    {
        get => value;
        set
        {
            int next = Math.Clamp(value, 0, 255);
            if (this.value == next) return;
            this.value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public OpacitySlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(220, 42);
        MinimumSize = new Size(120, 42);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = Localization.T("Opacity", "Deckkraft");
        AccessibleDescription = Localization.T("Drag anywhere or use the arrow keys.", "Überall ziehen oder die Pfeiltasten verwenden.");
    }

    int TrackRight => Math.Max(TrackInset + 1, ClientSize.Width - ValueWidth);

    void SetFromPointer(int x)
    {
        double fraction = Math.Clamp((x - TrackInset) / (double)(TrackRight - TrackInset), 0, 1);
        Value = (int)Math.Round(fraction * 255);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        Focus();
        dragging = true;
        Capture = true;
        SetFromPointer(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) SetFromPointer(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging || e.Button != MouseButtons.Left) return;
        SetFromPointer(e.X);
        dragging = false;
        Capture = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && dragging)
        {
            dragging = false;
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData)
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= 1; break;
            case Keys.Right or Keys.Up: Value += 1; break;
            case Keys.PageDown: Value -= 16; break;
            case Keys.PageUp: Value += 16; break;
            case Keys.Home: Value = 0; break;
            case Keys.End: Value = 255; break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int y = ClientSize.Height / 2;
        int right = TrackRight;
        var track = new Rectangle(TrackInset, y - 3, right - TrackInset, 6);
        using var background = new SolidBrush(Enabled ? Color.FromArgb(220, 228, 239) : Color.FromArgb(232, 234, 239));
        using var fill = new SolidBrush(Enabled ? Color.FromArgb(61, 112, 186) : Color.FromArgb(172, 181, 195));
        using var trackShape = new GraphicsPath();
        trackShape.AddRoundedRectangle(track, new Size(3, 3));
        e.Graphics.FillPath(background, trackShape);
        int thumbX = TrackInset + (int)Math.Round((right - TrackInset) * value / 255d);
        if (thumbX > TrackInset)
        {
            var activeTrack = new Rectangle(TrackInset, y - 3, thumbX - TrackInset, 6);
            using var activeShape = new GraphicsPath();
            activeShape.AddRoundedRectangle(activeTrack, new Size(3, 3));
            e.Graphics.FillPath(fill, activeShape);
        }
        using var thumbFill = new SolidBrush(Color.White);
        using var thumbBorder = new Pen(Enabled ? Color.FromArgb(61, 112, 186) : Color.FromArgb(172, 181, 195), 2);
        e.Graphics.FillEllipse(thumbFill, thumbX - 10, y - 10, 20, 20);
        e.Graphics.DrawEllipse(thumbBorder, thumbX - 10, y - 10, 20, 20);
        TextRenderer.DrawText(e.Graphics, $"{value * 100 / 255}%", Font,
            new Rectangle(right + 10, 0, ClientSize.Width - right - 12, ClientSize.Height),
            Enabled ? Color.FromArgb(36, 56, 83) : Color.FromArgb(139, 150, 165),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(1, 1, ClientSize.Width - 3, ClientSize.Height - 3));
    }
}
