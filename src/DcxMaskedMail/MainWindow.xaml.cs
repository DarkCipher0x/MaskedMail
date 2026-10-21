using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;
using DcxMail.Views;
using Microsoft.Web.WebView2.Core;

namespace DcxMail;

public partial class MainWindow : Window
{
    private ProviderRegistry? _reg;
    private readonly DispatcherTimer _sweepTimer = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exiting, _readerReady, _internalNav, _sweeping;

    private IMailSource? _source;          // what the middle column shows: a Fastmail folder or a disposable inbox
    private int _position, _total;
    private Alias? _alias;                // selected masked address
    private MessageDetail? _message;      // message in the reader
    private bool _remoteImages;
    private string? _readerPage;
    private const string ReaderUrl = "https://message.dcxmail.invalid/";
    private DateTime? _lastSweep;
    private int _movedTotal;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + App.Version;
        ThemeBox.SelectedIndex = AppSettings.Current.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };
        PreviewKeyDown += OnKey;
        _sweepTimer.Tick += async (_, _) => await SweepAsync(quiet: true);
        Loaded += (_, _) => { RefreshLockButton(); RefreshQueueButton(); };
        StartThrowawayTimers();
        Outbox.Changed += () => Dispatcher.BeginInvoke(RefreshQueueButton);
        Addons.AddonHost.Registry = () => _reg;
        ThemeManager.Changed += () => { if (_message != null) ShowMessageBody(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    // ---------- start-up, tray, closing ----------

    public void StartBackground()
    {
        CreateTray();
        _ = InitReaderAsync();
        _ = ReloadAccountsAsync();
    }

    private void CreateTray()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open MaskedMail", null, (_, _) => ShowFromTray());
        menu.Items.Add("New masked email…", null, (_, _) => { if (ShowFromTray()) OnNewAlias(this, new RoutedEventArgs()); });
        menu.Items.Add("New throwaway address", null, async (_, _) => { if (ShowFromTray()) await NewThrowawayAsync(TimeSpan.FromMinutes(AppSettings.Current.ThrowawayMinutes)); });
        menu.Items.Add("Sweep now", null, async (_, _) => await SweepAsync(quiet: false));
        menu.Items.Add("Outgoing queue…", null, (_, _) => { if (ShowFromTray()) OnQueueWindow(this, new RoutedEventArgs()); });
        menu.Items.Add("Lock now", null, (_, _) => LockNow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "DarkCipher MaskedMail",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    /// <summary>Shows the window; asks for sign-in first when the lock was armed while the app sat in the tray.</summary>
    public bool ShowFromTray()
    {
        if (!AppLock.EnsureUnlocked()) return false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return true;
    }

    /// <summary>Locks now; with nothing to unlock with yet, opens Settings → Security &amp; privacy to set it up.</summary>
    public void LockNow()
    {
        if (AppLock.Lock()) return;
        if (Dialogs.Confirm("Set up the app lock", "The lock screen needs a master password or two-factor sign-in to unlock with. Set one up now in Settings → Security & privacy?", "Open Security"))
            _ = OpenSettingsAsync("Security & privacy");
    }

    private void OnLock(object sender, RoutedEventArgs e) => LockNow();

    public void ShowStatus(string text) => Status(text);

    // ---------- add-ons ----------

    private AddonsWindow? _addons;
    private Addons.AddonContext AddonContextNow() => new(CurrentAlias(), _message);

    public void OpenAddons()
    {
        if (_addons is { IsLoaded: true }) { _addons.Activate(); return; }
        _addons = new AddonsWindow(AddonContextNow) { Owner = this };
        _addons.Closed += (_, _) => _addons = null;
        _addons.Show();
    }

    /// <summary>The header menu: every command of every enabled program add-on, then "Manage add-ons…".</summary>
    private void OnAddonsMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddonsBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var ctx = AddonContextNow();
        foreach (var a in Addons.AddonPackages.All.Where(a => a.Enabled && a.Manifest.Commands.Count > 0))
            foreach (var c in a.Manifest.Commands)
            {
                bool usable = c.Context switch { "alias" => ctx.Alias != null, "message" => ctx.Message != null, _ => true };
                var item = new MenuItem
                {
                    Header = $"{a.Manifest.Name} › {c.Title}" + c.Context switch { "alias" => "  (selected address)", "message" => "  (open message)", _ => "" },
                    IsEnabled = usable,
                };
                var (addon, cmd) = (a, c);
                item.Click += async (_, _) => { Status($"running {addon.Manifest.Name} › {cmd.Title}…"); await AddonsWindow.ShowRunResultAsync(addon, cmd, AddonContextNow()); Status("ready"); };
                menu.Items.Add(item);
            }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage add-ons…" };
        manage.Click += (_, _) => OpenAddons();
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    private OutboxWindow? _outbox;

    private void OnQueueWindow(object sender, RoutedEventArgs e)
    {
        if (_reg == null) return;
        if (_outbox is { IsLoaded: true }) { _outbox.Activate(); return; }
        _outbox = new OutboxWindow(_reg) { Owner = this };
        _outbox.StatusChanged += Status;
        _outbox.Closed += (_, _) => _outbox = null;
        _outbox.Show();
    }

    private void RefreshQueueButton()
    {
        var n = Outbox.Items.Count;
        QueueBtn.Content = n > 0 ? $"📤  Queue ({n})" : "📤  Queue";
    }
    private async void OnSecurityBadge(object sender, RoutedEventArgs e) => await OpenSettingsAsync("Security & privacy");

    public void RefreshLockButton()
    {
        var s = AppSettings.Current;
        LockBtn.ToolTip = AppLock.CanLock
            ? "Lock now (Ctrl+Shift+L)" + (s.AutoLockMinutes > 0 ? $" · auto-lock after {s.AutoLockMinutes} min" : "") + (s.SandboxMode ? " · sandbox mode" : "")
            : "App lock is not set up yet: click to add a master password or 2FA";
        LockBtn.Opacity = AppLock.CanLock ? 1 : 0.6;
        SecurityBadge.Content = (SecureStore.PasswordRequiredOnStart ? "🔒 encrypted · password at start" : SecureStore.HasPassword ? "🔒 encrypted · password" : "🔒 encrypted")
                                + (SecureStore.HasTotp ? " · 2FA" : "") + (s.AutoLockMinutes > 0 ? $" · lock {s.AutoLockMinutes}m" : "") + (s.SandboxMode ? " · sandbox" : "");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (SelfTest.Running || _exiting) return;   // off-screen render, or already exiting: just close
        if (!_exiting && AppSettings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(2500, "MaskedMail is still running", "Mail for your masked addresses keeps being filed. Right-click the tray icon to exit.", System.Windows.Forms.ToolTipIcon.None);
            return;
        }
        ExitApp();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _sweepTimer.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w != this).ToList()) w.Close();
        _reg?.Dispose();
        Application.Current.Shutdown();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.F5) { _ = ReloadAliasesAsync(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.L) { LockNow(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.Q) { OnQueueWindow(this, e); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.T) { _ = NewThrowawayAsync(TimeSpan.FromMinutes(AppSettings.Current.ThrowawayMinutes)); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.N) { OnNewAlias(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.N) { OnCompose(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.R && _message != null) { OnReply(this, e); e.Handled = true; }
        else if (e.Key == Key.Delete && MessageList.IsKeyboardFocusWithin && _message != null) { OnDeleteMessage(this, e); e.Handled = true; }
    }

    private void Status(string text) => StatusText.Text = text;

    // ---------- accounts and aliases ----------

    /// <summary>Rebuilds the providers from settings (start-up and after Settings is saved).</summary>
    public async Task ReloadAccountsAsync(ProviderRegistry? injected = null)
    {
        _reg?.Dispose();
        _reg = injected ?? new ProviderRegistry(AppSettings.Current);
        ProviderFilter.SelectionChanged -= OnFilter;
        ProviderFilter.Items.Clear();
        ProviderFilter.Items.Add(new ComboBoxItem { Content = "All services", Tag = "" });
        foreach (var p in _reg.Providers) ProviderFilter.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Key });
        ProviderFilter.SelectedIndex = 0;
        ProviderFilter.SelectionChanged += OnFilter;

        UpdateWelcome();
        _sweepTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(AppSettings.Current.SweepMinutes, 1, 120));
        if (_reg.Mail != null && AppSettings.Current.RoutingEnabled && !SelfTest.Running) _sweepTimer.Start(); else _sweepTimer.Stop();
        UpdateRouteText();

        await ReloadAliasesAsync();
        if (_reg.Mail != null)
        {
            NavInbox.IsChecked = false;
            NavInbox.IsChecked = true;
            await SweepAsync(quiet: true);
        }
    }

    private void UpdateWelcome()
    {
        bool any = _reg?.Providers.Count > 0;
        bool mail = _reg?.Mail != null;
        Welcome.Visibility = _message == null ? Visibility.Visible : Visibility.Collapsed;
        WelcomeBtn.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        WelcomeText.Text = !any
            ? "Welcome. Add your Fastmail API token in Settings to manage masked email, read and send mail, and file each address's mail into its own folder. SimpleLogin, addy.io and Firefox Relay keys can be added too."
            : mail ? "Pick a masked address or a message." : "Masked addresses are loaded. Add a Fastmail API token in Settings to read, send and file mail.";
    }

    private async Task ReloadAliasesAsync()
    {
        if (_reg == null) return;
        Status("loading masked addresses…");
        await _reg.RefreshAsync();
        RefreshThrowawayMarks();
        ApplyFilter();
        var errors = _reg.Errors.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        Status(errors.Count > 0 ? "⚠ " + string.Join(" · ", errors) : $"{_reg.Aliases.Count} masked addresses from {_reg.Providers.Count} service(s)");
        // Throwaways that ran out while the app was closed end now.
        if (errors.Count == 0 && !SelfTest.Running) await EnforceThrowawaysAsync();
    }

    private void ApplyFilter()
    {
        if (_reg == null) return;
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var provider = (ProviderFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var state = StateFilter.SelectedIndex;
        var keep = _alias;
        AliasList.ItemsSource = _reg.Aliases.Where(a =>
            (provider.Length == 0 || a.ProviderKey == provider) &&
            (state == 0 || (state == 1) == a.Enabled) &&
            (q.Length == 0 || a.Email.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Site.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (keep != null) AliasList.SelectedItem = ((List<Alias>)AliasList.ItemsSource).FirstOrDefault(a => a.Id == keep.Id && a.ProviderKey == keep.ProviderKey);
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void OnFilter(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAliasesAsync();

    private async void OnAliasSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AliasList.SelectedItem is not Alias a) return;
        _alias = a;
        NavInbox.IsChecked = false; NavSent.IsChecked = false;
        ShowCard();
        if (_reg?.MailFor(a) is { } source) { await OpenSourceAsync(source); return; }
        _source = null;
        MessageList.ItemsSource = null;
        SourceNote.Visibility = Visibility.Collapsed;
        if (_reg?.Mail == null) { FolderTitle.Text = "no mailbox"; ShowFolderMessage("Add a Fastmail API token in Settings to see this address's mail."); return; }
        FolderTitle.Text = "no folder yet";
        ShowFolderMessage("This address has no folder yet. Use \"📁 Create folder\" above to create\n" +
                          AppSettings.Current.RouteParent + "/" + MailService.FolderName(a.Email) + "\nand file its mail there." +
                          (a.ForwardsTo is { Length: > 0 } f ? $"\n(Its mail goes to {f}; a folder only helps if that is this Fastmail account.)" : ""));
    }

    private void ShowCard()
    {
        if (_alias is not { } a) { AliasCard.Visibility = Visibility.Collapsed; return; }
        AliasCard.Visibility = Visibility.Visible;
        CardTitle.Text = a.Title;
        CardEmail.Text = a.Email;
        CardStateText.Text = a.State.ToUpperInvariant();
        CardState.Background = (Brush)FindResource(a.Enabled ? "Good" : "Bad");
        var parts = new List<string> { a.ProviderName };
        if (a.Site.Length > 0) parts.Add(a.Site);
        if (a.Created is { } c) parts.Add("created " + c.LocalDateTime.ToString("d MMM yyyy"));
        if (a.LastUsed is { } l) parts.Add("last mail " + l.LocalDateTime.ToString("d MMM yyyy"));
        if (a.ForwardsTo is { Length: > 0 } f) parts.Add("→ " + f);
        if (a.Stats.Length > 0) parts.Add(a.Stats);
        CardMeta.Text = string.Join("  ·  ", parts);
        CardToggle.Content = a.Enabled ? "⏻ Disable" : "⏻ Enable";
        CardRoute.Content = a.Routed ? "📁 Stop routing" : "📁 Create folder";
        CardRoute.ToolTip = a.Routed ? "Stop moving this address's mail (the folder and its mail stay)" : "Create " + AppSettings.Current.RouteParent + "/" + MailService.FolderName(a.Email) + " and file its mail there";
        // Only what this address's service can really do is offered.
        var p = _reg?.Get(a.ProviderKey);
        var info = p?.Info;
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        CardCompose.Visibility = Show(p != null && p.SendMode != SendMode.None);
        CardToggle.Visibility = Show(info?.Has(Features.Disable) == true);
        CardEdit.Visibility = Show(info?.Has(Features.EditNote) == true || info?.Has(Features.EditSite) == true);
        CardRoute.Visibility = Show(info?.Has(Features.Inbox) != true);
        CardActivity.Visibility = Show(p is IActivityProvider);
        var forgetOnly = info != null && info.Has(Features.Remembered) && !info.Has(Features.Delete);
        CardDelete.Visibility = Show(info != null && (info.Has(Features.Delete) || forgetOnly));
        CardDelete.Content = forgetOnly ? "🗑 Remove from list" : "🗑 Delete";
        ThrowawayExtendBtn.Visibility = Show(Throwaways.CanExtend(a.Email));
        UpdateThrowawayBar();
    }

    // ---------- throwaway addresses ----------

    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, ThrowawayWindow> _throwawayWindows = new(StringComparer.OrdinalIgnoreCase);
    private bool _enforcing;

    private void StartThrowawayTimers()
    {
        _expiryTimer.Tick += async (_, _) => await EnforceThrowawaysAsync();
        _countdownTimer.Tick += (_, _) => UpdateThrowawayBar();
        Throwaways.Changed += () => Dispatcher.BeginInvoke(() => { RefreshThrowawayMarks(); ApplyFilter(); ShowCard(); });
        if (!SelfTest.Running) { _expiryTimer.Start(); _countdownTimer.Start(); }
    }

    /// <summary>Ends throwaways that ran out (every 30 s, and when accounts load). Failures stay and are retried.</summary>
    private async Task EnforceThrowawaysAsync()
    {
        RefreshThrowawayMarks();
        if (_reg == null || _enforcing || AppSettings.Current.Throwaways.Values.All(t => !t.Expired)) { AliasList.Items.Refresh(); return; }
        _enforcing = true;
        try
        {
            var ended = await Throwaways.EnforceAsync(_reg);
            if (ended.Count > 0)
            {
                Status($"⏱ {ended.Count} throwaway address(es) expired and ended: {string.Join(", ", ended)}");
                if (!IsVisible) _tray?.ShowBalloonTip(2500, "Throwaway expired", $"{ended.Count} throwaway address(es) ended.", System.Windows.Forms.ToolTipIcon.None);
                if (_alias != null && ended.Contains(_alias.Email, StringComparer.OrdinalIgnoreCase)) { _alias = null; ShowCard(); }
                ApplyFilter();
            }
        }
        finally { _enforcing = false; }
    }

    private void RefreshThrowawayMarks()
    {
        if (_reg == null) return;
        foreach (var a in _reg.Aliases)
            a.ExpiryText = Throwaways.Find(a.Email) is { } t ? "⏱ " + t.LeftText : "";
    }

    private void UpdateThrowawayBar()
    {
        if (_alias is { } a && Throwaways.Find(a.Email) is { } t)
        {
            ThrowawayBar.Visibility = Visibility.Visible;
            ThrowawayText.Text = (t.Expired ? "⏱ Throwaway — expired, ending…" : $"⏱ Throwaway — expires in {t.LeftText} ({t.Expires.LocalDateTime:ddd HH:mm})")
                                 + (t.LastError != null ? "  ⚠ " + t.LastError : "");
        }
        else ThrowawayBar.Visibility = Visibility.Collapsed;
    }

    private void OnThrowawayMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ThrowawayBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var def = AppSettings.Current.ThrowawayMinutes;
        foreach (var (min, label) in Throwaways.Lifetimes)
        {
            var item = new MenuItem { Header = $"New throwaway — lasts {label}" + (min == def ? "   (default, Ctrl+Shift+T)" : "") };
            var m = min;
            item.Click += async (_, _) => await NewThrowawayAsync(TimeSpan.FromMinutes(m));
            menu.Items.Add(item);
        }
        var custom = new MenuItem { Header = "New throwaway — custom lifetime…" };
        custom.Click += async (_, _) =>
        {
            if (Dialogs.Prompt("Throwaway lifetime", "How long should it last? e.g. 30m, 2h, 3d (1 minute – 365 days)", "2h") is not { } t) return;
            if (Throwaways.ParseLifetime(t) is not { } life) { Dialogs.Error("Throwaway lifetime", "Use a number with m, h or d, e.g. 45m, 6h, 14d — between 1 minute and 365 days."); return; }
            await NewThrowawayAsync(life);
        };
        menu.Items.Add(custom);
        var active = AppSettings.Current.Throwaways.Values.OrderBy(t => t.Expires).ToList();
        if (active.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var t in active)
            {
                var item = new MenuItem { Header = $"📥 {t.Email}   ·   {(t.Expired ? "expired" : t.LeftText + " left")}" };
                var email = t.Email;
                item.Click += (_, _) => OpenThrowawayInbox(email);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    private async Task NewThrowawayAsync(TimeSpan life)
    {
        if (_reg == null || _reg.Providers.Count == 0) { Dialogs.Info("No services yet", "Add a Fastmail API token (or a SimpleLogin, addy.io or Firefox Relay key) in Settings first."); return; }
        await Run(async () =>
        {
            Status("creating a throwaway address…");
            var (alias, info) = await Throwaways.CreateAsync(_reg, life);
            RefreshThrowawayMarks();
            SearchBox.Text = "";
            ApplyFilter();
            AliasList.SelectedItem = alias;
            AliasList.ScrollIntoView(alias);
            // The inbox opens first: a busy clipboard must never stop the throwaway from being usable.
            OpenThrowawayInbox(alias.Email);
            var copied = ClipboardHelper.TrySetText(alias.Email);
            Status($"⏱ {alias.Email} {(copied ? "copied" : "created (clipboard busy — use ⧉ Copy)")} — expires {info.Expires.LocalDateTime:ddd HH:mm}" + (info.LastError != null ? " · ⚠ " + info.LastError : ""));
        });
    }

    public void OpenThrowawayInbox(string email)
    {
        if (_reg == null) return;
        if (_throwawayWindows.TryGetValue(email, out var open) && open.IsLoaded) { open.Activate(); return; }
        var w = new ThrowawayWindow(_reg, email) { Owner = this };
        w.Closed += (_, _) => _throwawayWindows.Remove(email);
        _throwawayWindows[email] = w;
        w.Show();
    }

    private void OnThrowawayInbox(object sender, RoutedEventArgs e) { if (_alias != null) OpenThrowawayInbox(_alias.Email); }
    private void OnThrowawayExtend(object sender, RoutedEventArgs e) { if (_alias != null) Throwaways.Extend(_alias.Email, TimeSpan.FromHours(1)); }

    private void OnThrowawayKeep(object sender, RoutedEventArgs e)
    {
        if (_alias is not { } a || !Dialogs.Confirm("Keep this address", $"{a.Email} will no longer expire. It stays in your masked addresses with its folder.", "Keep it")) return;
        Throwaways.Keep(a.Email);
    }

    private async void OnThrowawayBurn(object sender, RoutedEventArgs e)
    {
        if (_alias is not { } a || _reg == null) return;
        var s = AppSettings.Current;
        var what = (s.ThrowawayDelete ? "The address is deleted (mail sent to it bounces)" : "The address is disabled") +
                   (s.ThrowawayCleanMail ? ", its mail goes to Trash and its folder is removed." : ".");
        if (!Dialogs.Confirm("Burn this throwaway", $"End {a.Email} now?\n\n{what}", "Burn it", danger: true)) return;
        await Run(async () =>
        {
            var result = await Throwaways.BurnAsync(_reg, a.Email, "burned by you");
            Status($"🔥 {a.Email}: {result}");
            _alias = null; ShowCard(); ApplyFilter();
        });
    }

    private void OnNewAlias(object sender, RoutedEventArgs e)
    {
        if (_reg == null || _reg.Providers.Count == 0) { Dialogs.Info("No services yet", "Add a Fastmail API token (or a SimpleLogin, addy.io or Firefox Relay key) in Settings first."); return; }
        var w = new NewAliasWindow(_reg) { Owner = this };
        if (w.ShowDialog() != true || w.Created == null) return;
        ApplyFilter();
        SearchBox.Text = "";
        AliasList.SelectedItem = w.Created;
        AliasList.ScrollIntoView(w.Created);
        AuditLog.Write("masked address created", $"{w.Created.ProviderName}: {w.Created.Email}");
        var copied = ClipboardHelper.TrySetText(w.Created.Email);
        Status($"created {w.Created.Email} ({(copied ? "copied to the clipboard" : "clipboard busy — use ⧉ copy")})" + (w.Created.Routed ? " · folder ready" : ""));
        if (w.FolderError != null) Dialogs.Error("Folder not created", "The address was created, but its folder could not be:\n\n" + w.FolderError);
    }

    private void OnCopyAlias(object sender, RoutedEventArgs e)
    {
        if (CurrentAlias() is not { } a) return;
        Status(ClipboardHelper.Say(ClipboardHelper.TrySetText(a.Email), a.Email));
    }

    private Alias? CurrentAlias() => AliasList.SelectedItem as Alias ?? _alias;

    private async void OnToggleAlias(object sender, RoutedEventArgs e)
    {
        if (CurrentAlias() is not { } a || _reg?.Get(a.ProviderKey) is not { } p) return;
        await Run(async () =>
        {
            await p.SetEnabledAsync(a, !a.Enabled);
            AuditLog.Write(a.Enabled ? "masked address enabled" : "masked address disabled", $"{a.ProviderName}: {a.Email}");
            Status($"{a.Email} is now {(a.Enabled ? "enabled" : "disabled")}");
        });
        ApplyFilter(); ShowCard();
    }

    private async void OnEditAlias(object sender, RoutedEventArgs e)
    {
        if (CurrentAlias() is not { } a || _reg?.Get(a.ProviderKey) is not { } p) return;
        var note = Dialogs.Prompt("Edit " + a.Email, "Note / description (what you used this address for):", a.Description);
        if (note == null) return;
        var site = a.Site;
        if (p.Key is FastmailProvider.ProviderKey or FirefoxRelayProvider.ProviderKey)
        {
            var s = Dialogs.Prompt("Edit " + a.Email, "Website it is for (e.g. example.com):", a.Site);
            if (s == null) return;
            site = s;
        }
        await Run(() => p.UpdateAsync(a, note, site));
        ApplyFilter(); ShowCard();
    }

    private async void OnRouteAlias(object sender, RoutedEventArgs e)
    {
        if (CurrentAlias() is not { } a || _reg == null) return;
        if (a.Routed)
        {
            AppSettings.Current.RouteOff.Add(a.Email);
            AppSettings.Save();
            _reg.MarkRouted();
            Status($"routing off for {a.Email} — its folder and mail are kept");
        }
        else
        {
            await Run(async () =>
            {
                var id = await _reg.EnsureFolderAsync(a);
                var moved = await _reg.SweepAsync();
                Status($"folder ready for {a.Email}" + (moved > 0 ? $" · {moved} message(s) filed" : ""));
                await OpenFolderAsync(id, AppSettings.Current.RouteParent + "/" + MailService.FolderName(a.Email));
            });
        }
        ApplyFilter(); ShowCard();
    }

    private async void OnDeleteAlias(object sender, RoutedEventArgs e)
    {
        if (CurrentAlias() is not { } a || _reg?.Get(a.ProviderKey) is not { } p) return;
        var info = p.Info;
        var forgetOnly = info.Has(Features.Remembered) && !info.Has(Features.Delete);
        var what = forgetOnly ? $"It is only removed from this app's list: {p.Name} keeps forwarding mail to it until you turn it off there."
            : info.Has(Features.Inbox) ? $"The inbox is removed from this app{(info.Key == "mailtm" ? $" and deleted at {p.Name}, with its mail" : $"; {p.Name} deletes it and its mail by itself")}."
            : a.ProviderKey == FastmailProvider.ProviderKey ? "Mail sent to it will bounce. Fastmail keeps it as \"deleted\"; you can restore it from Fastmail's settings."
            : $"It is removed from {p.Name}. Mail sent to it will no longer reach you.";
        var folderNote = info.Has(Features.Inbox) ? "" : "\n\nIts folder and any mail already in it stay in Fastmail.";
        if (!Dialogs.Confirm(forgetOnly ? "Remove from list" : "Delete address", $"{(forgetOnly ? "Remove" : "Delete")} {a.Email}?\n\n{what}{folderNote}", forgetOnly ? "Remove" : "Delete", danger: true)) return;
        await Run(async () =>
        {
            await p.DeleteAsync(a);
            AuditLog.Write("masked address deleted", $"{a.ProviderName}: {a.Email}");
            _reg.Aliases.Remove(a);
            AppSettings.Current.RouteOff.Add(a.Email);
            AppSettings.Save();
            Status("deleted " + a.Email);
        });
        _alias = null; ShowCard(); ApplyFilter();
    }

    /// <summary>What the service reports about this address's recent deliveries (SimpleLogin activity, ImprovMX log).</summary>
    private async void OnAliasActivity(object sender, RoutedEventArgs e)
    {
        if (CurrentAlias() is not { } a || _reg?.Get(a.ProviderKey) is not IActivityProvider p) return;
        await Run(async () =>
        {
            var list = await p.ActivityAsync(a);
            var text = list.Count == 0 ? "Nothing reported yet."
                : string.Join("\n", list.Take(25).Select(x => $"{x.At.LocalDateTime:dd MMM HH:mm}  {x.What,-10}  {x.From}{(x.Subject.Length > 0 ? "  — " + x.Subject : "")}"));
            Dialogs.Info($"Activity · {a.Email}", text);
        });
    }

    // ---------- folders and messages ----------

    private async void OnNavInbox(object sender, RoutedEventArgs e)
    {
        if (_reg?.Mail == null || NavInbox.IsChecked != true) return;
        AliasList.SelectedItem = null; _alias = null; ShowCard();
        await Run(async () => await OpenFolderAsync(await _reg.Mail.RoleIdAsync("inbox"), "Inbox"));
    }

    private async void OnNavSent(object sender, RoutedEventArgs e)
    {
        if (_reg?.Mail == null || NavSent.IsChecked != true) return;
        AliasList.SelectedItem = null; _alias = null; ShowCard();
        await Run(async () => await OpenFolderAsync(await _reg.Mail.RoleIdAsync("sent"), "Sent"));
    }

    private Task OpenFolderAsync(string folderId, string title) =>
        _reg?.Mail is { } mail ? OpenSourceAsync(new FastmailFolder(mail, folderId, title)) : Task.CompletedTask;

    private async Task OpenSourceAsync(IMailSource source)
    {
        _source = source;
        _position = 0;
        FolderTitle.Text = source.Title;
        SourceNote.Text = source.Attribution ?? "";
        SourceNote.Visibility = source.Attribution != null ? Visibility.Visible : Visibility.Collapsed;
        // A disposable inbox can't be replied from (its service doesn't send), and its messages are deleted, not trashed.
        var canReply = source is FastmailFolder;
        ReplyBtn.Visibility = ReplyAllBtn.Visibility = ForwardBtn.Visibility = canReply ? Visibility.Visible : Visibility.Collapsed;
        MarkUnreadBtn.Visibility = source.CanMarkUnread ? Visibility.Visible : Visibility.Collapsed;
        DeleteMsgBtn.ToolTip = source.DeleteLabel + " (Del)";
        await LoadMessagesAsync(append: false);
    }

    private async Task LoadMessagesAsync(bool append)
    {
        if (_source == null) return;
        var source = _source;
        try
        {
            var (items, total) = await source.ListAsync(_position);
            if (source != _source) return;
            _total = total;
            var list = append && MessageList.ItemsSource is List<MessageSummary> old ? old.Concat(items).ToList() : items;
            MessageList.ItemsSource = list;
            _position = list.Count;
            MoreBtn.Visibility = _position < _total ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0) ShowFolderMessage("No messages here yet."); else EmptyText.Visibility = Visibility.Collapsed;
            FolderTitle.ToolTip = $"{_total} message(s)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A folder that was deleted in Fastmail: forget it so it can be recreated.
            if (ex is JmapException { Type: "invalidArguments" or "notFound" } && _alias != null && AppSettings.Current.Folders.Remove(_alias.Email))
            {
                AppSettings.Save(); _reg?.MarkRouted(); ShowCard(); ApplyFilter();
                ShowFolderMessage("This address's folder no longer exists in Fastmail. Use \"📁 Create folder\" to make it again.");
                return;
            }
            ShowFolderMessage("Could not load messages:\n" + ex.Message);
        }
    }

    private void ShowFolderMessage(string text)
    {
        if (MessageList.ItemsSource is List<MessageSummary> { Count: > 0 }) return;
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
        MoreBtn.Visibility = Visibility.Collapsed;
    }

    private async void OnMore(object sender, RoutedEventArgs e) => await LoadMessagesAsync(append: true);

    private async void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageSummary s || _source is not { } source) return;
        try
        {
            Status("opening message…");
            var m = await source.GetAsync(s.Id);
            if (MessageList.SelectedItem != s) return;
            _message = m;
            _remoteImages = AppSettings.Current.LoadRemoteImages;
            ShowMessageHead();
            ShowMessageBody();
            Status("ready");
            if (s.Unread)
            {
                s.Unread = false;
                MessageList.Items.Refresh();
                MessageList.SelectedItem = s;
                await source.SetSeenAsync(s.Id, true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status("⚠ " + ex.Message); }
    }

    private void ShowMessageHead()
    {
        if (_message is not { } m) return;
        Welcome.Visibility = Visibility.Collapsed;
        ReaderHead.Visibility = Visibility.Visible;
        ReadSubject.Text = m.Subject;
        ReadFrom.Text = string.Join(", ", m.From.Select(a => a.ToString())) + "   ·   " + m.Sent.LocalDateTime.ToString("ddd d MMM yyyy, HH:mm");
        ReadTo.Text = "to " + string.Join(", ", m.To.Select(a => a.ToString())) + (m.Cc.Count > 0 ? "   cc " + string.Join(", ", m.Cc.Select(a => a.ToString())) : "");
        ImagesBtn.Visibility = !_remoteImages && m.Html.Contains("http", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        ReadAttachments.Children.Clear();
        foreach (var att in m.Attachments.Where(a => !(a.Inline && a.Cid != null && m.Html.Contains("cid:" + a.Cid, StringComparison.OrdinalIgnoreCase))))
        {
            var b = new Button { Content = $"📎 {att.Name}  ({Size(att.Size)})", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Save this attachment" };
            b.Click += async (_, _) => await SaveAttachmentAsync(att);
            ReadAttachments.Children.Add(b);
        }
    }

    private async void ShowMessageBody()
    {
        if (_message is not { } m || _source is not { } source) return;
        var html = m.Html.Length > 0 ? HtmlTools.Sanitize(m.Html) : HtmlTools.TextToHtml(m.Text);
        // Inline (cid:) images come from the message itself, so they always show.
        var cids = m.Attachments.Where(a => a.Cid != null && a.Size < 8_000_000 && html.Contains("cid:" + a.Cid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cids.Count > 0)
        {
            var parts = new Dictionary<string, (string, byte[])>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cids)
                try { parts[c.Cid!] = (c.Type, await source.DownloadAsync(c)); } catch { }
            if (_message != m) return;
            html = HtmlTools.ResolveCids(html, parts);
        }
        var page = HtmlTools.ReaderPage(html, _remoteImages, ThemeManager.Css("Bg"), ThemeManager.Css("Fg"), ThemeManager.Css("Accent"), ThemeManager.Css("Muted"));
        await NavigateReaderAsync(page);
    }

    private async Task InitReaderAsync()
    {
        try
        {
            await WebHost.InitAsync(Reader, scripts: false);
            // Big messages are served from memory at this private address: message content is never written to disk.
            Reader.CoreWebView2.AddWebResourceRequestedFilter(ReaderUrl, CoreWebView2WebResourceContext.Document);
            Reader.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith(ReaderUrl, StringComparison.Ordinal) || _readerPage == null) return;
                var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_readerPage));
                e.Response = Reader.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
            Reader.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (_internalNav) { _internalNav = false; return; }
                e.Cancel = true;
                Dispatcher.BeginInvoke(() => WebHost.OpenExternal(e.Uri));
            };
            Reader.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(() => WebHost.OpenExternal(e.Uri));
            };
            _readerReady = true;
        }
        catch (Exception ex)
        {
            Status("⚠ the message viewer needs the Microsoft Edge WebView2 Runtime: " + ex.Message);
        }
    }

    private async Task NavigateReaderAsync(string page)
    {
        for (int i = 0; i < 50 && !_readerReady; i++) await Task.Delay(100);
        if (!_readerReady) return;
        _internalNav = true;
        if (page.Length < 1_500_000) { _readerPage = null; Reader.NavigateToString(page); }
        else
        {
            // NavigateToString is limited to 2 MB; big messages (inline photos) are served from memory instead.
            _readerPage = page;
            Reader.CoreWebView2.Navigate(ReaderUrl + Guid.NewGuid().ToString("N"));
        }
    }

    private void OnLoadImages(object sender, RoutedEventArgs e)
    {
        _remoteImages = true;
        ImagesBtn.Visibility = Visibility.Collapsed;
        ShowMessageBody();
    }

    private async Task SaveAttachmentAsync(Attachment att)
    {
        if (_source is not { } source) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = string.Join("_", att.Name.Split(Path.GetInvalidFileNameChars())), Title = "Save attachment" };
        if (dlg.ShowDialog(this) != true) return;
        await Run(async () =>
        {
            await File.WriteAllBytesAsync(dlg.FileName, await source.DownloadAsync(att));
            Status("saved " + dlg.FileName);
        });
    }

    private async void OnMarkUnread(object sender, RoutedEventArgs e)
    {
        if (_message == null || _source is not { } source || MessageList.SelectedItem is not MessageSummary s) return;
        await Run(() => source.SetSeenAsync(_message.Id, false));
        s.Unread = true;
        MessageList.Items.Refresh();
    }

    private async void OnDeleteMessage(object sender, RoutedEventArgs e)
    {
        if (_message == null || _source is not { } source) return;
        var id = _message.Id;
        await Run(async () =>
        {
            await source.DeleteAsync(id);
            if (MessageList.ItemsSource is List<MessageSummary> list)
            {
                var idx = list.FindIndex(x => x.Id == id);
                list.RemoveAll(x => x.Id == id);
                MessageList.Items.Refresh();
                _message = null;
                ReaderHead.Visibility = Visibility.Collapsed;
                await NavigateReaderAsync("<html><body style='background:" + ThemeManager.Css("Bg") + "'></body></html>");
                if (list.Count > 0) MessageList.SelectedIndex = Math.Min(idx, list.Count - 1);
            }
            Status(source is FastmailFolder ? "moved to Trash" : "deleted");
        });
    }

    // ---------- compose ----------

    private void OnCompose(object sender, RoutedEventArgs e) => OpenCompose(ComposeWindow.Mode.New, CurrentAlias(), null);
    private void OnComposeFromAlias(object sender, RoutedEventArgs e) => OpenCompose(ComposeWindow.Mode.New, CurrentAlias(), null);
    private void OnReply(object sender, RoutedEventArgs e) => OpenCompose(ComposeWindow.Mode.Reply, ReplyAlias(), _message);
    private void OnReplyAll(object sender, RoutedEventArgs e) => OpenCompose(ComposeWindow.Mode.ReplyAll, ReplyAlias(), _message);
    private void OnForward(object sender, RoutedEventArgs e) => OpenCompose(ComposeWindow.Mode.Forward, ReplyAlias(), _message);

    public void ComposeTo(string address)
    {
        var w = OpenCompose(ComposeWindow.Mode.New, ReplyAlias(), null);
        w?.SetTo(address);
    }

    /// <summary>Replies go out from the masked address the message was sent to, when it is one of ours.</summary>
    private Alias? ReplyAlias()
    {
        if (_reg == null) return null;
        if (_message != null)
        {
            var addressed = _message.To.Concat(_message.Cc).Select(a => a.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hit = _reg.Aliases.FirstOrDefault(a => addressed.Contains(a.Email));
            if (hit != null) return hit;
        }
        return _alias;
    }

    private ComposeWindow? OpenCompose(ComposeWindow.Mode mode, Alias? from, MessageDetail? original)
    {
        if (_reg?.Mail == null) { Dialogs.Info("Fastmail needed", "Sending mail needs your Fastmail API token (with Email submission). Add it in Settings."); return null; }
        if (mode != ComposeWindow.Mode.New && original == null) return null;
        var w = new ComposeWindow(_reg, mode, from, original);
        w.Sent += () => Status("sent ✓");
        w.Queued += n => Status($"added {n} email(s) to the queue · {Outbox.Items.Count} waiting (📤 Queue)");
        w.Show();
        return w;
    }

    // ---------- routing ----------

    private async void OnSweep(object sender, RoutedEventArgs e) => await SweepAsync(quiet: false);

    private async Task SweepAsync(bool quiet)
    {
        if (_reg?.Mail == null || _sweeping) return;
        if (!AppSettings.Current.RoutingEnabled) { if (!quiet) Status("routing is off (Settings → Routing)"); return; }
        _sweeping = true;
        try
        {
            var moved = await _reg.SweepAsync();
            _lastSweep = DateTime.Now;
            _movedTotal += moved;
            if (moved > 0)
            {
                if (!quiet || !IsVisible) _tray?.ShowBalloonTip(2500, "Mail filed", $"{moved} message(s) moved to their masked-address folders.", System.Windows.Forms.ToolTipIcon.None);
                if (_source is FastmailFolder) await LoadMessagesAsync(append: false);
            }
            if (!quiet) Status(moved > 0 ? $"filed {moved} message(s)" : "nothing to file");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status("⚠ routing: " + ex.Message);
        }
        finally { _sweeping = false; UpdateRouteText(); }
    }

    private void UpdateRouteText()
    {
        if (_reg?.Mail == null) { RouteText.Text = "routing: needs Fastmail"; return; }
        if (!AppSettings.Current.RoutingEnabled) { RouteText.Text = "routing: off"; return; }
        var n = _reg.RouteMap().Count;
        RouteText.Text = $"routing {n} address(es) → {AppSettings.Current.RouteParent}/  ·  every {AppSettings.Current.SweepMinutes} min" +
                         (_lastSweep is { } t ? $"  ·  last {t:HH:mm}" : "") + (_movedTotal > 0 ? $"  ·  filed {_movedTotal}" : "");
    }

    // ---------- settings / theme ----------

    private async void OnSettings(object sender, RoutedEventArgs e) => await OpenSettingsAsync(null);

    public async Task OpenSettingsAsync(string? category)
    {
        var w = new SettingsWindow(_reg, category) { Owner = this };
        if (w.ShowDialog() == true || w.Restored) await ReloadAccountsAsync();
        else UpdateRouteText();
        RefreshLockButton();
        ApplyFilter();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !IsLoaded && AppSettings.Current.Theme == tag) return;
        if (AppSettings.Current.Theme != tag) { AppSettings.Current.Theme = tag; AppSettings.Save(); }
        ThemeManager.Apply(Enum.Parse<ThemeMode>(tag));
    }

    // ---------- helpers ----------

    private async Task Run(Func<Task> action)
    {
        try { Cursor = Cursors.Wait; await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status("⚠ " + ex.Message);
            Dialogs.Error("That didn't work", ex.Message);
        }
        finally { Cursor = null; }
    }

    private static string Size(long b) => b switch
    {
        < 1024 => b + " B",
        < 1024 * 1024 => (b / 1024.0).ToString("0") + " KB",
        _ => (b / 1024.0 / 1024.0).ToString("0.0") + " MB",
    };
}
