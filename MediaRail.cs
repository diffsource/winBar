using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Control;

// A three-pixel media progress line. The native media session only wakes us on changes;
// the existing UI timer can call Update to advance an already visible line.
sealed class MediaRail : IDisposable
{
    readonly Control owner;
    readonly RailLine rail = new();
    readonly MediaCard card;
    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? session;
    bool requested, disposed, mediaEnabled;
    long nextConnectAttempt;
    int generation;
    string title = "", artist = "";
    bool playing;

    public MediaRail(Control owner)
    {
        this.owner = owner;
        card = new MediaCard(() => _ = ToggleAsync());
    }

    public void Update(bool enabled, bool taskbarHidden)
    {
        if (disposed) return;
        mediaEnabled = enabled;
        if (!enabled || taskbarHidden)
        {
            if (!enabled && requested) Disconnect();
            rail.Hide();
            card.Hide();
            return;
        }
        if (!requested && Environment.TickCount64 >= nextConnectAttempt) { requested = true; _ = ConnectAsync(); }
        if (session == null) { rail.Hide(); card.Hide(); return; }

        try
        {
            var playback = session.GetPlaybackInfo();
            playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) { rail.Hide(); card.Hide(); return; }
            var position = timeline.Position - timeline.StartTime;
            if (playing) position += DateTimeOffset.Now - timeline.LastUpdatedTime;
            double progress = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
            var bar = Taskbar.VisibleBounds().FirstOrDefault(r => Screen.FromRectangle(r).Primary &&
                r.Width > r.Height * 3 && r.Height >= 32 && !Taskbar.CoveredByFullscreenWindow(r));
            if (bar == Rectangle.Empty) { rail.Hide(); card.Hide(); return; }
            var screen = Screen.FromRectangle(bar).Bounds;
            int width = Math.Min(180, bar.Width / 4);
            int x = bar.Left + (bar.Width - width) / 2;
            int y = bar.Top >= screen.Top + screen.Height / 2 ? bar.Top + 2 : bar.Bottom - 5;
            rail.Bounds = new Rectangle(x, y, width, 3);
            rail.SetProgress(progress, playing);
            if (!rail.Visible) rail.Show();

            var pointer = Cursor.Position;
            var hoverZone = rail.Bounds;
            hoverZone.Inflate(14, 16);
            if (!hoverZone.Contains(pointer) && (!card.Visible || !card.Bounds.Contains(pointer)))
            {
                card.Hide();
                return;
            }
            card.SetTrack(title, artist, playing, progress);
            int cardX = Math.Clamp(x + (width - card.Width) / 2, screen.Left + 8, screen.Right - card.Width - 8);
            int cardY = bar.Top >= screen.Top + screen.Height / 2 ? bar.Top - card.Height - 8 : bar.Bottom + 8;
            card.Location = new Point(cardX, cardY);
            if (!card.Visible) card.Show();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            rail.Hide();
            card.Hide();
        }
    }

    async Task ConnectAsync()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            if (disposed || !mediaEnabled) return;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;
            ReconnectSession();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            requested = false;
            nextConnectAttempt = Environment.TickCount64 + 60_000;
        }
    }

    void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Post(ReconnectSession);
    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Post(ReconnectSession);
    void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Post(() => _ = ReadMetadataAsync());
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Post(() => card.SetTrack(title, artist, playing, rail.CurrentProgress()));
    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Post(() => rail.Invalidate());

    void Post(Action action)
    {
        if (disposed || owner.IsDisposed || !owner.IsHandleCreated) return;
        try { owner.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    void ReconnectSession()
    {
        if (disposed || manager == null) return;
        GlobalSystemMediaTransportControlsSession? next;
        try { next = manager.GetCurrentSession(); }
        catch (COMException) { next = null; }
        if (ReferenceEquals(next, session)) return;
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnMediaChanged;
            session.PlaybackInfoChanged -= OnPlaybackChanged;
            session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        session = next;
        generation++;
        title = artist = "";
        if (session == null) { rail.Hide(); card.Hide(); return; }
        session.MediaPropertiesChanged += OnMediaChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        _ = ReadMetadataAsync();
    }

    async Task ReadMetadataAsync()
    {
        var current = session;
        int version = generation;
        if (current == null) return;
        try
        {
            var properties = await current.TryGetMediaPropertiesAsync().AsTask();
            if (disposed || version != generation) return;
            title = properties?.Title ?? "";
            artist = properties?.Artist ?? "";
            card.SetTrack(title, artist, playing, rail.CurrentProgress());
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    async Task ToggleAsync()
    {
        var current = session;
        if (current == null) return;
        try { await current.TryTogglePlayPauseAsync().AsTask(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Disconnect();
        rail.Close();
        card.Close();
    }

    void Disconnect()
    {
        generation++;
        if (manager != null)
        {
            manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            manager.SessionsChanged -= OnSessionsChanged;
        }
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnMediaChanged;
            session.PlaybackInfoChanged -= OnPlaybackChanged;
            session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        manager = null;
        session = null;
        requested = false;
    }

    sealed class RailLine : Form
    {
        double progress;
        bool playing;
        public double CurrentProgress() => progress;

        public RailLine()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(28, 39, 57);
            DoubleBuffered = true;
        }

        public void SetProgress(double value, bool isPlaying)
        {
            if (Math.Abs(progress - value) < 0.004 && playing == isPlaying) return;
            progress = value;
            playing = isPlaying;
            Invalidate();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000 | 0x20; return p; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(53, 69, 93));
            using var brush = new SolidBrush(playing ? Color.FromArgb(148, 195, 255) : Color.FromArgb(124, 142, 169));
            e.Graphics.FillRectangle(brush, 0, 0, (int)Math.Round(Width * progress), Height);
        }
    }

    sealed class MediaCard : Form
    {
        readonly Action toggle;
        string title = "", artist = "";
        bool playing;
        double progress;
        readonly Rectangle playArea = new(220, 23, 42, 42);

        public MediaCard(Action toggle)
        {
            this.toggle = toggle;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Size = new Size(280, 94);
            BackColor = Color.FromArgb(27, 37, 54);
            DoubleBuffered = true;
            Opacity = 0.98;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), new Size(16, 16));
            Region = new Region(shape);
        }

        public void SetTrack(string name, string by, bool active, double value)
        {
            if (title == name && artist == by && playing == active && Math.Abs(progress - value) < 0.004) return;
            title = name;
            artist = by;
            playing = active;
            progress = value;
            Invalidate();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; }
        }

        protected override void OnMouseMove(MouseEventArgs e) => Cursor = playArea.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        protected override void OnMouseClick(MouseEventArgs e) { if (e.Button == MouseButtons.Left && playArea.Contains(e.Location)) toggle(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(1, 1, Width - 2, Height - 2), new Size(16, 16));
            using var fill = new SolidBrush(Color.FromArgb(27, 37, 54));
            using var border = new Pen(Color.FromArgb(94, 119, 154));
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(border, shape);
            using var titleFont = new Font("Segoe UI Semibold", 10, FontStyle.Bold);
            using var artistFont = new Font("Segoe UI", 8.5f);
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(e.Graphics, string.IsNullOrWhiteSpace(title) ? "Wiedergabe" : title, titleFont,
                new Rectangle(17, 16, 193, 26), Color.White, flags);
            TextRenderer.DrawText(e.Graphics, artist, artistFont, new Rectangle(17, 43, 193, 21), Color.FromArgb(171, 193, 225), flags);
            using var controlFill = new SolidBrush(Color.FromArgb(57, 83, 119));
            e.Graphics.FillEllipse(controlFill, playArea);
            using var icon = new SolidBrush(Color.White);
            if (playing)
            {
                e.Graphics.FillRectangle(icon, playArea.X + 14, playArea.Y + 12, 5, 18);
                e.Graphics.FillRectangle(icon, playArea.X + 23, playArea.Y + 12, 5, 18);
            }
            else
            {
                var points = new[] { new Point(playArea.X + 16, playArea.Y + 11), new Point(playArea.X + 16, playArea.Y + 31), new Point(playArea.X + 29, playArea.Y + 21) };
                e.Graphics.FillPolygon(icon, points);
            }
            using var track = new SolidBrush(Color.FromArgb(64, 82, 109));
            using var elapsed = new SolidBrush(Color.FromArgb(148, 195, 255));
            e.Graphics.FillRectangle(track, 17, 78, Width - 34, 3);
            e.Graphics.FillRectangle(elapsed, 17, 78, (int)((Width - 34) * progress), 3);
        }
    }
}
