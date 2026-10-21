using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcxMail.Addons;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;
using Microsoft.Win32;
using static DcxMail.Views.SettingsUi;

namespace DcxMail.Views;

/// <summary>
/// Settings, laid out like DarkCipher ServerManagement: categories on the left, a search across every setting, and one
/// category's cards at a time. Security changes apply at once; everything else is applied with Save. All of it is
/// stored encrypted (settings.dcx).
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>The categories in order: name, glyph, what belongs there.</summary>
    public static readonly (string Name, string Glyph, string Blurb)[] Categories =
    {
        ("Security & privacy", "⛨", "Encryption of everything stored, master password, two-factor sign-in, app lock, backups and the security log."),
        ("Fastmail", "✉", "The Fastmail account this app manages masked email for, reads, files and sends mail with."),
        ("Masked-email services", "⇌", "SimpleLogin (and Proton Pass), addy.io, Firefox Relay and DuckDuckGo. Their addresses appear beside your Fastmail ones and their mail is filed the same way."),
        ("Your own domains", "⌂", "Addresses on domains you own: Forward Email, Cloudflare Email Routing, ImprovMX, Migadu and your own mailcow server."),
        ("Disposable inboxes", "⌛", "Throwaway inboxes on other services, read inside the app: Mail.tm / Mail.gw, TempMail.lol and DropMail. Off unless you turn them on — and never for anything important."),
        ("Throwaway addresses", "⏱", "Disposable addresses that end themselves after a set time: which service makes them, how long they last and what happens when they expire."),
        ("Folders & routing", "📁", "Where each masked address's folder lives and how often Inbox mail is filed into it."),
        ("Writing", "✎", "Your name on sent mail and the composer's default font."),
        ("Appearance & behaviour", "◐", "Theme, the notification-area icon and starting with Windows."),
        ("Add-ons", "◇", "Extend MaskedMail with sandboxed programs and colour themes. Everything between an add-on and the app is encrypted."),
        ("About", "ⓘ", "Version, where your data is stored, and what the app connects to."),
    };

    /// <summary>The category Settings shows when it opens without one (the last one used this session).</summary>
    private static string _lastCategory = Categories[0].Name;

    private readonly ProviderRegistry? _reg;
    private readonly PasswordBox _fmToken = Pw();
    private readonly List<ServiceSettingsCard> _serviceCards = [];
    private readonly TextBox _fmUrl = Field();
    private readonly TextBox _parent = Field(), _minutes = Field(), _senderName = Field();
    private readonly ComboBox _font = new() { Margin = new Thickness(0, 0, 0, 10), Width = 260, HorizontalAlignment = HorizontalAlignment.Left },
        _size = new() { Margin = new Thickness(0, 0, 0, 10), Width = 160, HorizontalAlignment = HorizontalAlignment.Left },
        _theme = new() { Margin = new Thickness(0, 0, 0, 10), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _routing = Check("File mail for masked addresses into their folders"),
        _tray = Check("Closing the window keeps MaskedMail running in the notification area (routing keeps working)"),
        _startup = Check("Start with Windows (in the notification area)"),
        _images = Check("Load remote images in messages automatically (lets senders see when you open mail)");

    private readonly ListBox _catList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox _search = new() { Padding = new Thickness(6, 5, 6, 5), ToolTip = "Search every setting (Ctrl+F)" };
    private readonly TextBlock _searchHint = new() { Text = "🔍 search settings", IsHitTestVisible = false, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _page = new() { Margin = new Thickness(22, 16, 22, 24), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Dictionary<string, FrameworkElement> _catHeads = new();
    private readonly List<(string Category, Border Card, string Title)> _cards = [];
    private readonly TextBlock _searchHead = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
    private Dictionary<Border, string>? _corpus;
    private string _building = "";

    public string ShownCategory { get; private set; } = "";

    public SettingsWindow(ProviderRegistry? reg, string? category = null)
    {
        _reg = reg;
        Title = "Settings — DarkCipher MaskedMail";
        Width = 1040; Height = 780; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "FsBase");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var s = AppSettings.Current;
        _fmToken.Password = s.FastmailToken; _fmUrl.Text = s.FastmailSessionUrl;
        _routing.IsChecked = s.RoutingEnabled; _parent.Text = s.RouteParent; _minutes.Text = s.SweepMinutes.ToString();
        _tray.IsChecked = s.CloseToTray; _startup.IsChecked = s.StartWithWindows; _images.IsChecked = s.LoadRemoteImages;
        _senderName.Text = s.SenderName;
        foreach (var f in new[] { "Segoe UI", "Arial", "Calibri", "Cambria", "Georgia", "Helvetica", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana", "Consolas", "Courier New" })
            _font.Items.Add(f);
        _font.SelectedItem = s.DefaultFont; if (_font.SelectedIndex < 0) _font.SelectedIndex = 0;
        foreach (var (label, v) in new[] { ("Small", 2), ("Normal", 3), ("Large", 4), ("Larger", 5) }) _size.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        _size.SelectedItem = _size.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.DefaultFontSize) ?? _size.Items[1];
        foreach (var (label, tag) in new[] { ("◐  System", "System"), ("☾  Dark", "Dark"), ("☀  Light", "Light") }) _theme.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        _theme.SelectedItem = _theme.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Theme) ?? _theme.Items[0];

        // ---- pages ----
        _searchHead.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _page.Children.Add(_searchHead);
        foreach (var (name, glyph, blurb) in Categories)
        {
            _page.Children.Add(_catHeads[name] = CategoryHead(name, blurb));
            _building = name;
            BuildCategory(name);
            _catList.Items.Add(CategoryItem(glyph, name));
        }
        _scroll.Content = _page;

        // ---- left: title, search, categories ----
        var left = new DockPanel { Margin = new Thickness(14, 16, 8, 12) };
        var lh = new StackPanel();
        lh.Children.Add(new TextBlock { Text = "Settings", Style = (Style)Application.Current.Resources["H1"], Margin = new Thickness(4, 0, 0, 10) });
        var searchHost = new Grid();
        _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        searchHost.Children.Add(_search);
        searchHost.Children.Add(_searchHint);
        lh.Children.Add(searchHost);
        lh.Children.Add(new Border { Height = 12 });
        DockPanel.SetDock(lh, Dock.Top);
        left.Children.Add(lh);
        var status = Hint(SecuritySummary());
        status.Margin = new Thickness(4, 10, 4, 0);
        DockPanel.SetDock(status, Dock.Bottom);
        left.Children.Add(status);
        left.Children.Add(_catList);
        var leftBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        leftBorder.SetResourceReference(Border.BackgroundProperty, "Surface");
        leftBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(leftBorder);
        Grid.SetColumn(_scroll, 1);
        grid.Children.Add(_scroll);

        // ---- footer ----
        var save = new Button { Content = "Save", IsDefault = true, Style = (Style)Application.Current.Resources["PrimaryButton"] };
        save.Click += (_, _) => Save();
        var footerNote = Hint("Security changes apply immediately. Save applies everything else.");
        footerNote.Margin = new Thickness(0);
        footerNote.VerticalAlignment = VerticalAlignment.Center;
        var buttons = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(save);
        right.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) });
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(footerNote);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        footer.SetResourceReference(Border.BorderBrushProperty, "Line");
        footer.SetResourceReference(Border.BackgroundProperty, "Surface");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(grid);
        Content = root;

        _catList.SelectionChanged += (_, _) => { if (_catList.SelectedItem is ListBoxItem { Tag: string c } && _search.Text.Length == 0) ShowCategory(c); };
        _search.TextChanged += (_, _) => { _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ApplySearch(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape && _search.IsKeyboardFocusWithin && _search.Text.Length > 0) { _search.Text = ""; e.Handled = true; }
        };
        RevealCategory(category ?? _lastCategory);
    }

    private void BuildCategory(string name)
    {
        switch (name)
        {
            case "Security & privacy":
                SecurityCards();
                var hide = Check("Hide MaskedMail from screen capture — screenshots, recording, streaming (OBS, Discord, Teams) and remote viewers see a blank window");
                hide.IsChecked = AppSettings.Current.HideFromCapture;
                hide.Click += (_, _) =>
                {
                    AppSettings.Current.HideFromCapture = hide.IsChecked == true; AppSettings.Save(); CaptureGuard.Refresh();
                    AuditLog.Write(AppSettings.Current.HideFromCapture ? "hide from capture on" : "hide from capture off");
                };
                Card("Privacy",
                    "How messages are shown. Scripts in mail never run, remote content is blocked until you load it, and links show their real address before they open. " +
                    "The sign-in screen and security dialogs (2FA QR code, recovery codes, passwords) are always hidden from screen capture.",
                    _images, hide);
                break;
            case "Fastmail":
                Card("Fastmail account",
                    "Create an API token in Fastmail: Settings → Privacy & Security → Manage API tokens. Give it Masked Email, Email (read/write) and Email submission access.",
                    Label("API TOKEN"), _fmToken,
                    Label("JMAP SESSION URL"), _fmUrl,
                    TestRow(() => TestFastmailAsync()),
                    LinkRow("Open Fastmail API token settings", "https://app.fastmail.com/settings/security/tokens"));
                break;
            case "Masked-email services":
                ServiceCards(ServiceFamily.Masked);
                break;
            case "Your own domains":
                ServiceCards(ServiceFamily.OwnDomain);
                break;
            case "Disposable inboxes":
                ServiceCards(ServiceFamily.Disposable);
                Card("Services not included, and why",
                    "Every service with an API that could be checked is here. These were looked at and left out:",
                    Hint(string.Join("\n\n", ServiceCatalog.NotIncluded.Select(n => $"{n.Name} — {n.Why}"))));
                break;
            case "Throwaway addresses":
                ThrowawayCards();
                break;
            case "Folders & routing":
                var bulk = new Button { Content = "Create folders for every existing address", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
                var bulkResult = Hint("");
                bulk.Click += async (_, _) => await BulkFoldersAsync(bulk, bulkResult);
                Card("Routing",
                    "Each masked address gets a folder named after it under the parent folder. MaskedMail checks the Inbox on the interval and moves mail sent to that address into its folder (To, Cc, Bcc or Delivered-To). It keeps running while the app is in the notification area, and while it is locked.",
                    _routing,
                    Label("PARENT FOLDER"), _parent,
                    Label("CHECK EVERY (minutes)"), _minutes,
                    bulk, bulkResult);
                break;
            case "Writing":
                Card("Composer",
                    "Defaults for new emails. You can change font and size for any part of a message in the composer's toolbar.",
                    Label("YOUR NAME ON SENT MAIL (empty = none)"), _senderName,
                    Label("DEFAULT FONT"), _font,
                    Label("DEFAULT SIZE"), _size);
                break;
            case "Appearance & behaviour":
                Card("Appearance", "", Label("THEME"), _theme);
                Card("Notification area & start-up", "", _tray, _startup);
                break;
            case "Add-ons":
                var manage = new Button { Content = "◇  Manage add-ons…", Style = (Style)Application.Current.Resources["PrimaryButton"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
                manage.Click += (_, _) =>
                {
                    if (Application.Current.MainWindow is MainWindow mw) { DialogResult = false; mw.Dispatcher.BeginInvoke(mw.OpenAddons); }
                };
                var installed = AddonPackages.All;
                Card("Installed add-ons",
                    "Same model as DarkCipher ServerManagement. Program add-ons run at Low integrity in a Job with a locked-down process, and talk to the app only over an encrypted bridge (ECDH P-256 key exchange, AES-256-GCM, fresh keys for every run). " +
                    "You approve each permission when installing; changes and sends they ask for are shown to you first. Files are hash-checked before every run; theme add-ons are encrypted on disk. Only API 2 (encrypted) add-ons install.",
                    Hint(installed.Count == 0 ? "None installed yet." : string.Join("\n", installed.Select(a => $"{(a.Kind == "theme" ? "🎨" : "◇")} {a.Title} — {a.StateText}"))),
                    manage);
                break;
            case "About":
                Card("DarkCipher MaskedMail",
                    $"Version {App.Version} · © 2026 darkcipher0x · MIT licence",
                    Hint("Encrypted settings, key ring and security log: " + SecureStore.DataDir),
                    Hint("Viewer cache (no message content): " + App.CacheDir),
                    Hint("Connects only to Fastmail and the services you set up or turn on in Settings (" + ConnectedServices() + ") and, if you load them, remote images in a message. No telemetry, no account of its own."));
                break;
        }
    }

    /// <summary>The services currently set up, for the About card ("none" when only Fastmail is).</summary>
    private static string ConnectedServices()
    {
        var on = ServiceCatalog.All.Where(i => i.IsConfigured(AppSettings.Current.ServiceFields(i.Key))).Select(i => i.Name).ToList();
        return on.Count == 0 ? "none besides Fastmail right now" : "now: " + string.Join(", ", on);
    }

    private static ListBoxItem CategoryItem(string glyph, string name)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var g = new TextBlock { Text = glyph, Width = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        row.Children.Add(g);
        row.Children.Add(new TextBlock { Text = name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = name, Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 0, 0, 2) };
    }

    private FrameworkElement CategoryHead(string name, string blurb)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 14), Tag = name };
        p.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["H1"] });
        p.Children.Add(new TextBlock { Text = blurb, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 3, 0, 0) });
        return p;
    }

    /// <summary>Shows one category (clears the search).</summary>
    public void RevealCategory(string name)
    {
        if (!_catHeads.ContainsKey(name)) name = Categories[0].Name;
        if (_search.Text.Length > 0) _search.Text = "";
        _catList.SelectedItem = _catList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == name);
        ShowCategory(name);
    }

    private void ShowCategory(string name)
    {
        ShownCategory = _lastCategory = name;
        _searchHead.Visibility = Visibility.Collapsed;
        foreach (var (cat, head) in _catHeads) head.Visibility = cat == name ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (cat, card, _) in _cards) card.Visibility = cat == name ? Visibility.Visible : Visibility.Collapsed;
        _scroll.ScrollToTop();
    }

    /// <summary>Text a card is found by: title, category, hints and option labels (never the contents of text or password boxes).</summary>
    private Dictionary<Border, string> Corpus()
    {
        if (_corpus != null) return _corpus;
        _corpus = new();
        foreach (var (cat, card, title) in _cards)
        {
            var sb = new StringBuilder(title).Append(' ').Append(cat).Append(' ');
            void Walk(DependencyObject d)
            {
                switch (d)
                {
                    case TextBox or PasswordBox: return;
                    case TextBlock t: sb.Append(t.Text).Append(' '); break;
                    case ContentControl { Content: string s }: sb.Append(s).Append(' '); break;
                }
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c);
            }
            Walk(card);
            _corpus[card] = sb.ToString();
        }
        return _corpus;
    }

    private void ApplySearch()
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) { RevealCategory(_lastCategory); return; }
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int hits = 0;
        foreach (var (card, text) in Corpus())
        {
            bool hit = words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
            card.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            if (hit) hits++;
        }
        foreach (var head in _catHeads.Values) head.Visibility = Visibility.Collapsed;
        _catList.SelectedItem = null;
        ShownCategory = "";
        _searchHead.Visibility = Visibility.Visible;
        _searchHead.Text = hits == 0 ? $"Nothing matches “{q}”. Try other words, e.g. “password”, “2FA”, “lock”, “folder”, “token”, “font”."
                                     : $"{hits} card(s) match “{q}”. Esc clears the search.";
        _scroll.ScrollToTop();
    }

    /// <summary>A card in the category being built. The title shows the category when search mixes them.</summary>
    private Border Card(string title, string hint, params UIElement[] children)
    {
        var p = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var cat = new TextBlock { Text = _building, VerticalAlignment = VerticalAlignment.Center };
        cat.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        cat.SetResourceReference(TextBlock.FontSizeProperty, "FsSmall");
        DockPanel.SetDock(cat, Dock.Right);
        header.Children.Add(cat);
        header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H2"], Margin = new Thickness(0) });
        p.Children.Add(header);
        if (hint.Length > 0) p.Children.Add(Hint(hint));
        foreach (var c in children) p.Children.Add(c);
        var card = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(18, 14, 18, 14), Child = p };
        _cards.Add((_building, card, title));
        _page.Children.Add(card);
        return card;
    }

    private static string SecuritySummary() =>
        (SecureStore.PasswordRequiredOnStart ? "🔒 Encrypted · master password at start" : SecureStore.HasPassword ? "🔒 Encrypted · Windows account + master password" : "🔒 Encrypted · Windows account") +
        (SecureStore.HasTotp ? " · 2FA on" : "") + (AppSettings.Current.AutoLockMinutes > 0 ? $" · auto-lock {AppSettings.Current.AutoLockMinutes} min" : "") +
        (AppSettings.Current.SandboxMode ? " · sandbox" : "");

    /// <summary>One card per service of a kind, each built from what the service declares about itself.</summary>
    private void ServiceCards(ServiceFamily family)
    {
        foreach (var info in ServiceCatalog.All.Where(i => i.Family == family))
        {
            var card = new ServiceSettingsCard(info, AppSettings.Current.ServiceFields(info.Key));
            _serviceCards.Add(card);
            Card(info.Name, info.Summary, card.Controls);
        }
    }

    // ---- throwaway addresses ----

    private readonly ComboBox _twService = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) },
        _twLife = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
    private readonly RadioButton _twDelete = new() { GroupName = "twExpiry", Margin = new Thickness(0, 2, 0, 4) },
        _twDisable = new() { GroupName = "twExpiry", Margin = new Thickness(0, 2, 0, 8) };
    private readonly CheckBox _twClean = Check("Also move its mail to Trash and remove its folder (Trash keeps it for Fastmail's usual period)");
    private readonly TextBox _twPoll = Field();

    private void ThrowawayCards()
    {
        var s = AppSettings.Current;
        _twService.Items.Add(new ComboBoxItem { Content = "Automatic (Fastmail when set up)", Tag = "" });
        foreach (var (key, name) in new[] { FastmailProvider.Service }.Concat(ServiceCatalog.All).Where(i => i.Has(Features.Create)).Select(i => (i.Key, i.Family == ServiceFamily.Disposable ? i.Name + " (disposable inbox)" : i.Name)))
            _twService.Items.Add(new ComboBoxItem { Content = name + (_reg?.Get(key) == null ? "  (not set up)" : ""), Tag = key });
        _twService.SelectedItem = _twService.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.ThrowawayProvider) ?? _twService.Items[0];
        foreach (var (min, label) in Throwaways.Lifetimes) _twLife.Items.Add(new ComboBoxItem { Content = label, Tag = min });
        _twLife.SelectedItem = _twLife.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.ThrowawayMinutes) ?? _twLife.Items[1];
        _twDelete.Content = new TextBlock { Text = "Delete the address — mail sent to it afterwards bounces (Fastmail keeps it as “deleted”)", TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
        _twDisable.Content = new TextBlock { Text = "Only disable it — mail to it is dropped, and you can enable it again later", TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
        _twDelete.IsChecked = s.ThrowawayDelete; _twDisable.IsChecked = !s.ThrowawayDelete;
        _twClean.IsChecked = s.ThrowawayCleanMail;
        _twPoll.Text = s.ThrowawayPollSeconds.ToString();
        _twPoll.Width = 80; _twPoll.HorizontalAlignment = HorizontalAlignment.Left;

        Card("Throwaway addresses",
            "⏱ Throwaway in the header (or Ctrl+Shift+T, or the tray menu) makes a random masked address in one click, copies it, and opens its live inbox, where verification codes and confirmation links are picked out for you. " +
            "When its time is up it ends by itself — checked every 30 s while MaskedMail runs (the tray is enough) and at every start. You can extend it, keep it as a normal address, or burn it early.",
            Label("SERVICE FOR NEW THROWAWAYS"), _twService,
            Label("DEFAULT LIFETIME"), _twLife,
            Label("WHEN IT EXPIRES"), _twDelete, _twDisable, _twClean,
            Label("LIVE INBOX CHECKS FOR MAIL EVERY (seconds, 5–300)"), _twPoll);

        var active = s.Throwaways.Values.OrderBy(t => t.Expires).ToList();
        Card("Active throwaways", "",
            Hint(active.Count == 0 ? "None right now."
                : string.Join("\n", active.Select(t => $"⏱ {t.Email} — {(t.Expired ? "expired, ending at the next check" : "expires in " + t.LeftText + $" ({t.Expires.LocalDateTime:ddd d MMM HH:mm})")} · {ProviderRegistry.NameOf(t.ProviderKey)}" + (t.LastError != null ? $"  ⚠ {t.LastError}" : "")))));
    }

    // ---- self-test hooks ----
    internal IReadOnlyList<string> VisibleCardsForTest => _cards.Where(c => c.Card.Visibility == Visibility.Visible).Select(c => c.Title).ToList();
    internal IReadOnlyList<string> CardTitlesForTest => _cards.Select(c => c.Title).ToList();
    internal void SearchForTest(string q) => _search.Text = q;

    private void Save()
    {
        var s = AppSettings.Current;
        s.FastmailToken = _fmToken.Password.Trim(); s.FastmailSessionUrl = Url(_fmUrl.Text, "https://api.fastmail.com/jmap/session");
        foreach (var card in _serviceCards)
        {
            var values = card.Values();
            if (values.Count > 0) s.Services[card.Info.Key] = values; else s.Services.Remove(card.Info.Key);
        }
        var parent = _parent.Text.Trim().Trim('/');
        if (!parent.Equals(s.RouteParent, StringComparison.OrdinalIgnoreCase) && s.Folders.Count > 0 &&
            !Dialogs.Confirm("Change the parent folder?", $"Existing folders stay where they are, under {s.RouteParent}. New addresses get folders under {parent}.", "Change"))
            return;
        s.RouteParent = parent.Length > 0 ? parent : "Inbox/automation";
        s.RoutingEnabled = _routing.IsChecked == true;
        s.SweepMinutes = int.TryParse(_minutes.Text, out var m) ? Math.Clamp(m, 1, 120) : 2;
        s.CloseToTray = _tray.IsChecked == true;
        s.LoadRemoteImages = _images.IsChecked == true;
        s.SenderName = _senderName.Text.Trim();
        s.DefaultFont = _font.SelectedItem as string ?? "Segoe UI";
        s.DefaultFontSize = (int)((ComboBoxItem)_size.SelectedItem).Tag;
        s.StartWithWindows = _startup.IsChecked == true;
        SetStartup(s.StartWithWindows);
        s.ThrowawayProvider = (_twService.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        s.ThrowawayMinutes = (_twLife.SelectedItem as ComboBoxItem)?.Tag is int tl ? tl : 60;
        s.ThrowawayDelete = _twDelete.IsChecked == true;
        s.ThrowawayCleanMail = _twClean.IsChecked == true;
        s.ThrowawayPollSeconds = int.TryParse(_twPoll.Text, out var tp) ? Math.Clamp(tp, 5, 300) : 10;
        if (_theme.SelectedItem is ComboBoxItem { Tag: string theme } && theme != s.Theme)
        {
            s.Theme = theme;
            ThemeManager.Apply(Enum.Parse<ThemeMode>(theme));
        }
        AppSettings.Save();
        DialogResult = true;
    }

    private static string Url(string text, string fallback)
    {
        var t = text.Trim();
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == "https" ? t : fallback;
    }

    private static void SetStartup(bool on)
    {
        if (Packaging.IsPackaged)
        {
            // Store install: Windows' startup task (a Run entry can't start a packaged app).
            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (await Packaging.SetStartupTaskAsync(on) is { } problem) Dialogs.Info("Start with Windows", problem);
            });
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (on) key.SetValue("DarkCipherMaskedMail", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("DarkCipherMaskedMail", false);
        }
        catch { }
    }

    private async Task<string> TestFastmailAsync()
    {
        using var j = new JmapClient(Url(_fmUrl.Text, "https://api.fastmail.com/jmap/session"), _fmToken.Password);
        await j.ConnectAsync();
        var missing = new List<string>();
        if (!j.HasMaskedEmail) missing.Add("Masked Email");
        if (!j.HasMail) missing.Add("Email");
        if (!j.HasSubmission) missing.Add("Email submission");
        var count = j.HasMaskedEmail ? (await new FastmailProvider(j).ListAsync()).Count : 0;
        return $"✓ signed in as {j.Username} · {count} masked address(es)" + (missing.Count > 0 ? " · ⚠ token lacks: " + string.Join(", ", missing) : "");
    }

    private async Task BulkFoldersAsync(Button b, TextBlock result)
    {
        if (_reg?.Mail == null) { result.Text = "Save a Fastmail API token first."; return; }
        var todo = _reg.Aliases.Where(a => !AppSettings.Current.Folders.ContainsKey(a.Email)).ToList();
        if (todo.Count == 0) { result.Text = "Every address already has a folder."; return; }
        if (!Dialogs.Confirm("Create folders", $"Create {todo.Count} folder(s) under {AppSettings.Current.RouteParent}, one per masked address, and file their Inbox mail?", "Create")) return;
        b.IsEnabled = false;
        int ok = 0; var errors = new List<string>();
        foreach (var a in todo)
        {
            result.Text = $"creating {ok + errors.Count + 1} of {todo.Count}…";
            try { await _reg.EnsureFolderAsync(a); ok++; }
            catch (Exception ex) { errors.Add($"{a.Email}: {ex.Message}"); }
        }
        try { var moved = await _reg.SweepAsync(); result.Text = $"✓ {ok} folder(s) created, {moved} message(s) filed"; }
        catch (Exception ex) { result.Text = $"✓ {ok} folder(s) created; filing failed: {ex.Message}"; }
        if (errors.Count > 0) result.Text += "\n⚠ " + string.Join("\n⚠ ", errors.Take(5)) + (errors.Count > 5 ? $"\n…and {errors.Count - 5} more" : "");
        b.IsEnabled = true;
    }

    // ---------- security (same options as DarkCipher ServerManagement) ----------

    /// <summary>Set when a backup was restored: the caller reloads everything even though Save wasn't pressed.</summary>
    public bool Restored { get; private set; }

    private readonly TextBlock _storeStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _totpStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private CheckBox? _sandbox;


    private void SecurityCards()
    {
        UpdateSecurity();
        var s = AppSettings.Current;
        Card("Encryption & master password",
            "Your Fastmail token, every other API key, the folder list, all settings and the security log are encrypted with AES-256-GCM under one random data key. " +
            "Mail itself stays in Fastmail; message content is never written to this PC. A master password required at start means the files can't be opened even by an administrator or from a copy of your disk.",
            _storeStatus,
            Row(Btn("Set / change master password…", SetPassword, "PrimaryButton"), Btn("Remove master password", RemovePassword)));

        Card("Two-factor sign-in",
            "A 6-digit code from an authenticator app (Microsoft/Google Authenticator, Authy, 1Password, Bitwarden…) on top of the password or Windows account, asked when the app opens and when it unlocks.",
            _totpStatus,
            Row(Btn("Set up 2FA…", SetupTotp, "PrimaryButton"), Btn("New recovery codes…", NewRecoveryCodes), Btn("Turn off 2FA…", RemoveTotp)));

        var winLock = Check("Lock when Windows locks or the PC sleeps");
        winLock.IsChecked = s.LockOnWindowsLock;
        winLock.Click += (_, _) => { s.LockOnWindowsLock = winLock.IsChecked == true; AppSettings.Save(); };
        _sandbox = Check("Sandbox mode — master password required at every start, auto-lock always on, and the message viewer's cache wiped on exit");
        _sandbox.IsChecked = s.SandboxMode;
        _sandbox.Click += (_, _) => SetSandbox(_sandbox.IsChecked == true);
        Card("App lock",
            "Hides every window behind the sign-in screen. Needs a master password or 2FA to unlock with. Mail keeps being filed while locked.",
            Label("LOCK AFTER INACTIVITY"),
            Row(AutoLockBox(), Btn("🔒 Lock now (Ctrl+Shift+L)", () =>
            {
                if (!AppLock.CanLock) { Dialogs.Info("Lock", "Set a master password or turn on 2FA first."); return; }
                if (Application.Current.MainWindow is MainWindow mw) { DialogResult = false; mw.Dispatcher.BeginInvoke(mw.LockNow); }
            })),
            winLock, _sandbox);

        Card("Backups & security log",
            "A backup holds every API key and setting, sealed with a password of its own, so it restores on any PC. The security log lists sign-ins, locks, security changes and masked addresses created or deleted; each line is encrypted.",
            Row(Btn("Encrypted backup…", ExportBackup), Btn("Restore from backup…", RestoreBackup), Btn("Security log…", ShowAuditLog)));
    }

    private ComboBox AutoLockBox()
    {
        var opts = new[] { (0, "Auto-lock: never"), (5, "after 5 minutes"), (10, "after 10 minutes"), (15, "after 15 minutes"), (30, "after 30 minutes"), (60, "after 1 hour"), (240, "after 4 hours") };
        var box = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 4), ItemsSource = opts.Select(o => o.Item2).ToList(), ToolTip = "Lock after this long without keyboard or mouse input" };
        box.SelectedIndex = Math.Max(0, Array.FindIndex(opts, o => o.Item1 == AppSettings.Current.AutoLockMinutes));
        box.SelectionChanged += (_, _) =>
        {
            var v = opts[Math.Max(0, box.SelectedIndex)].Item1;
            if (v > 0 && !AppLock.CanLock) { Dialogs.Info("Auto-lock", "Set a master password or turn on 2FA first — the lock screen needs something to unlock with."); box.SelectedIndex = 0; return; }
            if (v == 0 && AppSettings.Current.SandboxMode) { Dialogs.Info("Auto-lock", "Sandbox mode needs auto-lock. Turn sandbox mode off first to choose “never”."); box.SelectedIndex = Math.Max(0, Array.FindIndex(opts, o => o.Item1 == AppSettings.Current.AutoLockMinutes)); return; }
            AppSettings.Current.AutoLockMinutes = v; AppSettings.Save();
        };
        return box;
    }

    private void SetSandbox(bool on)
    {
        var s = AppSettings.Current;
        if (on && !SecureStore.PasswordRequiredOnStart)
        {
            if (!Dialogs.Confirm("Sandbox mode", "Sandbox mode makes the master password mandatory at every start (the Windows-account copy of the key is removed), so nothing opens without signing in.\n\nSet the master password now?", "Set password"))
            { _sandbox!.IsChecked = false; return; }
            SetPassword();
            if (!SecureStore.PasswordRequiredOnStart) { _sandbox!.IsChecked = false; Dialogs.Info("Sandbox mode", "Not enabled — a master password with “require at start” is needed."); return; }
        }
        s.SandboxMode = on;
        if (on && s.AutoLockMinutes == 0) s.AutoLockMinutes = 15;
        AppSettings.Save();
        AuditLog.Write(on ? "sandbox mode on" : "sandbox mode off");
        AppLock.ReconcileSandbox();
        _sandbox!.IsChecked = s.SandboxMode;
    }

    private void UpdateSecurity()
    {
        _storeStatus.Text = SecureStore.PasswordRequiredOnStart
            ? "🔒 Key protected by your master password only — asked every time the app starts."
            : SecureStore.HasPassword
                ? "🔒 Key protected by your Windows account (DPAPI) and a master password (used for recovery on another PC/account)."
                : "🔒 Key protected by your Windows account (DPAPI). Anyone signed in as you can open the app.";
        _totpStatus.Text = SecureStore.HasTotp
            ? $"✓ On — a 6-digit code from your authenticator is needed to open or unlock the app. {SecureStore.RecoveryCodesLeft} recovery code(s) left."
            : "Off.";
        if (AppLock.ReconcileSandbox() && _sandbox != null) _sandbox.IsChecked = AppSettings.Current.SandboxMode;
        (Application.Current.MainWindow as MainWindow)?.RefreshLockButton();
    }

    private static bool ReauthPassword(string title)
    {
        if (!SecureStore.HasPassword) return true;
        if (Dialogs.Prompt(title, "Current master password:", password: true) is { } pw && SecureStore.VerifyPassword(pw)) return true;
        Dialogs.Error(title, "Wrong password.");
        return false;
    }

    private void SetPassword()
    {
        if (SecureStore.HasPassword && !ReauthPassword("Master password")) return;
        var w = Dialogs.MakeWindow("Master password", 460);
        var p = new StackPanel { Margin = new Thickness(18) };
        var a = new PasswordBox { Margin = new Thickness(0, 0, 0, 6) }; var b = new PasswordBox();
        var require = new CheckBox { Content = new TextBlock { Text = "Require it every time the app starts (removes the Windows-account copy of the key)", TextWrapping = TextWrapping.Wrap }, IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        p.Children.Add(Label("NEW PASSWORD (12+ characters)")); p.Children.Add(a); p.Children.Add(Label("AGAIN")); p.Children.Add(b); p.Children.Add(require);
        p.Children.Add(new TextBlock { Text = "There is no recovery if you forget it while “require” is on — keep an encrypted backup.", Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 8, 0, 0) });
        var ok = new Button { Content = "Set password", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        p.Children.Add(Buttons(ok));
        w.Content = p;
        ok.Click += (_, _) =>
        {
            if (SecureStore.PasswordProblem(a.Password) is { } weak) { Dialogs.Error("Master password", weak); return; }
            if (a.Password != b.Password) { Dialogs.Error("Master password", "The passwords don't match."); return; }
            w.DialogResult = true;
        };
        w.Loaded += (_, _) => a.Focus();
        if (w.ShowDialog() != true) return;
        SecureStore.SetPassword(a.Password, require.IsChecked == true);
        AuditLog.Write(require.IsChecked == true ? "master password set (required on start)" : "master password set");
        UpdateSecurity();
    }

    private void RemovePassword()
    {
        if (!SecureStore.HasPassword) { Dialogs.Info("Master password", "No master password is set."); return; }
        if (AppSettings.Current.SandboxMode && !Dialogs.Confirm("Remove master password", "Sandbox mode needs the master password and will be switched off.", "Continue")) return;
        if (Dialogs.Prompt("Remove master password", "Current master password:", password: true) is not { } cur || !SecureStore.VerifyPassword(cur)) { Dialogs.Error("Master password", "Wrong password."); return; }
        SecureStore.RemovePassword();
        AuditLog.Write("master password removed");
        UpdateSecurity();
    }

    private void SetupTotp()
    {
        if (SecureStore.HasTotp) { Dialogs.Info("2FA", "Two-factor sign-in is already on. Use “New recovery codes” to re-enrol."); return; }
        if (!ReauthPassword("Set up 2FA")) return;
        var secret = Totp.NewSecret();
        var account = Environment.UserName + "@" + Environment.MachineName;
        var uri = Totp.Uri(secret, account);
        var w = Dialogs.MakeWindow("Set up two-factor sign-in", 520);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "1. Scan this with your authenticator app (or type the key).", TextWrapping = TextWrapping.Wrap });
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6, [0, 0, 0], [255, 255, 255]);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.StreamSource = new System.IO.MemoryStream(png); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
            p.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10), Child = new System.Windows.Controls.Image { Source = bmp, Width = 220, Height = 220 } });
        }
        catch { /* the typed key below still works */ }
        var b32 = Totp.Base32(secret);
        var key = string.Join(" ", Enumerable.Range(0, (b32.Length + 3) / 4).Select(i => b32.Substring(i * 4, Math.Min(4, b32.Length - i * 4))));
        var keyBox = new TextBox { Text = key, IsReadOnly = true, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(4) };
        keyBox.SetResourceReference(FontFamilyProperty, "MonoFont");
        p.Children.Add(keyBox);
        p.Children.Add(new TextBlock { Text = $"Account: DarkCipher MaskedMail:{account} · issuer DarkCipher Systems · SHA1, 6 digits, 30 s", Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 4, 0, 10) });
        p.Children.Add(new TextBlock { Text = "2. Enter the 6-digit code it shows to confirm:", TextWrapping = TextWrapping.Wrap });
        var code = new TextBox { MaxLength = 7, Width = 140, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4) };
        code.SetResourceReference(FontFamilyProperty, "MonoFont");
        code.SetResourceReference(FontSizeProperty, "FsCode");
        p.Children.Add(code);
        var ok = new Button { Content = "Confirm", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        p.Children.Add(Buttons(ok));
        w.Content = p;
        ok.Click += (_, _) =>
        {
            var c = new string(code.Text.Where(char.IsDigit).ToArray());
            long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            if (Enumerable.Range(-1, 3).Any(d => Totp.Code(secret, step + d) == c)) w.DialogResult = true;
            else Dialogs.Error("2FA", "That code doesn't match. Check the phone's clock is set automatically and try the next code.");
        };
        w.Loaded += (_, _) => code.Focus();
        if (w.ShowDialog() != true) { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); return; }
        var codes = Totp.RecoveryCodes();
        SecureStore.SetTotp(secret, codes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        AuditLog.Write("two-factor sign-in enabled");
        ShowRecovery(codes);
        UpdateSecurity();
    }

    private static void ShowRecovery(List<string> codes)
    {
        var text = "DarkCipher MaskedMail — 2FA recovery codes\n" + $"Created {DateTime.Now:yyyy-MM-dd HH:mm} on {Environment.MachineName}. Each code works once.\n\n" + string.Join("\n", codes);
        var w = Dialogs.MakeWindow("Recovery codes", 440);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "Store these somewhere safe (password manager, printed). Each one signs you in once if you lose your phone. They won't be shown again.", TextWrapping = TextWrapping.Wrap });
        var box = new TextBox { Text = string.Join("\n", codes), IsReadOnly = true, Margin = new Thickness(0, 10, 0, 10), Padding = new Thickness(8), TextAlignment = TextAlignment.Center };
        box.SetResourceReference(FontFamilyProperty, "MonoFont");
        box.SetResourceReference(FontSizeProperty, "FsTitle");
        p.Children.Add(box);
        var copy = new Button { Content = "Copy" };
        copy.Click += (_, _) =>
        {
            // Recovery codes don't linger: the clipboard is cleared after 60 s if it still holds them.
            if (!ClipboardHelper.TrySetText(text)) { copy.Content = "Clipboard busy — try again"; return; }
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            t.Tick += (_, _) => { t.Stop(); ClipboardHelper.ClearIf(text); };
            t.Start();
            copy.Content = "Copied (clears in 60 s)";
        };
        var save = new Button { Content = "Save as…" };
        save.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "darkcipher-maskedmail-recovery-codes.txt", Filter = "Text|*.txt" };
            if (dlg.ShowDialog(w) == true) System.IO.File.WriteAllText(dlg.FileName, text);
        };
        var done = new Button { Content = "I've saved them", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true, Margin = new Thickness(0) };
        done.Click += (_, _) => w.DialogResult = true;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(copy); row.Children.Add(save); row.Children.Add(done);
        p.Children.Add(row);
        w.Content = p;
        w.Closing += (_, e) => { if (w.DialogResult != true && !Dialogs.Confirm("Recovery codes", "Close without saving the recovery codes?", "Close")) e.Cancel = true; };
        w.ShowDialog();
    }

    private void NewRecoveryCodes()
    {
        if (!SecureStore.HasTotp) { Dialogs.Info("2FA", "Two-factor sign-in is off."); return; }
        if (Dialogs.Prompt("New recovery codes", "Enter a current authenticator code:") is not { } c || !SecureStore.VerifySecondFactor(c)) { Dialogs.Error("2FA", "Wrong code."); return; }
        Dialogs.Info("New recovery codes", "To issue new recovery codes, 2FA is set up again with a new key (your authenticator entry changes). Continue on the next screen.");
        SecureStore.RemoveTotp();
        AuditLog.Write("two-factor sign-in re-enrolled");
        SetupTotp();
        UpdateSecurity();
    }

    private void RemoveTotp()
    {
        if (!SecureStore.HasTotp) { Dialogs.Info("2FA", "Two-factor sign-in is off."); return; }
        if (!ReauthPassword("Turn off 2FA")) return;
        if (Dialogs.Prompt("Turn off 2FA", "Authenticator code (or a recovery code):") is not { } c || !SecureStore.VerifySecondFactor(c)) { Dialogs.Error("2FA", "Wrong code."); return; }
        SecureStore.RemoveTotp();
        AuditLog.Write("two-factor sign-in disabled");
        UpdateSecurity();
    }

    private void ExportBackup()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"maskedmail-{DateTime.Now:yyyyMMdd}.dcxbackup", Filter = "DarkCipher backup|*.dcxbackup", Title = "Encrypted backup" };
        if (dlg.ShowDialog(this) != true) return;
        var w = Dialogs.MakeWindow("Backup password", 440);
        var p = new StackPanel { Margin = new Thickness(18) };
        var a = new PasswordBox { Margin = new Thickness(0, 0, 0, 6) }; var b = new PasswordBox();
        p.Children.Add(new TextBlock { Text = "The backup holds every API key and setting, sealed with this password (not your Windows account), so it restores on any PC.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        p.Children.Add(Label("BACKUP PASSWORD (12+ characters)")); p.Children.Add(a); p.Children.Add(Label("AGAIN")); p.Children.Add(b);
        var ok = new Button { Content = "Create backup", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        p.Children.Add(Buttons(ok));
        w.Content = p;
        ok.Click += (_, _) =>
        {
            if (SecureStore.PasswordProblem(a.Password) is { } weak) { Dialogs.Error("Backup", weak); return; }
            if (a.Password != b.Password) { Dialogs.Error("Backup", "The passwords don't match."); return; }
            w.DialogResult = true;
        };
        w.Loaded += (_, _) => a.Focus();
        if (w.ShowDialog() != true) return;
        try
        {
            Backup.Export(dlg.FileName, a.Password);
            AuditLog.Write("encrypted backup created", System.IO.Path.GetFileName(dlg.FileName));
            Dialogs.Info("Backup", "Encrypted backup saved:\n" + dlg.FileName);
        }
        catch (Exception ex) { Dialogs.Error("Backup", ex.Message); }
    }

    private void RestoreBackup()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "DarkCipher backup|*.dcxbackup|All files|*.*", Title = "Restore from backup" };
        if (dlg.ShowDialog(this) != true) return;
        if (Dialogs.Prompt("Restore from backup", "Backup password:", password: true) is not { } pw) return;
        AppSettings restored;
        try { restored = Backup.Open(dlg.FileName, pw); }
        catch (System.Security.Cryptography.CryptographicException) { Dialogs.Error("Restore", "Wrong password, or the file was damaged."); return; }
        catch (Exception ex) { Dialogs.Error("Restore", ex.Message); return; }
        if (!Dialogs.Confirm("Restore from backup", "Replace every API key and setting here with the ones in the backup? Your master password and 2FA stay as they are.", "Replace", danger: true)) return;
        AppSettings.Replace(restored);
        AuditLog.Write("restored from backup", System.IO.Path.GetFileName(dlg.FileName));
        Restored = true;
        Dialogs.Info("Restore", "Restored. Settings will reload now.");
        DialogResult = false;
    }

    private void ShowAuditLog()
    {
        var entries = AuditLog.Read();
        entries.Reverse();
        var w = Dialogs.MakeWindow("Security log", 640);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "Sign-ins, locks, security changes, backups and masked addresses created or deleted. Every line is encrypted on disk; no keys, passwords or message content are recorded.", Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 0, 0, 8) });
        var list = new TextBox
        {
            IsReadOnly = true, Height = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8),
            Text = entries.Count == 0 ? "(empty)" : string.Join("\n", entries.Select(e => $"{e.At.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {e.Action}{(e.Detail.Length > 0 ? "  —  " + e.Detail : "")}")),
        };
        list.SetResourceReference(FontFamilyProperty, "MonoFont");
        list.SetResourceReference(FontSizeProperty, "FsMeta");
        p.Children.Add(list);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, Margin = new Thickness(0) };
        p.Children.Add(Buttons(close));
        w.Content = p;
        w.ShowDialog();
    }

    private static StackPanel Buttons(Button primary)
    {
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(primary);
        if (!primary.IsCancel) bs.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) });
        return bs;
    }

    private static WrapPanel Row(params UIElement[] items)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var i in items) w.Children.Add(i);
        return w;
    }

    private static Button Btn(string text, Action a, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4) };
        if (style != null) b.Style = (Style)Application.Current.Resources[style];
        b.Click += (_, _) => a();
        return b;
    }

    // ---------- small builders ----------

}
