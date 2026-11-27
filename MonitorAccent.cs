using System.Runtime.InteropServices;

internal sealed record MonitorTaskbar(IntPtr Handle, Rectangle Bounds, Screen Screen);

internal static class MonitorTaskbars
{
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    public static IReadOnlyList<MonitorTaskbar> Enumerate()
    {
        var result = new List<MonitorTaskbar>();
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) Add(main);
        var handle = IntPtr.Zero;
        while ((handle = FindWindowEx(IntPtr.Zero, handle, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) Add(handle);
        return result;

        void Add(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var screens = Screen.AllScreens.Where(screen => screen.Bounds.Contains(center)).Take(2).ToArray();
            if (screens.Length == 1) result.Add(new MonitorTaskbar(hwnd, bounds, screens[0]));
        }
    }
}

internal static class MonitorAccent
{
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr handle, ref WinCompAttrData data);

    static readonly Dictionary<IntPtr, (int State, int Color)> applied = [];
    static readonly int[] accentFor = [0, 2, 2, 3, 4];

    public static void Apply(IEnumerable<MonitorTaskbar> bars, Func<Screen, MonitorVisual> selectVisual,
        Screen? foregroundScreen, bool foregroundMaximized, int? foregroundAppTint)
    {
        var current = new HashSet<IntPtr>();
        foreach (var bar in bars)
        {
            current.Add(bar.Handle);
            var visual = selectVisual(bar.Screen);
            bool activeOnThisScreen = foregroundScreen != null &&
                string.Equals(foregroundScreen.DeviceName, bar.Screen.DeviceName, StringComparison.OrdinalIgnoreCase);
            int style = Math.Clamp(visual.ContextStyle && foregroundMaximized && activeOnThisScreen
                ? visual.MaximizedStyle : visual.Style, 0, 4);
            int color = visual.AppAccent && activeOnThisScreen && foregroundAppTint is int tint
                ? (visual.Color & unchecked((int)0xFF000000)) | tint
                : visual.AutoContrast ? Taskbar.SystemTint(visual.Color) : visual.Color;
            int state = accentFor[style];
            int abgr = style == 1 ? 0 : Taskbar.ToAbgr(color);
            if (applied.TryGetValue(bar.Handle, out var previous) && previous == (state, abgr)) continue;
            if (style == 0 && !applied.ContainsKey(bar.Handle)) continue;
            Set(bar.Handle, state, abgr);
            applied[bar.Handle] = (state, abgr);
        }
        foreach (var stale in applied.Keys.Where(handle => !current.Contains(handle)).ToArray()) applied.Remove(stale);
    }

    public static void Reset()
    {
        foreach (var handle in applied.Keys.ToArray()) Set(handle, 0, 0);
        applied.Clear();
    }

    static void Set(IntPtr handle, int state, int abgr)
    {
        var policy = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = abgr };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WinCompAttrData { Attribute = 19, Data = pointer, SizeOfData = Marshal.SizeOf<AccentPolicy>() };
            SetWindowCompositionAttribute(handle, ref data);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
