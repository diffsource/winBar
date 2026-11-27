using System.Globalization;

// UI copy stays next to each control so new dialogs can be translated without a resource loader.
// Unknown preferences and unsupported system languages use English.
internal static class Localization
{
    internal const string English = "English";
    internal const string German = "Deutsch";
    internal const string System = "System";

    internal static string Preference { get; private set; } = English;
    internal static bool IsGerman { get; private set; }
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> EnglishToGerman = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> GermanToEnglish = new(StringComparer.Ordinal);

    internal static void Configure(string? preference)
    {
        Preference = preference?.Trim() switch
        {
            German or "German" => German,
            System => System,
            _ => English
        };
        IsGerman = Preference == German ||
            (Preference == System && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de");
    }

    internal static string T(string english, string german)
    {
        Register(english, german);
        return IsGerman ? german : english;
    }

    internal static void Register(string english, string german)
    {
        lock (Gate)
        {
            EnglishToGerman[english] = german;
            GermanToEnglish[german] = english;
        }
    }

    private static string Translate(string text)
    {
        lock (Gate)
        {
            if (IsGerman) return EnglishToGerman.TryGetValue(text, out var german) ? german : text;
            return GermanToEnglish.TryGetValue(text, out var english) ? english : text;
        }
    }

    // Reapply registered static copy to an existing form after a language change.
    // Text boxes and non-string list entries may contain user data and are left alone.
    internal static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is not TextBoxBase) root.Text = Translate(root.Text);
        if (root.AccessibleName is { } accessible) root.AccessibleName = Translate(accessible);

        if (root is ComboBox combo && combo.DataSource == null)
        {
            combo.BeginUpdate();
            try
            {
                int selected = combo.SelectedIndex;
                for (int i = 0; i < combo.Items.Count; i++)
                    if (combo.Items[i] is string item) combo.Items[i] = Translate(item);
                if (selected >= 0 && selected < combo.Items.Count) combo.SelectedIndex = selected;
            }
            finally { combo.EndUpdate(); }
        }
        else if (root is ListBox list && root is not CheckedListBox && list.DataSource == null)
        {
            list.BeginUpdate();
            try
            {
                for (int i = 0; i < list.Items.Count; i++)
                    if (list.Items[i] is string item) list.Items[i] = Translate(item);
            }
            finally { list.EndUpdate(); }
        }

        if (root is ToolStrip strip)
            foreach (ToolStripItem item in strip.Items) Apply(item);
        foreach (Control child in root.Controls) Apply(child);
        if (root.ContextMenuStrip is { } menu)
            foreach (ToolStripItem item in menu.Items) Apply(item);
    }

    private static void Apply(ToolStripItem item)
    {
        if (item.Text is { } text) item.Text = Translate(text);
        if (item.AccessibleName is { } accessible) item.AccessibleName = Translate(accessible);
        if (item is ToolStripDropDownItem dropdown)
            foreach (ToolStripItem child in dropdown.DropDownItems) Apply(child);
    }
}
