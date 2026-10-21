using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxMail.Addons;
using DcxMail.Services;

namespace DcxMail.Views;

/// <summary>
/// Add-ons, as in DarkCipher ServerManagement: install a .dcxaddon after an approval screen (publisher, version, SHA-256,
/// signature, every permission with its risk; only ticked permissions are granted), then enable/disable, grant or revoke
/// single permissions, run commands, read the run log, switch themes, and remove.
/// </summary>
public sealed class AddonsWindow : Window
{
    private readonly ListBox _list = new() { MinWidth = 260, BorderThickness = new Thickness(0) };
    private readonly StackPanel _detail = new() { Margin = new Thickness(20, 16, 20, 16) };
    private readonly Func<AddonContext> _context;

    public AddonsWindow(Func<AddonContext> context)
    {
        _context = context;
        Title = "Add-ons — DarkCipher MaskedMail";
        Width = 1000; Height = 700; MinWidth = 760; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "FsBase");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var left = new DockPanel { Margin = new Thickness(14, 16, 8, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Add-ons", Style = (Style)Application.Current.Resources["H1"], Margin = new Thickness(4, 0, 0, 8) });
        var install = new Button { Content = "＋ Install add-on…", Style = (Style)Application.Current.Resources["PrimaryButton"], HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
        install.Click += (_, _) => InstallFromFile();
        head.Children.Add(install);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var note = Hint("Add-ons run as separate programs at Low integrity in a sandbox. Everything between them and the app is encrypted (AES-256-GCM, keys made fresh for each run); themes are encrypted on disk. Install only add-ons you trust.");
        note.Margin = new Thickness(4, 10, 4, 0);
        DockPanel.SetDock(note, Dock.Bottom);
        left.Children.Add(note);
        _list.Background = Brushes.Transparent;
        left.Children.Add(_list);
        var leftBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = left, Width = 300 };
        leftBorder.SetResourceReference(Border.BackgroundProperty, "Surface");
        leftBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(leftBorder);
        var scroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;

        _list.SelectionChanged += (_, _) => ShowDetail();
        AddonPackages.Changed += OnChanged;
        Closed += (_, _) => AddonPackages.Changed -= OnChanged;
        Refresh();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var keep = (_list.SelectedItem as ListBoxItem)?.Tag as string;
        _list.Items.Clear();
        foreach (var a in AddonPackages.All)
        {
            var row = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
            row.Children.Add(new TextBlock { Text = (a.Kind == "theme" ? "🎨  " : "◇  ") + a.Manifest.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var sub = new TextBlock { Text = $"{a.Manifest.Version} · {a.StateText}", TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, a.Enabled ? "Muted" : "Bad");
            sub.SetResourceReference(TextBlock.FontSizeProperty, "FsMeta");
            row.Children.Add(sub);
            _list.Items.Add(new ListBoxItem { Content = row, Tag = a.Id });
        }
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == keep) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        ShowDetail();
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not string id || AddonPackages.Find(id) is not { } a)
        {
            _detail.Children.Add(new TextBlock { Text = "No add-on selected", Style = (Style)Application.Current.Resources["H1"] });
            _detail.Children.Add(Hint("Install a .dcxaddon package to extend MaskedMail: reports about your masked addresses, bulk tasks, integrations with other tools, or a colour theme. " +
                                      "Add-ons are written in PowerShell or as a program using the SDK (docs/ADDONS.md, samples/addons)."));
            return;
        }
        var m = a.Manifest;
        _detail.Children.Add(new TextBlock { Text = m.Name, Style = (Style)Application.Current.Resources["H1"] });
        _detail.Children.Add(Hint($"{m.Version} · {m.Publisher} · {(a.Kind == "theme" ? "theme (no program)" : "program")} · {a.Signature} · installed {a.Installed:d MMM yyyy}" + (a.Updated > a.Installed.AddMinutes(1) ? $", updated {a.Updated:d MMM yyyy}" : "")));
        if (m.Description.Length > 0) _detail.Children.Add(new TextBlock { Text = m.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        if (!a.Enabled && a.DisabledReason != null)
        {
            var warn = new TextBlock { Text = "⚠ Disabled: " + a.DisabledReason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            _detail.Children.Add(warn);
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        void Act(string text, Action act, string? style = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4) };
            if (style != null) b.Style = (Style)Application.Current.Resources[style];
            b.Click += (_, _) => act();
            actions.Children.Add(b);
        }
        Act(a.Enabled ? "Disable" : "Enable", () => AddonPackages.SetEnabled(a, !a.Enabled));
        if (a.Kind == "theme" && a.Enabled)
        {
            if (AppSettings.Current.ThemeAddon == a.Id) Act("Back to the default theme", () => { AppSettings.Current.ThemeAddon = null; AppSettings.Save(); ThemeAddons.Reapply(); Refresh(); });
            else Act("Use this theme", () => { AppSettings.Current.ThemeAddon = a.Id; AppSettings.Save(); ThemeAddons.Reapply(); Refresh(); });
        }
        Act("Check files", () =>
        {
            var problem = AddonPackages.VerifyFiles(a);
            if (problem == null) Dialogs.Info("Check files", "Every file matches what you approved at install (SHA-256)" + (a.Sealed ? " and is encrypted on disk." : "."));
            else { AddonPackages.SetEnabled(a, false, "files changed after install: " + problem); Dialogs.Error("Check files", $"{problem}. The add-on was disabled; reinstall it from a trusted package."); }
        });
        Act("🗑 Remove", () =>
        {
            if (!Dialogs.Confirm("Remove add-on", $"Remove {a.Title}? Its files and its data folder are deleted.", "Remove", danger: true)) return;
            AddonPackages.Remove(a);
        }, "DangerButton");
        _detail.Children.Add(actions);

        if (m.Commands.Count > 0)
        {
            _detail.Children.Add(Label("COMMANDS"));
            var cmds = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var c in m.Commands)
            {
                var b = new Button { Content = "▶ " + c.Title, Margin = new Thickness(0, 0, 6, 4), ToolTip = c.Context switch { "alias" => "Runs for the masked address selected in the main window", "message" => "Runs for the message open in the main window", _ => "Runs on its own" }, IsEnabled = a.Enabled };
                b.Click += async (_, _) => await RunCommandAsync(a, c);
                cmds.Children.Add(b);
            }
            _detail.Children.Add(cmds);
        }

        if (m.Permissions.Count > 0)
        {
            _detail.Children.Add(Label("PERMISSIONS (untick to revoke)"));
            foreach (var perm in m.Permissions)
            {
                var info = AddonPackages.Permission(perm);
                var cb = new CheckBox
                {
                    IsChecked = a.Granted.Contains(perm), Margin = new Thickness(0, 2, 0, 4),
                    Content = new TextBlock { Text = $"{info?.Text ?? perm}   [{perm} · risk {info?.Risk ?? "?"}]", TextWrapping = TextWrapping.Wrap, MaxWidth = 620 },
                };
                cb.Click += (_, _) => AddonPackages.SetPermission(a, perm, cb.IsChecked == true);
                _detail.Children.Add(cb);
            }
            _detail.Children.Add(new Border { Height = 10 });
        }

        _detail.Children.Add(Label("RUN LOG"));
        var logBox = new TextBox
        {
            IsReadOnly = true, Height = 160, TextWrapping = TextWrapping.NoWrap, Padding = new Thickness(6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = a.RunLog.Count == 0 ? "(not run yet)" : string.Join("\n", a.RunLog.AsEnumerable().Reverse()),
        };
        logBox.SetResourceReference(FontFamilyProperty, "MonoFont");
        logBox.SetResourceReference(FontSizeProperty, "FsMeta");
        _detail.Children.Add(logBox);
        _detail.Children.Add(Hint($"Files: {a.Files.Count} · package SHA-256 {a.PackageSha256[..Math.Min(16, a.PackageSha256.Length)]}… · data folder (the only place it can write): {AddonPackages.DataFolder(a.Id)}"));
    }

    private async Task RunCommandAsync(InstalledAddon a, AddonCommand c)
    {
        var ctx = _context();
        if (c.Context == "alias" && ctx.Alias == null) { Dialogs.Info(c.Title, "Select a masked address in the main window first."); return; }
        if (c.Context == "message" && ctx.Message == null) { Dialogs.Info(c.Title, "Open a message in the main window first."); return; }
        Cursor = System.Windows.Input.Cursors.Wait;
        try { await ShowRunResultAsync(a, c, ctx); }
        finally { Cursor = null; Refresh(); }
    }

    /// <summary>Runs a command and shows its result and log (also used by the main window's Add-ons menu).</summary>
    public static async Task ShowRunResultAsync(InstalledAddon a, AddonCommand c, AddonContext ctx)
    {
        var r = await AddonHost.RunAsync(a, c.Id, ctx);
        if (!r.Show) return;
        var text = r.Text + (r.Log.Count > 0 ? "\n\n— log —\n" + string.Join("\n", r.Log.Take(40)) : "");
        if (r.Ok) Dialogs.Info($"{a.Manifest.Name}: {c.Title}", text);
        else Dialogs.Error($"{a.Manifest.Name}: {c.Title} ({r.Status})", text);
    }

    // ---- install + approval ----

    private void InstallFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "DarkCipher add-on|*.dcxaddon|All files|*.*", Title = "Install add-on" };
        if (dlg.ShowDialog(this) != true) return;
        var info = AddonPackages.Inspect(dlg.FileName);
        if (info.Refusal != null) { Dialogs.Error("Add-on refused", info.Refusal); return; }
        var grant = Approve(info);
        if (grant == null) return;
        try
        {
            var a = AddonPackages.Install(info, grant);
            if (a.Kind == "theme" && Dialogs.Confirm("Theme installed", $"Use “{a.Manifest.Name}” now?", "Use it"))
            { AppSettings.Current.ThemeAddon = a.Id; AppSettings.Save(); ThemeAddons.Reapply(); }
            Refresh();
            _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == a.Id);
        }
        catch (Exception ex) { Dialogs.Error("Install failed", ex.Message); }
    }

    /// <summary>The approval screen: returns the permissions the user ticked, or null when cancelled.</summary>
    private List<string>? Approve(PackageInfo info)
    {
        var m = info.Manifest;
        var w = Dialogs.MakeWindow(info.Existing == null ? "Install add-on" : "Update add-on", 600);
        var p = new StackPanel { Margin = new Thickness(20) };
        p.Children.Add(new TextBlock { Text = m.Name, Style = (Style)Application.Current.Resources["H1"] });
        p.Children.Add(Hint($"{m.Publisher} · version {m.Version}" + (info.Existing != null ? $" (installed: {info.Existing.Manifest.Version})" : "")));
        if (m.Description.Length > 0) p.Children.Add(new TextBlock { Text = m.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        var facts = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Text = $"Package SHA-256: {info.Sha256}\nFiles: {info.FileCount} · {info.Bytes / 1024.0:0} KB\nProgram: {info.Signature}" };
        facts.SetResourceReference(FontFamilyProperty, "MonoFont");
        facts.SetResourceReference(FontSizeProperty, "FsMeta");
        p.Children.Add(facts);
        var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        warn.SetResourceReference(TextBlock.ForegroundProperty, m.Entry.Length > 0 ? "Warn" : "Muted");
        warn.Text = m.Entry.Length > 0
            ? "An add-on is a program. It runs sandboxed (Low integrity, its own data folder, memory and process limits, no access to the app's keys) and talks to the app only over an encrypted bridge. " +
              "Low integrity can still READ many files and use the network: the permissions below control what the APP gives it, not everything a program can do. Install only add-ons you trust."
            : "This is a theme: colours only, no program runs. Its files are encrypted on disk and checked every time it is used.";
        p.Children.Add(warn);
        var boxes = new List<(CheckBox Box, string Perm)>();
        if (m.Permissions.Count > 0)
        {
            p.Children.Add(Label("PERMISSIONS — only ticked ones are granted"));
            foreach (var perm in m.Permissions)
            {
                var pi = AddonPackages.Permission(perm)!;
                bool isNew = info.NewPermissions.Contains(perm);
                bool already = info.Existing?.Granted.Contains(perm) == true;
                var cb = new CheckBox
                {
                    IsChecked = already || pi.Risk != "HIGH", IsEnabled = info.Existing == null || isNew, Margin = new Thickness(0, 2, 0, 4),
                    Content = new TextBlock { Text = (isNew && info.Existing != null ? "NEW · " : "") + $"{pi.Text}   [{perm} · risk {pi.Risk}]", TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
                };
                if (pi.Risk == "HIGH") cb.SetResourceReference(ForegroundProperty, "Warn");
                boxes.Add((cb, perm));
                p.Children.Add(cb);
            }
            if (info.Existing != null) p.Children.Add(Hint("Permissions you granted before are kept; new ones start unticked unless you tick them."));
        }
        var ok = new Button { Content = info.Existing == null ? "Install" : "Update", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        ok.Click += (_, _) => w.DialogResult = true;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(ok); row.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) });
        p.Children.Add(row);
        w.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 720 };
        if (w.ShowDialog() != true) return null;
        return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Perm).ToList();
    }

    private static TextBlock Label(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 6, 0, 4) };
    private static TextBlock Hint(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 0, 0, 8) };
}
