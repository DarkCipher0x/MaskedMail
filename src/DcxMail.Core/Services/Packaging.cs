using System.Runtime.InteropServices;
using System.Text;

namespace DcxMail.Services;

/// <summary>
/// The same binaries ship as the MSI/zip (website) and as an MSIX for the Microsoft Store, which signs the package for
/// free (same approach as DarkCipher ServerManagement). A Store install runs with package identity:
/// <list type="bullet">
/// <item>the taskbar identity (AUMID) comes from the manifest, so the app doesn't set its own;</item>
/// <item>"Start with Windows" is a Windows startup task declared in the manifest (a registry Run entry can't start a
/// packaged app); Windows lists it in Task Manager → Startup apps, and the app starts in the tray as usual;</item>
/// <item>Windows (the Store) owns updates and uninstall.</item>
/// </list>
/// </summary>
public static class Packaging
{
    private const int AppModelErrorNoPackage = 15700;
    /// <summary>The startup task's id in installer\msix\AppxManifest.xml.</summary>
    public const string StartupTaskId = "MaskedMailStartup";
    /// <summary>The manifest's Application Id.</summary>
    public const string ApplicationId = "App";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

    private static readonly Lazy<string?> _family = new(() =>
    {
        try
        {
            int len = 0;
            if (GetCurrentPackageFamilyName(ref len, null) == AppModelErrorNoPackage || len <= 0) return null;
            var sb = new StringBuilder(len);
            return GetCurrentPackageFamilyName(ref len, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }   // older Windows without the API: never packaged
    });

    /// <summary>The package family name when installed from an MSIX (Store or sideloaded), else null.</summary>
    public static string? FamilyName => _family.Value;
    public static bool IsPackaged => FamilyName != null;

    /// <summary>True when Windows started this packaged app through its startup task (sign-in): start in the tray.</summary>
    public static bool LaunchedByStartupTask()
    {
        if (!IsPackaged) return false;
        try { return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind == Windows.ApplicationModel.Activation.ActivationKind.StartupTask; }
        catch { return false; }
    }

    /// <summary>Turns the packaged app's startup task on or off. Returns a message when Windows won't allow it
    /// (the user or a policy turned it off in Task Manager / Settings → Apps → Startup), else null.</summary>
    public static async Task<string?> SetStartupTaskAsync(bool on)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (!on) { task.Disable(); return null; }
            var state = await task.RequestEnableAsync();
            return state switch
            {
                Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => null,
                Windows.ApplicationModel.StartupTaskState.DisabledByUser => "Start-up was turned off for MaskedMail in Windows (Settings → Apps → Startup). Turn it on there.",
                Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => "Your organisation's policy doesn't allow MaskedMail to start with Windows.",
                _ => "Windows didn't allow MaskedMail to start with Windows (" + state + ").",
            };
        }
        catch (Exception ex) { return "The start-up task couldn't be changed: " + ex.Message; }
    }
}
