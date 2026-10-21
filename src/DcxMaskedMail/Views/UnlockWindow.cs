using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DcxMail.Services;

namespace DcxMail.Views;

/// <summary>
/// Sign-in gate (same as DarkCipher ServerManagement). At start-up it opens the store (master password when there is no
/// Windows copy of the key) and asks for the authenticator code when 2FA is on. As the lock screen it re-checks the
/// same factors while mail keeps being filed in the background.
/// </summary>
public sealed class UnlockWindow : Window
{
    private int _failures;
    private readonly bool _lockScreen;

    public bool QuitRequested { get; private set; }

    /// <param name="needPassword">Open the store with the master password (start-up) / verify it (lock screen).</param>
    /// <param name="needCode">Ask for the 6-digit authenticator code or a recovery code.</param>
    public UnlockWindow(bool needPassword = true, bool needCode = false, bool lockScreen = false)
    {
        if (SelfTest.Running && !SelfTest.RenderingWindows) throw new InvalidOperationException("Sign-in window during self-test");
        _lockScreen = lockScreen;
        Title = lockScreen ? "Locked — " + App.ProductName : App.ProductName;
        Width = 440;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = lockScreen;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "FsControl");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        CaptureGuard.Always(this);   // never visible in screenshots, recordings or streams

        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        root.Children.Add(new Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 0, 12), Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")) });
        root.Children.Add(new TextBlock { Text = lockScreen ? "Locked" : "Sign in", Style = (Style)FindResource("H1"), HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(new TextBlock
        {
            Text = lockScreen ? "MaskedMail is locked. Mail for your masked addresses keeps being filed in the background." :
                "Your API keys and settings are encrypted (AES-256-GCM)." + (needPassword ? " Enter the master password to open them." : ""),
            Style = (Style)FindResource("Hint"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 14), TextWrapping = TextWrapping.Wrap,
        });
        TextBlock Lbl(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 6, 0, 4) };
        var pb = new PasswordBox();
        var code = new TextBox { Padding = new Thickness(8, 4, 8, 4), MaxLength = 16, HorizontalContentAlignment = HorizontalAlignment.Center };
        code.SetResourceReference(FontFamilyProperty, "MonoFont");
        code.SetResourceReference(FontSizeProperty, "FsCode");
        if (needPassword) { root.Children.Add(Lbl("MASTER PASSWORD")); root.Children.Add(pb); }
        if (needCode) { root.Children.Add(Lbl("AUTHENTICATOR CODE (or a recovery code)")); root.Children.Add(code); }
        var err = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        err.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        root.Children.Add(err);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = lockScreen ? "Unlock" : "Sign in", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, MinWidth = 100 };
        var quit = new Button { Content = lockScreen ? "Exit app" : "Quit", IsCancel = !lockScreen, Margin = new Thickness(0) };
        buttons.Children.Add(ok); buttons.Children.Add(quit);
        root.Children.Add(buttons);
        Content = root;

        quit.Click += (_, _) => { QuitRequested = true; DialogResult = false; };
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            // Wrong attempts are counted in the key ring, so the wait survives restarting the app.
            for (var wait = SecureStore.RetryWait(); wait > TimeSpan.Zero; wait = SecureStore.RetryWait())
            {
                err.Text = $"Too many wrong attempts — wait {Math.Ceiling(wait.TotalSeconds):0} s.";
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(100, wait.TotalMilliseconds))));
            }
            err.Text = "Checking…";
            var pw = pb.Password; var c = code.Text;
            bool good = true;
            if (needPassword)
                good = await Task.Run(() => lockScreen ? SecureStore.VerifyPassword(pw) : SecureStore.Unlock(pw));   // PBKDF2 600k: off the UI thread
            if (good && needCode) good = SecureStore.VerifySecondFactor(c);
            if (good)
            {
                SecureStore.RecordSuccess();
                AuditLog.Write(lockScreen ? "unlocked" : "signed in", needPassword && needCode ? "password + 2FA" : needCode ? "2FA" : "password");
                DialogResult = true;
                return;
            }
            _failures++;
            SecureStore.RecordFailure();
            err.Text = (needPassword && needCode ? "Wrong password or code." : needCode ? "Wrong or already-used code." : "Wrong password.") +
                       (SecureStore.FailedAttempts >= 3 ? $" ({SecureStore.FailedAttempts} wrong attempts in a row: each new try waits longer.)" : "");
            AuditLog.Write("failed sign-in attempt", $"#{SecureStore.FailedAttempts}");
            await Task.Delay(Math.Min(15000, 500 * _failures * _failures));   // slow down guessing in this window too
            ok.IsEnabled = true;
            code.Clear();
            if (needPassword) { pb.SelectAll(); pb.Focus(); } else code.Focus();
        };
        Loaded += (_, _) => { if (needPassword) pb.Focus(); else code.Focus(); };
        Closing += (_, e) => { if (_lockScreen && DialogResult == null && !QuitRequested) e.Cancel = true; };   // the lock screen can't just be closed
    }
}
