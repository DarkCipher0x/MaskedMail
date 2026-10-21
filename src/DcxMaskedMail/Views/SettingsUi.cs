using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace DcxMail.Views;

/// <summary>The small building blocks Settings pages are made of (labels, hints, fields, test and link rows), shared by
/// the Settings window and the service cards.</summary>
internal static class SettingsUi
{
    public static PasswordBox Pw() => new() { Margin = new Thickness(0, 0, 0, 10) };
    public static TextBox Field() => new() { Style = (Style)Application.Current.Resources["Field"] };
    // The check box template lays its content out horizontally, so the text needs a width to wrap within the card.
    public static CheckBox Check(string t) => new() { Content = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, MaxWidth = 720 }, Margin = new Thickness(0, 4, 0, 6) };
    public static TextBlock Label(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"] };
    public static TextBlock Hint(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 0, 0, 10) };

    /// <summary>A highlighted note for something security- or privacy-relevant the user should read first.</summary>
    public static Border Caution(string t)
    {
        var text = new TextBlock { Text = "⚠ " + t, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 10), Child = text };
        b.SetResourceReference(Border.BorderBrushProperty, "Warn");
        return b;
    }

    public static ComboBox Choice(IEnumerable<string> items, string selected)
    {
        var c = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var i in items) c.Items.Add(new ComboBoxItem { Content = i.Length == 0 ? "(default)" : i, Tag = i });
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? (c.Items.Count > 0 ? c.Items[0] : null);
        return c;
    }

    public static UIElement TestRow(Func<Task<string>> test)
    {
        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var btn = new Button { Content = "Test connection", Style = (Style)Application.Current.Resources["SmallButton"] };
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false; result.Text = "testing…";
            try { result.Text = await test(); }
            catch (Exception ex) { result.Text = "✗ " + ex.Message; }
            btn.IsEnabled = true;
        };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(btn, Dock.Left);
        row.Children.Add(btn); row.Children.Add(result);
        return row;
    }

    public static UIElement LinkRow(string text, string url)
    {
        var link = new TextBlock { Text = "↗ " + text, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 6, 0, 0) };
        link.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        link.SetResourceReference(TextBlock.FontSizeProperty, "FsMeta");
        // Only web links from the app's own service list are ever opened here.
        link.MouseLeftButtonUp += (_, _) => { if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); };
        return link;
    }
}
