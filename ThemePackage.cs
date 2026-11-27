using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Shareable themes deliberately contain visual values only: no app paths, commands or private profiles.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ThemePackage
{
    public required int Version { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required int Style { get; init; }
    public required int Color { get; init; }
    public required bool AutoContrast { get; init; }
    public required bool ContextStyle { get; init; }
    public required int MaximizedStyle { get; init; }

    public static string Export(ThemeDefinition theme) => JsonSerializer.Serialize(new ThemePackage
    {
        Version = 1,
        Name = theme.Name,
        Description = theme.Description,
        Style = theme.Style,
        Color = theme.Color,
        AutoContrast = theme.AutoContrast,
        ContextStyle = theme.ContextStyle,
        MaximizedStyle = theme.MaximizedStyle
    }, new JsonSerializerOptions { WriteIndented = true });

    public static ThemeDefinition Import(string json)
    {
        if (json.Length > 16 * 1024) throw new InvalidDataException("Design-Datei ist zu groß.");
        ThemePackage package;
        try { package = JsonSerializer.Deserialize<ThemePackage>(json) ?? throw new InvalidDataException("Leere Design-Datei."); }
        catch (JsonException ex) { throw new InvalidDataException("Ungültiges Design-Format.", ex); }
        if (package.Version != 1 || package.Style is < 0 or > 4 || package.MaximizedStyle is < 0 or > 4)
            throw new InvalidDataException("Nicht unterstützte Design-Werte.");
        var name = package.Name?.Trim() ?? "";
        var description = package.Description?.Trim() ?? "";
        if (name.Length is < 1 or > 32 || description.Length is < 1 or > 80 ||
            name.Any(char.IsControl) || description.Any(char.IsControl))
            throw new InvalidDataException("Ungültiger Design-Name oder Beschreibung.");
        var canonical = $"{name}\n{description}\n{package.Style}\n{package.Color}\n{package.AutoContrast}\n{package.ContextStyle}\n{package.MaximizedStyle}";
        var id = "shared-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12].ToLowerInvariant();
        var rgb = System.Drawing.Color.FromArgb(package.Color | unchecked((int)0xFF000000));
        var previewBase = Blend(rgb, System.Drawing.Color.FromArgb(26, 39, 57), 0.38);
        var previewTint = Blend(rgb, System.Drawing.Color.FromArgb(143, 172, 210), 0.42);
        return new ThemeDefinition(id, name, description, package.Style, package.Color,
            package.AutoContrast, package.ContextStyle, package.MaximizedStyle,
            previewBase, previewTint, System.Drawing.Color.FromArgb(194, 219, 248));
    }

    public static ThemeDefinition ImportFile(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Design-Datei ist zu groß.");
        return Import(File.ReadAllText(path, Encoding.UTF8));
    }

    public static void ExportFile(string path, ThemeDefinition theme) => File.WriteAllText(path, Export(theme), Encoding.UTF8);

    static System.Drawing.Color Blend(System.Drawing.Color a, System.Drawing.Color b, double weight) =>
        System.Drawing.Color.FromArgb(
            (int)Math.Round(a.R * weight + b.R * (1 - weight)),
            (int)Math.Round(a.G * weight + b.G * (1 - weight)),
            (int)Math.Round(a.B * weight + b.B * (1 - weight)));
}
