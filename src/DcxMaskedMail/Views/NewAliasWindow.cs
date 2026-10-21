using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxMail.Providers;
using DcxMail.Services;
using static DcxMail.Views.SettingsUi;

namespace DcxMail.Views;

/// <summary>Create an address with any configured service. The window offers only what the chosen service can do
/// (a chosen name, a domain, where its mail goes) and shows its caution, if it has one, before anything is created.</summary>
public sealed class NewAliasWindow : Window
{
    private readonly ProviderRegistry _reg;
    private readonly ComboBox _provider = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly ComboBox _domain = new() { Margin = new Thickness(0, 0, 0, 10) }, _forward = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _desc = Field(), _site = Field(), _prefix = Field();
    private readonly TextBlock _prefixHint = Hint(""), _folderHint = Hint(""), _error = Hint(""), _domainLabel = Label("DOMAIN"), _forwardLabel = Label("ITS MAIL GOES TO");
    private readonly StackPanel _caution = new();
    private readonly CheckBox _route = new() { Content = "Create its folder and file its mail there", Margin = new Thickness(0, 2, 0, 4) };
    private readonly Button _create = new() { Content = "Create", IsDefault = true };
    private int _loading;

    public Alias? Created { get; private set; }
    public string? FolderError { get; private set; }

    public NewAliasWindow(ProviderRegistry reg)
    {
        _reg = reg;
        Title = "New address";
        Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "FsBase");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        _create.Style = (Style)Application.Current.Resources["PrimaryButton"];
        _error.Foreground = (Brush)Application.Current.Resources["Bad"];
        foreach (var p in reg.Providers.Where(p => p.Info.Has(Features.Create)))
            _provider.Items.Add(new ComboBoxItem { Content = p.Info.Family == ServiceFamily.Disposable ? p.Name + "  (disposable inbox)" : p.Name, Tag = p });
        _provider.SelectedIndex = 0;
        _provider.SelectionChanged += async (_, _) => await ServiceChangedAsync();
        _prefix.TextChanged += (_, _) => UpdateHints();

        var s = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        s.Children.Add(new TextBlock { Text = "New address", Style = (Style)Application.Current.Resources["H2"] });
        s.Children.Add(Label("SERVICE")); s.Children.Add(_provider);
        s.Children.Add(_caution);
        s.Children.Add(Label("WHAT IS IT FOR? (note)")); s.Children.Add(_desc);
        s.Children.Add(Label("WEBSITE (optional)")); s.Children.Add(_site);
        s.Children.Add(Label("ADDRESS STARTS WITH (optional)")); s.Children.Add(_prefix); s.Children.Add(_prefixHint);
        _prefixHint.Margin = new Thickness(0, -6, 0, 10);
        s.Children.Add(_domainLabel); s.Children.Add(_domain);
        s.Children.Add(_forwardLabel); s.Children.Add(_forward);
        s.Children.Add(_route); s.Children.Add(_folderHint);
        _folderHint.Margin = new Thickness(24, 0, 0, 12);
        s.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_create);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) });
        s.Children.Add(buttons);
        Content = s;
        _create.Click += async (_, _) => await CreateAsync();
        Loaded += async (_, _) => { _desc.Focus(); await ServiceChangedAsync(); };
        if (_provider.Items.Count == 0) { _error.Text = "Set up a service in Settings first."; _create.IsEnabled = false; }
    }

    private IAliasProvider? Provider => (_provider.SelectedItem as ComboBoxItem)?.Tag as IAliasProvider;

    /// <summary>Shows the chosen service's options, and loads its domains and forwarding targets.</summary>
    private async Task ServiceChangedAsync()
    {
        if (Provider is not { } p) return;
        var info = p.Info;
        _caution.Children.Clear();
        if (info.Caution != null) _caution.Children.Add(Caution(info.Caution));
        _prefix.IsEnabled = info.Has(Features.CustomName);
        _route.Visibility = info.Has(Features.Inbox) ? Visibility.Collapsed : Visibility.Visible;
        _route.IsChecked = AppSettings.Current.RoutingEnabled && _reg.Mail != null && !info.Has(Features.Inbox);
        _route.IsEnabled = _reg.Mail != null;
        UpdateHints();
        var round = ++_loading;
        await Fill(_domain, _domainLabel, p as IDomainChoice is { } d && info.Has(Features.ChooseDomain) ? d.DomainsAsync : null, round);
        await Fill(_forward, _forwardLabel, p as IForwardChoice is { } f && info.Has(Features.ForwardTo) ? f.ForwardTargetsAsync : null, round);
    }

    private async Task Fill(ComboBox box, TextBlock label, Func<CancellationToken, Task<List<string>>>? load, int round)
    {
        box.Items.Clear();
        box.Visibility = label.Visibility = load == null ? Visibility.Collapsed : Visibility.Visible;
        if (load == null) return;
        box.Items.Add(new ComboBoxItem { Content = "loading…", Tag = "" });
        box.SelectedIndex = 0;
        try
        {
            var items = await load(CancellationToken.None);
            if (round != _loading) return;
            box.Items.Clear();
            foreach (var i in items) box.Items.Add(new ComboBoxItem { Content = i, Tag = i });
            if (box.Items.Count == 0) box.Items.Add(new ComboBoxItem { Content = "(the service's default)", Tag = "" });
            box.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (round != _loading) return;
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem { Content = "(default — couldn't load: " + ex.Message + ")", Tag = "" });
            box.SelectedIndex = 0;
        }
    }

    private static string Picked(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void UpdateHints()
    {
        if (Provider is not { } p) return;
        var info = p.Info;
        _prefixHint.Text = p.Key switch
        {
            FastmailProvider.ProviderKey => FastmailProvider.CleanPrefix(_prefix.Text) is { Length: > 0 } c
                ? $"Fastmail makes it start with \"{c}\" (a-z, 0-9 and _ only), then adds random characters."
                : "Leave empty for a random address. Fastmail allows a-z, 0-9 and _.",
            SimpleLoginProvider.ProviderKey => "With a prefix you get prefix + the suffix chosen below; empty gives a random alias.",
            _ when info.Has(Features.CustomName) => "Exactly name@domain (a–z, 0–9, dot, dash, underscore). Empty = a random, unguessable name.",
            _ => $"{p.Name} always makes a random address.",
        };
        var sendNote = p.SendMode switch
        {
            SendMode.Direct => "You can send straight from this address.",
            SendMode.ReverseAlias => $"Mail you send from it goes out through {p.Name} (reverse alias), from your Fastmail account.",
            SendMode.Service => $"Mail you send from it goes out through {p.Name} itself.",
            _ => $"{p.Name} can't send to new contacts from it.",
        };
        _folderHint.Text = info.Has(Features.Inbox) ? $"Its mail stays at {p.Name} and is read here. {sendNote}"
            : _reg.Mail == null ? "Folders need a Fastmail API token (Settings). " + sendNote
            : $"Folder: {AppSettings.Current.RouteParent}/<address>. Mail to it is moved there from the Inbox (when it arrives in this Fastmail account). {sendNote}";
    }

    private async Task CreateAsync()
    {
        if (Provider is not { } p) return;
        _error.Text = "";
        _create.IsEnabled = false;
        _create.Content = "Creating…";
        try
        {
            var req = new CreateRequest { Description = _desc.Text, Site = _site.Text, Prefix = _prefix.IsEnabled ? _prefix.Text : "", Domain = Picked(_domain), ForwardTo = Picked(_forward) };
            if (_route.IsChecked == true && _route.Visibility == Visibility.Visible)
            {
                var (alias, folderError) = await _reg.CreateAsync(p, req);
                Created = alias; FolderError = folderError;
                if (folderError == null) { try { await _reg.SweepAsync(); } catch (Exception ex) when (ex is not OperationCanceledException) { FolderError = ex.Message; } }
            }
            else
            {
                Created = await p.CreateAsync(req);
                _reg.Aliases.Insert(0, Created);
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
            _create.IsEnabled = true;
            _create.Content = "Create";
        }
    }
}
