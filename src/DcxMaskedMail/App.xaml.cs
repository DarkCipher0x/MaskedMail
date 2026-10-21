using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DcxMail.Addons;
using DcxMail.Services;

namespace DcxMail;

public partial class App : Application
{
    public const string ProductName = AppInfo.ProductName;
    public static string Version => AppInfo.Version;
    /// <summary>Local, non-roaming cache (WebView2 profile, installed add-on files).</summary>
    public static string CacheDir => AppInfo.CacheDir;

    /// <summary>Matches the Start menu shortcut so the taskbar groups the window with it.</summary>
    public const string AppUserModelId = "darkcipher0x.DarkCipherMaskedMail";
    private const string MutexName = @"Local\DarkCipher.MaskedMail.Single";
    private const string ShowEventName = @"Local\DarkCipher.MaskedMail.Show";
    private const string ExitEventName = @"Local\DarkCipher.MaskedMail.Exit";

    /// <summary>Set when the installer asked the app to exit: windows close without asking about unsent mail.</summary>
    public static bool ForceExit { get; set; }

    private Mutex? _single;
    private EventWaitHandle? _showSignal, _exitSignal;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProcessHardening.Apply();
        WatchForCrashes();

        // --selftest <report.txt>: UI and runtime checks (renders, capture guard, editor CSP…); shows no window.
        // The logic itself is covered by the unit tests (tests\DcxMail.Tests); build.ps1 runs both.
        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            var report = e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "dcxmail-selftest.txt");
            int code = await SelfTest.RunAsync(report);
            Shutdown(code);
            return;
        }

        // Installer hooks (no window, no store): --exit closes a running copy; --cleanup also removes data on uninstall.
        if (e.Args.Length > 0 && e.Args[0] is "--exit" or "--cleanup")
        {
            try
            {
                ExitRunningCopy();
                if (e.Args[0] == "--cleanup") Cleanup(e.Args.Skip(1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { /* best effort for the installer */ }
            Shutdown(0);
            return;
        }

        // A Store (MSIX) install takes its taskbar identity from the package manifest.
        if (!Packaging.IsPackaged) SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // One copy per Windows user: a second launch asks the first to show its window.
        _single = new Mutex(true, MutexName, out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        if (!first) { _showSignal.Set(); Shutdown(0); return; }

        ThemeManager.Apply(ThemeMode.System);   // the sign-in screen needs the palette before settings can be read
        bool open;
        try { open = SecureStore.Initialize(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            MessageBox.Show("The encrypted settings store could not be opened:\n\n" + ex.Message, ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // Master password (when it is required at start) and/or the authenticator code, before anything is decrypted.
        if (!open || SecureStore.HasTotp)
        {
            var gate = new Views.UnlockWindow(needPassword: !open, needCode: SecureStore.HasTotp);
            if (gate.ShowDialog() != true) { SecureStore.Forget(); Shutdown(0); return; }
        }
        try { AppSettings.Load(); Mail.Outbox.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            MessageBox.Show("Your encrypted settings could not be read, so MaskedMail will not start (nothing was changed):\n\n" + ex.Message, ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        ThemeAddons.Changed += () => ThemeManager.Apply(ThemeManager.Mode);
        ThemeManager.Apply(Enum.TryParse<ThemeMode>(AppSettings.Current.Theme, out var t) ? t : ThemeMode.System);
        ThemeManager.FollowSystemChanges();
        CaptureGuard.Start();
        FolderAcl.Protect(CacheDir);
        AppLock.ReconcileSandbox();
        if (AppSettings.Current.SandboxMode) WipeCache();   // left over if the last run could not remove it on exit
        try { File.Delete(Path.Combine(CacheDir, "reader.html")); } catch (IOException) { /* plain-text page written by 0.1.0–0.1.2 */ }
        AuditLog.Write("app started", "v" + Version);

        var main = new MainWindow();
        MainWindow = main;
        ConnectAddonUi(main);
        // MSI: the Run entry passes --minimized. Store: Windows' startup task starts the package with no arguments.
        bool minimized = e.Args.Contains("--minimized") || Packaging.LaunchedByStartupTask();
        if (!minimized) main.Show();
        main.StartBackground();
        AppLock.Start();

        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.Invoke(() => main.ShowFromTray());
        }) { IsBackground = true, Name = "show-signal" }.Start();
        var exit = _exitSignal;
        new Thread(() =>
        {
            if (exit.WaitOne()) Dispatcher.Invoke(() => { ForceExit = true; main.ExitApp(); });
        }) { IsBackground = true, Name = "exit-signal" }.Start();
    }

    /// <summary>Nothing fails silently: an exception on the UI thread is shown and logged (the app keeps running); one on
    /// another thread or an unobserved task is logged (encrypted, ErrorLog). Repeats within a few seconds aren't shown
    /// again, so a failing timer can't stack up dialogs.</summary>
    private void WatchForCrashes()
    {
        DateTime lastShown = DateTime.MinValue;
        string? lastText = null;
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            ErrorLog.Write("UI thread", ex.Exception);
            if (SelfTest.Running) return;   // the self-test records it as a failure itself
            var text = ex.Exception.Message;
            if (text == lastText && DateTime.Now - lastShown < TimeSpan.FromSeconds(10)) return;
            lastText = text; lastShown = DateTime.Now;
            Dialogs.Error("Something went wrong", text + "\n\nIt has been written to the encrypted error log.");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { if (ex.ExceptionObject is Exception x) ErrorLog.Write("background thread", x); };
        TaskScheduler.UnobservedTaskException += (_, ex) => { ErrorLog.Write("background task", ex.Exception); ex.SetObserved(); };
    }

    /// <summary>The add-on engine has no UI of its own: approvals, file picks, status lines and the UI thread come from here.</summary>
    private static void ConnectAddonUi(MainWindow main)
    {
        AddonHost.Approver = (a, title, detail) =>
            Dialogs.Confirm($"{a.Manifest.Name}: {title}", $"{detail}\n\nThe add-on “{a.Manifest.Name}” ({a.Manifest.Publisher}, {a.Manifest.Version}) asks to do this. Allow it?", "Allow", danger: true);
        AddonHost.FilePrompt = (a, title) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"“{a.Manifest.Name}” asks for a file" + (title.Length > 0 ? ": " + title : "") + " — only the file you pick is given to it" };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        };
        AddonHost.Notify = text => main.Dispatcher.BeginInvoke(() => main.ShowStatus(text));
        AddonHost.Dispatch = call => main.Dispatcher.InvokeAsync(call).Task.Unwrap();
    }

    /// <summary>Asks a running copy (possibly only in the notification area) to exit, and waits up to 10 s for it.</summary>
    private static void ExitRunningCopy()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exit)) return;
        using (exit) exit.Set();
        if (!Mutex.TryOpenExisting(MutexName, out var m)) return;
        using (m)
        {
            try { if (m.WaitOne(TimeSpan.FromSeconds(10))) m.ReleaseMutex(); }
            catch (AbandonedMutexException) { /* the other copy ended without releasing: it is gone, which is what we wanted */ }
        }
    }

    /// <summary>Uninstall: always removes the start-with-Windows entry; data=1 deletes the encrypted settings, cache=1 the
    /// WebView2 profile and add-on files. Uses the real folders, never DCX_DATA_DIR.</summary>
    private static void Cleanup(IEnumerable<string> args)
    {
        var opts = args.Select(a => a.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim() == "1");
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("DarkCipherMaskedMail", throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { /* nothing to remove */ }
        if (opts.GetValueOrDefault("data"))
        {
            Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkCipher", "MaskedMail"));
            // Add-ons' own writable folders (Low integrity, under LocalLow).
            Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "DarkCipher", "MaskedMail"));
        }
        if (opts.GetValueOrDefault("cache"))
            Delete(CacheDir);
    }

    /// <summary>Sandbox mode: removes the message viewer's browser profile (HTTP cache of loaded remote images, etc.).</summary>
    public static void WipeCache() => Delete(Path.Combine(CacheDir, "WebView2"));

    private static void Delete(string dir)
    {
        for (int i = 0; i < 5 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }   // WebView2 may still be releasing its profile
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_single != null)
        {
            try { _single.ReleaseMutex(); } catch (ApplicationException) { /* not owned: nothing to release */ }   // lets an installer waiting in --exit go on at once
            _single.Dispose();
        }
        // The show/exit signals are not disposed: their background threads are still waiting on them, and Windows closes
        // the handles when the process ends.
        if (!SelfTest.Running && SecureStore.IsUnlocked && AppSettings.Current.SandboxMode) WipeCache();
        SecureStore.Forget();
        base.OnExit(e);
    }
}
