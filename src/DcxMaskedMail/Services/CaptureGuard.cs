using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DcxMail.Services;

/// <summary>
/// Keeps windows out of screenshots, screen recording, streaming (OBS, Teams, Discord) and remote viewers, using
/// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE): the window shows black (or not at all) in any capture.
/// <list type="bullet">
/// <item>Always: the sign-in / lock screen and the security dialogs (2FA QR code and key, recovery codes, password and
/// backup-password prompts).</item>
/// <item>Optionally every MaskedMail window (Settings → Security &amp; privacy → "Hide MaskedMail from screen capture").</item>
/// </list>
/// </summary>
public static class CaptureGuard
{
    private const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    private static readonly DependencyProperty AlwaysProperty = DependencyProperty.RegisterAttached("Always", typeof(bool), typeof(CaptureGuard), new PropertyMetadata(false));

    /// <summary>Hooks every window the app creates (applied when its handle exists).</summary>
    public static void Start()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) Apply(w); }));
    }

    /// <summary>Marks a window as always hidden from capture (security dialogs).</summary>
    public static void Always(Window w)
    {
        w.SetValue(AlwaysProperty, true);
        w.SourceInitialized += (_, _) => Apply(w);
    }

    public static void Apply(Window w)
    {
        // (Off-screen self-test renders use RenderTargetBitmap, which display affinity doesn't affect.)
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool hide = (bool)w.GetValue(AlwaysProperty) || AppSettings.Current.HideFromCapture;
        SetWindowDisplayAffinity(hwnd, hide ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    /// <summary>After the setting changes: re-applies to every open window.</summary>
    public static void Refresh()
    {
        foreach (Window w in Application.Current.Windows) Apply(w);
    }

    public static bool IsExcluded(Window w) =>
        GetWindowDisplayAffinity(new WindowInteropHelper(w).Handle, out var a) && a == WDA_EXCLUDEFROMCAPTURE;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
