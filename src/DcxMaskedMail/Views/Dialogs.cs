using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcxMail.Services;

namespace DcxMail;

/// <summary>Themed message, confirm and prompt boxes. They refuse to open during the self-test (no window ever shows).</summary>
public static class Dialogs
{
    public static void Info(string title, string message) => Show(title, message, "OK", null, false);
    public static void Error(string title, string message) => Show(title, message, "OK", null, true);

    public static bool Confirm(string title, string message, string ok = "OK", bool danger = false) =>
        Show(title, message, ok, "Cancel", danger) == true;

    public static string? Prompt(string title, string label, string initial = "", bool password = false)
    {
        if (SelfTest.Running) throw new InvalidOperationException("Dialog during self-test: " + title);
        if (password)
        {
            var pb = new PasswordBox { MinWidth = 380, Margin = new Thickness(0, 0, 0, 10) };
            var pw = Build(title, label, "OK", "Cancel", false, pb, out _);
            CaptureGuard.Always(pw);
            pb.Loaded += (_, _) => pb.Focus();
            return pw.ShowDialog() == true ? pb.Password : null;
        }
        var box = new TextBox { Text = initial, Style = (Style)Application.Current.Resources["Field"], MinWidth = 380 };
        var w = Build(title, label, "OK", "Cancel", false, box, out var okBtn);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { w.DialogResult = true; } };
        return w.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>A themed empty dialog window for custom content (2FA set-up, recovery codes, password).</summary>
    public static Window MakeWindow(string title, double width)
    {
        if (SelfTest.Running) throw new InvalidOperationException("Dialog during self-test: " + title);
        var w = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Bg");
        w.SetResourceReference(Window.ForegroundProperty, "Fg");
        w.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        w.SetResourceReference(Window.FontSizeProperty, "FsBase");
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
        if (owner != null && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(w);
        CaptureGuard.Always(w);   // these hold QR codes, keys, recovery codes and passwords
        return w;
    }

    private static bool? Show(string title, string message, string ok, string? cancel, bool danger)
    {
        if (SelfTest.Running) throw new InvalidOperationException("Dialog during self-test: " + title + " — " + message);
        return Build(title, message, ok, cancel, danger, null, out _).ShowDialog();
    }

    private static Window Build(string title, string message, string ok, string? cancel, bool danger, FrameworkElement? extra, out Button okBtn)
    {
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MaxWidth = 620,
            Background = (Brush)Application.Current.Resources["Bg"],
            Foreground = (Brush)Application.Current.Resources["Fg"],
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            FontSize = (double)Application.Current.Resources["FsBase"],
        };
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current.MainWindow;
        if (owner != null && owner != w && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(w);

        var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        stack.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H2"], Foreground = danger ? (Brush)Application.Current.Resources["Bad"] : (Brush)Application.Current.Resources["Accent"] });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 4, 0, 14) });
        if (extra != null) stack.Children.Add(extra);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okB = new Button { Content = ok, IsDefault = true, Style = (Style)Application.Current.Resources[danger && cancel != null ? "DangerButton" : "PrimaryButton"] };
        okB.Click += (_, _) => w.DialogResult = true;
        buttons.Children.Add(okB);
        if (cancel != null) buttons.Children.Add(new Button { Content = cancel, IsCancel = true, Margin = new Thickness(0) });
        stack.Children.Add(buttons);
        w.Content = stack;
        okBtn = okB;
        return w;
    }
}
