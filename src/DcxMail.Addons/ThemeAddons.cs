using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxMail.Services;

namespace DcxMail.Addons;

/// <summary>A theme add-on's colours. Pure data: no program runs. Unknown keys and anything that is not a #RRGGBB colour
/// are refused when the package is inspected, so an installed theme can only recolour, never inject (same rule as
/// ServerManagement: no XAML, no code). Colours are kept as validated "#RRGGBB" strings; the app turns them into
/// brushes.</summary>
public sealed class ThemePack
{
    /// <summary>The palette keys the app's theme defines (ThemeManager checks at start-up that it defines exactly these).</summary>
    public static readonly string[] PaletteKeys =
        { "Bg", "Surface", "Input", "Fg", "Muted", "Line", "Hover", "Track", "ScrollThumb", "HeaderBg", "HeaderFg", "Good", "Warn", "Bad" };
    public static readonly string[] AccentKeys = { "Accent", "AccentHover", "OnAccent", "Brand" };
    public const int MaxBytes = 64 * 1024;

    /// <summary>Palette key → (dark, light) as "#RRGGBB". A missing side keeps the app's own colour for that mode.</summary>
    public Dictionary<string, (string? Dark, string? Light)> Palette { get; } = new(StringComparer.Ordinal);

    public string? Get(string key, bool dark) => Palette.TryGetValue(key, out var p) ? (dark ? p.Dark : p.Light) : null;

    /// <summary>Parses theme.json strictly. Throws <see cref="InvalidDataException"/> with a plain reason.</summary>
    public static ThemePack Parse(string json)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("theme file is larger than 64 KB");
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 6, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new InvalidDataException("theme file is not valid JSON: " + ex.Message); }
        if (root is not JsonObject o) throw new InvalidDataException("theme file must be a JSON object");
        var t = new ThemePack();
        var allowed = PaletteKeys.Concat(AccentKeys).ToHashSet(StringComparer.Ordinal);
        static string Hex(JsonNode? v, string what)
        {
            string s;
            try { s = v?.GetValue<string>() ?? ""; } catch (InvalidOperationException) { s = ""; }
            if (s.Length != 7 || s[0] != '#' || !s[1..].All(Uri.IsHexDigit)) throw new InvalidDataException($"“{what}” must be #RRGGBB");
            return s.ToUpperInvariant();
        }
        foreach (var (prop, value) in o)
        {
            switch (prop)
            {
                case "colors":
                    if (value is not JsonObject colors) throw new InvalidDataException("\"colors\" must be an object");
                    foreach (var (key, pair) in colors)
                    {
                        if (!allowed.Contains(key)) throw new InvalidDataException($"unknown colour key “{key}”");
                        if (pair is not JsonObject dl) throw new InvalidDataException($"“{key}” must be {{\"dark\": \"#RRGGBB\", \"light\": \"#RRGGBB\"}}");
                        if (dl.Any(kv => kv.Key is not ("dark" or "light"))) throw new InvalidDataException($"“{key}” may only have \"dark\" and \"light\"");
                        t.Palette[key] = (dl["dark"] is { } d ? Hex(d, key + ".dark") : null, dl["light"] is { } l ? Hex(l, key + ".light") : null);
                    }
                    break;
                case "name" or "notes":
                    string text;
                    try { text = value?.GetValue<string>() ?? ""; } catch (InvalidOperationException) { throw new InvalidDataException($"\"{prop}\" must be text"); }
                    if (text.Length > 1000) throw new InvalidDataException($"\"{prop}\" is longer than 1000 characters");
                    break;
                default:
                    throw new InvalidDataException($"unknown field “{prop}” (allowed: colors, name, notes)");
            }
        }
        if (t.Palette.Count == 0) throw new InvalidDataException("the theme sets no colours");
        return t;
    }
}

/// <summary>The theme add-on in use (AppSettings.ThemeAddon). Its sealed files are decrypted and hash-checked every time
/// it is applied; a changed theme is disabled with the reason and the app's own colours come back.</summary>
public static class ThemeAddons
{
    /// <summary>Raised when the theme in use may have changed; the app repaints (ThemeManager.Apply).</summary>
    public static event Action? Changed;

    public static ThemePack? Active()
    {
        if (AppSettings.Current.ThemeAddon is not { } id || AddonPackages.Find(id) is not { Enabled: true } a || a.Manifest.Theme.Length == 0) return null;
        try { return ThemePack.Parse(AddonPackages.ReadInstalledText(a, a.Manifest.Theme)); }
        catch (InvalidDataException ex)
        {
            a.Enabled = false; a.DisabledReason = "files changed after install: " + ex.Message;
            AppSettings.Current.ThemeAddon = null;
            try { AppSettings.Save(); } catch (IOException) { /* the in-memory state is already safe; the next save writes it */ }
            AuditLog.Write("add-on disabled", $"{a.Id} · theme failed its check: {ex.Message}");
            return null;
        }
    }

    /// <summary>Repaints with the theme in use (or the app's own palette).</summary>
    public static void Reapply()
    {
        if (Changed is { } handler) handler();
        else _ = Active();   // no UI (tests): still verify, so a tampered theme is disabled
    }
}
