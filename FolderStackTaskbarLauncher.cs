using System.Diagnostics;
using System.Runtime.InteropServices;

// A native Windows shortcut can be pinned to the taskbar. Its process lives only while the popup is open.
static class FolderStackTaskbarLauncher
{
    public static void RunPopup() => Application.Run(new PopupContext());

    public static string CreateShortcut()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("winBar-Pfad nicht gefunden.");
        string directory = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(directory);
        string shortcutPath = Path.Combine(directory, "winBar Ordnerstapel.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host ist nicht verfügbar.");
        object shell = Activator.CreateInstance(shellType)!;
        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            var type = shortcut!.GetType();
            void Set(string name, object value) => type.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, shortcut, [value]);
            Set("TargetPath", exe);
            Set("Arguments", "--folders");
            Set("WorkingDirectory", Path.GetDirectoryName(exe)!);
            Set("Description", "winBar-Ordnerstapel öffnen");
            Set("IconLocation", Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",3");
            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
        return shortcutPath;
    }

    public static void RevealShortcut(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true });
    }

    sealed class PopupContext : ApplicationContext
    {
        public PopupContext() => Application.Idle += OpenOnIdle;

        void OpenOnIdle(object? sender, EventArgs e)
        {
            Application.Idle -= OpenOnIdle;
            var pointer = Cursor.Position;
            var bar = Taskbar.VisibleBounds().FirstOrDefault(bounds => bounds.Contains(pointer));
            if (bar == Rectangle.Empty)
                bar = Taskbar.VisibleBounds().FirstOrDefault(bounds => Screen.FromRectangle(bounds).Primary);
            if (bar == Rectangle.Empty)
            {
                var screen = Screen.PrimaryScreen!.WorkingArea;
                bar = new Rectangle(screen.Left + screen.Width / 2 - 24, screen.Bottom - 48, 48, 48);
            }
            else if (bar.Width > bar.Height * 3)
            {
                bar = new Rectangle(Math.Clamp(pointer.X - 24, bar.Left, bar.Right - 48), bar.Top, 48, bar.Height);
            }
            FolderStackPopup.ShowFolders(Settings.Load().FolderStackPaths, bar);
            var popup = Application.OpenForms.OfType<FolderStackPopup>().LastOrDefault();
            if (popup == null) ExitThread();
            else popup.FormClosed += (_, _) => ExitThread();
        }
    }
}
