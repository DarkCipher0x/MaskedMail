using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DcxMail.Services;

public enum ThemeMode { System, Dark, Light }

/// <summary>The darkcipher0x "terminal" palette, identical to DarkCipher ServerManagement (teal on black / ink on paper).</summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["Bg"]          = ("#050909", "#F1F6F6"),
        ["Surface"]     = ("#0B1314", "#E3EDED"),
        ["Input"]       = ("#020505", "#FAFDFD"),
        ["Fg"]          = ("#D2F2EF", "#08191A"),
        ["Muted"]       = ("#5A8986", "#4E6E6C"),
        ["Line"]        = ("#16302F", "#BCD2D1"),
        ["Hover"]       = ("#0F2122", "#D5E5E5"),
        ["Track"]       = ("#0F2122", "#CFDFDF"),
        ["ScrollThumb"] = ("#234746", "#A3BEBD"),
        ["HeaderBg"]    = ("#000000", "#061010"),
        ["HeaderFg"]    = ("#D2F2EF", "#E2F4F2"),
        ["Good"]        = ("#36D27A", "#137A3F"),
        ["Warn"]        = ("#E8C547", "#8A6A00"),
        ["Bad"]         = ("#FF5F6D", "#B3202E"),
    };

    public const string Brand = "36D2CB";   // darkcipher0x brand teal

    /// <summary>The palette keys a theme add-on may replace.</summary>
    public static IEnumerable<string> PaletteKeys => Palette.Keys;

    /// <summary>A theme add-on colour ("#RRGGBB", validated when the package was inspected) as a WPF colour.</summary>
    private static Color? C(string? hex) => hex == null ? null : (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>The type ramp shared with ServerManagement; every font size in the UI refers to one of these.</summary>
    public static readonly (string Key, double Px)[] Ramp =
    {
        ("FsMicro", 10.5), ("FsCaption", 11), ("FsSmall", 11.5), ("FsMeta", 12), ("FsBody", 12.5), ("FsControl", 13), ("FsBase", 13.5),
        ("FsInput", 14), ("FsTitle", 15), ("FsHeading", 16), ("FsH1", 17), ("FsCode", 18), ("FsValue", 20), ("FsIcon", 26),
    };

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        var res = Application.Current.Resources;
        foreach (var (key, px) in Ramp) res[key] = px;
        // A theme add-on (sealed, hash-checked on every use) replaces any colour it names; the rest keep the app's own.
        var theme = SecureStore.IsUnlocked ? Addons.ThemeAddons.Active() : null;
        foreach (var (key, (dark, light)) in Palette)
            res[key] = Frozen(C(theme?.Get(key, IsDark)) ?? (Color)ColorConverter.ConvertFromString(IsDark ? dark : light));

        var brand = C(theme?.Get("Brand", IsDark)) ?? (Color)ColorConverter.ConvertFromString("#" + Brand);
        var bg = Hex("Bg");
        var accent = C(theme?.Get("Accent", IsDark)) ?? (IsDark ? brand : Mix(brand, Colors.Black, 0.42));
        res["Accent"] = Frozen(accent);
        res["AccentHover"] = Frozen(C(theme?.Get("AccentHover", IsDark)) ?? (IsDark ? Mix(accent, Colors.White, 0.25) : Mix(accent, Colors.Black, 0.2)));
        res["Brand"] = Frozen(brand);
        res["OnAccent"] = Frozen(C(theme?.Get("OnAccent", IsDark)) ?? (Luminance(accent) > 0.5 ? Colors.Black : Colors.White));
        res["Selection"] = Frozen(Mix(bg, accent, IsDark ? 0.18 : 0.20));

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    public static Color Hex(string key) =>
        Application.Current?.Resources[key] is SolidColorBrush b ? b.Color
        : Palette.TryGetValue(key, out var p) ? (Color)ColorConverter.ConvertFromString(IsDark ? p.Dark : p.Light) : Colors.Gray;

    public static string Css(string key) { var c = Hex(key); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static void FollowSystemChanges()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                Application.Current.Dispatcher.Invoke(() => Apply(ThemeMode.System));
        };
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
        catch { return true; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }
}
