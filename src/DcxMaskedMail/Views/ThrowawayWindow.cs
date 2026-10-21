using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;

namespace DcxMail.Views;

/// <summary>
/// The live inbox of one throwaway address: the address to copy, a countdown, extend / keep / burn, and the mail that
/// arrives — checked every few seconds — with any verification codes and confirmation links pulled out to copy or open.
/// </summary>
public sealed class ThrowawayWindow : Window
{
    private readonly ProviderRegistry _reg;
    private readonly string _email;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _poll = new();
    private readonly TextBlock _countdown = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel _found = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(1), MinHeight = 120 };
    private readonly TextBox _preview = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8), BorderThickness = new Thickness(1) };
    private readonly HashSet<string> _seen = [];
    private bool _polling, _ending;
    private int _newCount;

    public ThrowawayWindow(ProviderRegistry reg, string email)
    {
        _reg = reg;
        _email = email;
        Title = "Throwaway — " + email;
        Width = 760; Height = 640; MinWidth = 560; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "FsBase");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "⏱  Throwaway address", Style = (Style)Application.Current.Resources["H2"] });
        var addrRow = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var copy = new Button { Content = "⧉ Copy", Style = (Style)Application.Current.Resources["PrimaryButton"], Margin = new Thickness(10, 0, 0, 0) };
        copy.Click += (_, _) => _status.Text = ClipboardHelper.Say(ClipboardHelper.TrySetText(_email), "the address");
        DockPanel.SetDock(copy, Dock.Right);
        addrRow.Children.Add(copy);
        var addr = new TextBox { Text = email, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        addr.SetResourceReference(FontFamilyProperty, "MonoFont");
        addr.SetResourceReference(FontSizeProperty, "FsValue");
        addr.SetResourceReference(ForegroundProperty, "Accent");
        addrRow.Children.Add(addr);
        head.Children.Add(addrRow);

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _countdown.Margin = new Thickness(0, 0, 14, 4);
        _countdown.SetResourceReference(FontFamilyProperty, "MonoFont");
        actions.Children.Add(_countdown);
        void Act(string text, string tip, Action a, string? style = null)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.Resources[style ?? "SmallButton"], Margin = new Thickness(0, 0, 6, 4) };
            b.Click += (_, _) => a();
            actions.Children.Add(b);
        }
        Act("+1 hour", "Expire one hour later", () => Throwaways.Extend(_email, TimeSpan.FromHours(1)));
        Act("+1 day", "Expire one day later", () => Throwaways.Extend(_email, TimeSpan.FromDays(1)));
        Act("Keep it", "Make it a normal masked address that never expires", () =>
        {
            if (!Dialogs.Confirm("Keep this address", $"{_email} will no longer expire. It stays in your masked addresses with its folder.", "Keep it")) return;
            Throwaways.Keep(_email);
            Close();
        });
        Act("🔥 Burn now", "End it now: " + (AppSettings.Current.ThrowawayDelete ? "delete the address" : "disable the address") + (AppSettings.Current.ThrowawayCleanMail ? ", move its mail to Trash and remove its folder" : ""), async () => await BurnAsync(), "DangerButton");
        head.Children.Add(actions);

        head.Children.Add(new TextBlock { Text = "CODES & LINKS FROM THE NEWEST MESSAGE", Style = (Style)Application.Current.Resources["Label"] });
        head.Children.Add(_found);
        head.Children.Add(new TextBlock { Text = "MESSAGES", Style = (Style)Application.Current.Resources["Label"] });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FsMeta");
        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star) });
        _list.Background = Brushes.Transparent;
        _list.SelectionChanged += async (_, _) => await ShowSelectedAsync();
        grid.Children.Add(_list);
        _preview.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_preview, 1);
        grid.Children.Add(_preview);
        root.Children.Add(grid);
        Content = root;

        ShowFound([], []);
        _tick.Tick += (_, _) => UpdateCountdown();
        _poll.Interval = TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.ThrowawayPollSeconds, 5, 300));
        _poll.Tick += async (_, _) => await PollAsync();
        Throwaways.Changed += OnChanged;
        Activated += (_, _) => { _newCount = 0; Title = "Throwaway — " + _email; };
        Loaded += async (_, _) => { UpdateCountdown(); _tick.Start(); _poll.Start(); await PollAsync(); };
        Closed += (_, _) => { _tick.Stop(); _poll.Stop(); Throwaways.Changed -= OnChanged; };
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Ended elsewhere (expired, burned from the main window) or kept: nothing left to watch.
        if (Throwaways.Find(_email) == null && !_ending) Close();
        else UpdateCountdown();
    });

    private void UpdateCountdown()
    {
        if (Throwaways.Find(_email) is not { } t) { _countdown.Text = "not a throwaway any more"; return; }
        _countdown.Text = t.Expired ? "⏱ expired — ending…" : $"⏱ expires in {t.LeftText}  ({t.Expires.LocalDateTime:ddd HH:mm})";
        _countdown.SetResourceReference(TextBlock.ForegroundProperty, t.Left < TimeSpan.FromMinutes(5) ? "Warn" : "Fg");
    }

    private IMailSource? _source;

    private async Task PollAsync()
    {
        if (_polling) return;
        var alias = _reg.Aliases.FirstOrDefault(a => a.Email.Equals(_email, StringComparison.OrdinalIgnoreCase));
        _source = alias != null ? _reg.MailFor(alias) : null;
        if (_source == null)
        {
            _status.Text = _reg.Mail == null ? "Mail for this address arrives in your mailbox. Add a Fastmail API token in Settings to see it here."
                : "This address has no folder yet, so its mail stays in your Inbox.";
            return;
        }
        _polling = true;
        try
        {
            // A Fastmail folder: file its new Inbox mail first (whatever the general routing setting). A disposable
            // inbox is read straight from its service.
            if (_source is FastmailFolder folder && _reg.Mail != null)
                await _reg.Mail.SweepAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [_email] = folder.FolderId }, 100);
            var (items, reported) = await _source.ListAsync(0);
            var total = Math.Max(reported, items.Count);   // the total is only a hint from the server
            var keep = (_list.SelectedItem as ListBoxItem)?.Tag as string;
            var fresh = items.Where(i => !_seen.Contains(i.Id)).ToList();
            bool first = _seen.Count == 0;
            foreach (var i in items) _seen.Add(i.Id);
            _list.Items.Clear();
            foreach (var m in items) _list.Items.Add(Row(m));
            if (fresh.Count > 0 && !first)
            {
                _newCount += fresh.Count;
                if (!IsActive) Title = $"({_newCount} new) Throwaway — {_email}";
            }
            var target = fresh.Count > 0 && !first ? fresh[0].Id : keep ?? items.FirstOrDefault()?.Id;
            _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == target);
            _status.Text = $"{total} message(s) · checking every {(int)_poll.Interval.TotalSeconds} s · last check {DateTime.Now:HH:mm:ss}" + (_source.Attribution is { } credit ? " · " + credit : "");
            if (items.Count == 0) _preview.Text = "Waiting for mail… Give the address to the site, then watch for the message here.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _status.Text = "⚠ " + ex.Message; }
        finally { _polling = false; }
    }

    private static ListBoxItem Row(MessageSummary m)
    {
        var p = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
        var top = new DockPanel();
        var when = new TextBlock { Text = m.Received.LocalDateTime.ToString("HH:mm"), Margin = new Thickness(8, 0, 0, 0) };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(when, Dock.Right);
        top.Children.Add(when);
        top.Children.Add(new TextBlock { Text = m.FromText, FontWeight = m.Unread ? FontWeights.Bold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        p.Children.Add(top);
        p.Children.Add(new TextBlock { Text = m.Subject, TextTrimming = TextTrimming.CharacterEllipsis });
        return new ListBoxItem { Content = p, Tag = m.Id };
    }

    private string? _shown;

    private async Task ShowSelectedAsync()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not string id || _source is not { } source || id == _shown) return;
        _shown = id;
        try
        {
            var m = await source.GetAsync(id);
            if (_shown != id) return;
            var text = m.Text.Length > 0 ? m.Text : HtmlTools.ToPlainText(m.Html);
            _preview.Text = $"From: {string.Join(", ", m.From.Select(a => a.ToString()))}\nSubject: {m.Subject}\n{m.Sent.LocalDateTime:f}\n\n{text}";
            ShowFound(Throwaways.FindCodes(m.Subject, text), Throwaways.FindLinks(m.Html, text));
            await source.SetSeenAsync(id, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _preview.Text = "Could not open the message: " + ex.Message; }
    }

    private void ShowFound(List<string> codes, List<string> links)
    {
        _found.Children.Clear();
        foreach (var c in codes)
        {
            var b = new Button { Content = $"{c}   ⧉", ToolTip = "Copy this code", Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(12, 5, 12, 5) };
            b.SetResourceReference(FontFamilyProperty, "MonoFont");
            b.SetResourceReference(FontSizeProperty, "FsTitle");
            var code = c;
            b.Click += (_, _) => _status.Text = ClipboardHelper.Say(ClipboardHelper.TrySetText(code), $"code {code}");
            _found.Children.Add(b);
        }
        foreach (var l in links)
        {
            var host = Uri.TryCreate(l, UriKind.Absolute, out var u) ? u.Host : l;
            var link = l;
            var open = new Button { Content = $"↗ Open link ({host})", ToolTip = l, Style = (Style)Application.Current.Resources["SmallButton"], Margin = new Thickness(0, 0, 4, 4) };
            open.Click += (_, _) => WebHost.OpenExternal(link);
            var cp = new Button { Content = "⧉", ToolTip = "Copy the link", Style = (Style)Application.Current.Resources["SmallButton"], Margin = new Thickness(0, 0, 10, 4) };
            cp.Click += (_, _) => _status.Text = ClipboardHelper.Say(ClipboardHelper.TrySetText(link), "the link");
            _found.Children.Add(open); _found.Children.Add(cp);
        }
        if (_found.Children.Count == 0)
        {
            var none = new TextBlock { Text = "Nothing yet — verification codes and confirmation links show up here.", Margin = new Thickness(0, 2, 0, 4) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _found.Children.Add(none);
        }
    }

    private async Task BurnAsync()
    {
        var s = AppSettings.Current;
        var what = (s.ThrowawayDelete ? "The address is deleted (mail sent to it bounces)" : "The address is disabled") +
                   (s.ThrowawayCleanMail ? ", its mail goes to Trash and its folder is removed." : ".");
        if (!Dialogs.Confirm("Burn this throwaway", $"End {_email} now?\n\n{what}", "Burn it", danger: true)) return;
        _ending = true;
        try
        {
            var result = await Throwaways.BurnAsync(_reg, _email, "burned by you");
            (Application.Current.MainWindow as MainWindow)?.ShowStatus($"🔥 {_email}: {result}");
            Close();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ending = false;
            Dialogs.Error("Could not burn it", ex.Message + "\n\nIt will be tried again automatically.");
        }
    }
}
