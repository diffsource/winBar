using System.ComponentModel;
using System.Diagnostics;

sealed record AppStackLaunchResult(int Started, List<string> Failed);

static class AppStackLauncher
{
    // Only files the user explicitly selected in the stack editor are launched.
    public static Task<AppStackLaunchResult> LaunchAllAsync(AppStackGroup group) => Task.Run(() =>
    {
        int started = 0;
        var failed = new List<string>();
        foreach (var path in (group.LaunchPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var extension = Path.GetExtension(path);
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
                    (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                     !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)))
                {
                    failed.Add(path);
                    continue;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
                started++;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                failed.Add(path);
            }
        }
        return new AppStackLaunchResult(started, failed);
    });
}
