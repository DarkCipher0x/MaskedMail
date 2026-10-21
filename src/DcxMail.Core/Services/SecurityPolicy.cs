namespace DcxMail.Services;

/// <summary>The rules behind the app lock and sandbox mode, free of UI (the lock screen itself is the app's AppLock).</summary>
public static class SecurityPolicy
{
    /// <summary>The lock screen needs something to unlock with: a master password or two-factor sign-in.</summary>
    public static bool CanLock => SecureStore.HasPassword || SecureStore.HasTotp;

    /// <summary>Sandbox mode promises "master password at start + auto-lock". If the password requirement was removed
    /// the promise no longer holds, so the flag is switched off (and logged) rather than shown while untrue; with
    /// sandbox on, auto-lock can't be "never". Returns true when something changed.</summary>
    public static bool ReconcileSandbox()
    {
        var s = AppSettings.Current;
        if (!s.SandboxMode) return false;
        if (!SecureStore.PasswordRequiredOnStart)
        {
            s.SandboxMode = false;
            AuditLog.Write("sandbox mode off", "the master password is no longer required at start");
        }
        else if (s.AutoLockMinutes <= 0) s.AutoLockMinutes = 15;
        else return false;
        AppSettings.Save();
        return true;
    }
}
