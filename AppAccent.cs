using System.Diagnostics;
using System.Runtime.InteropServices;

// Samples the foreground app icon only when its process changes, then caches the result.
static class AppAccent
{
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    static readonly Dictionary<uint, int?> cache = [];
    static uint lastProcess;
    static int? lastColor;

    public static int? ForegroundColor(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == lastProcess) return lastColor;
        lastProcess = pid;
        if (pid == 0 || pid == (uint)Environment.ProcessId) return lastColor = null;
        if (cache.TryGetValue(pid, out var color)) return lastColor = color;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var file = process.MainModule?.FileName;
            if (file == null) return lastColor = null;
            using var icon = Icon.ExtractAssociatedIcon(file);
            if (icon == null) return lastColor = null;
            using var bitmap = icon.ToBitmap();
            long red = 0, green = 0, blue = 0, weightSum = 0;
            for (int y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 16))
                for (int x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 16))
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A < 90) continue;
                    int saturation = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                    int weight = pixel.A * Math.Max(15, saturation);
                    red += pixel.R * (long)weight;
                    green += pixel.G * (long)weight;
                    blue += pixel.B * (long)weight;
                    weightSum += weight;
                }
            if (weightSum == 0) return lastColor = null;
            int r = (int)(red / weightSum), g = (int)(green / weightSum), b = (int)(blue / weightSum);
            int Blend(int channel, int baseChannel) => (channel * 38 + baseChannel * 62) / 100;
            color = (Blend(r, 28) << 16) | (Blend(g, 39) << 8) | Blend(b, 57);
            if (cache.Count > 48) cache.Clear();
            cache[pid] = color;
            return lastColor = color;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            return lastColor = null;
        }
    }
}
