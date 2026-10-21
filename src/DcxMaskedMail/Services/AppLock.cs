using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DcxMail.Views;
using Microsoft.Win32;

namespace DcxMail.Services;

/// <summary>
/// App lock (same as DarkCipher ServerManagement): hides every window behind a sign-in screen (master password and/or
/// authenticator code) after inactivity, when Windows locks or the PC sleeps, or on demand. Mail keeps being filed
/// while locked. When the app is only in the notification area, the lock is armed and the sign-in screen shows the
/// next time a window is opened. Checked every 20 s with GetLastInputInfo — no hooks, negligible cost.
/// </summary>
public static class AppLock
{
    private static DispatcherTimer? _timer;
    private static bool _locked, _armed;

    public static bool CanLock => SecurityPolicy.CanLock;
    public static bool IsLocked => _locked || _armed;
    public static event Action? Changed;

    public static void Start()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Tick(), Application.Current.Dispatcher);
        _timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPower;
    }

    public static void Stop()
    {
        _timer?.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPower;
    }

    private static void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && AppSettings.Current.LockOnWindowsLock)
            Application.Current.Dispatcher.BeginInvoke(() => Lock("Windows was locked"));
    }

    private static void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && AppSettings.Current.LockOnWindowsLock)
            Application.Current.Dispatcher.BeginInvoke(() => Lock("the PC went to sleep"));
    }

    private static void Tick()
    {
        var m = AppSettings.Current.AutoLockMinutes;
        if (m <= 0 || IsLocked || !CanLock) return;
        if (IdleTime() >= TimeSpan.FromMinutes(m)) Lock($"{m} min without input");
    }

    /// <summary>See <see cref="SecurityPolicy.ReconcileSandbox"/>.</summary>
    public static bool ReconcileSandbox()
    {
        return SecurityPolicy.ReconcileSandbox();
    }

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Locks now. Returns false if no factor is configured (nothing to unlock with).</summary>
    public static bool Lock(string reason = "locked by you")
    {
        if (IsLocked) return true;
        if (!CanLock) return false;
        AuditLog.Write("locked", reason);
        var windows = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w is not UnlockWindow).ToList();
        if (windows.Count == 0)
        {
            // Only in the notification area: nothing to hide; ask on the next open.
            _armed = true;
            Changed?.Invoke();
            return true;
        }
        _locked = true;
        Changed?.Invoke();
        foreach (var w in windows) w.Hide();
        bool open = Gate(lockScreen: true);
        _locked = false;
        Changed?.Invoke();
        if (open) foreach (var w in windows) { try { w.Show(); } catch { } }
        return true;
    }

    /// <summary>Called before a window is shown from the notification area: asks for sign-in if the lock is armed.
    /// Returns false if the user chose to exit.</summary>
    public static bool EnsureUnlocked()
    {
        if (_locked) return false;          // the lock screen is already up
        if (!_armed) return true;
        _locked = true;
        bool open = Gate(lockScreen: true);
        _locked = false;
        if (open) _armed = false;
        Changed?.Invoke();
        return open;
    }

    /// <summary>Shows the sign-in screen until it succeeds (true) or the user exits the app (false).</summary>
    private static bool Gate(bool lockScreen)
    {
        while (true)
        {
            var gate = new UnlockWindow(needPassword: SecureStore.HasPassword, needCode: SecureStore.HasTotp, lockScreen: lockScreen);
            if (gate.ShowDialog() == true) return true;
            if (gate.QuitRequested)
            {
                if (Application.Current.MainWindow is MainWindow mw) { App.ForceExit = true; mw.ExitApp(); }
                SecureStore.Forget();
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
