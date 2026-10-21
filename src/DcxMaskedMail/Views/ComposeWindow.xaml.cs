using System.ComponentModel;
using System.IO;
using System.Net.Mail;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;

namespace DcxMail.Views;

/// <summary>The HTML composer: fonts, sizes, colours, lists, alignment, links, inline images (insert, paste or drag)
/// and attachments, sending from a masked address or any Fastmail identity.</summary>
public partial class ComposeWindow : Window
{
    public enum Mode { New, Reply, ReplyAll, Forward }

    private sealed class FromOption
    {
        public required string Label { get; init; }
        public required string Email { get; init; }
        public Alias? Alias { get; init; }
        public override string ToString() => Label;
    }

    private readonly ProviderRegistry _reg;
    private readonly MailService _mail;
    private readonly Mode _mode;
    private readonly MessageDetail? _original;
    private readonly Alias? _preferred;
    private readonly List<OutgoingFile> _files = [];
    private readonly HashSet<string> _keepAsIs = new(StringComparer.OrdinalIgnoreCase);
    private List<(string Id, string Name, string Email)> _identities = [];
    private string _initialHtml = "";
    private bool _ready, _dirty, _sent, _sending;
    private string _textColour = "#E53935", _highlight = "#FFF176";

    public event Action? Sent;

    public ComposeWindow(ProviderRegistry reg, Mode mode, Alias? from, MessageDetail? original)
    {
        InitializeComponent();
        _reg = reg;
        _mail = reg.Mail!;
        _mode = mode;
        _original = original;
        _preferred = from;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        if (Application.Current.MainWindow is { IsVisible: true } mw) Owner = mw; else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        foreach (var f in new[] { "Segoe UI", "Arial", "Calibri", "Cambria", "Georgia", "Helvetica", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana", "Consolas", "Courier New", "Comic Sans MS" })
            FontBox.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f) });
        foreach (var (label, v) in new[] { ("Tiny", 1), ("Small", 2), ("Normal", 3), ("Large", 4), ("Larger", 5), ("Huge", 6), ("Huger", 7) })
            SizeBox.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        SuppressToolbar(() =>
        {
            FontBox.SelectedItem = FontBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == AppSettings.Current.DefaultFont) ?? FontBox.Items[0];
            SizeBox.SelectedItem = SizeBox.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == Math.Clamp(AppSettings.Current.DefaultFontSize, 1, 7));
        });

        Prefill();
        Loaded += async (_, _) => { await LoadSendersAsync(); await InitEditorAsync(); };
        PreviewKeyDown += (_, e) =>
        {
            var mods = System.Windows.Input.Keyboard.Modifiers;
            if (e.Key == System.Windows.Input.Key.Enter && mods == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; OnSend(this, e); }
            else if (e.Key == System.Windows.Input.Key.Enter && mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)) { e.Handled = true; OnQueue(this, e); }
        };
    }

    /// <summary>Opens a queued email for editing; "Add to queue" then updates it in place, "Send" sends it and removes it.</summary>
    public ComposeWindow(ProviderRegistry reg, QueuedEmail queued) : this(reg, Mode.New, null, null)
    {
        _queued = queued;
        Title = "Queued email — DarkCipher MaskedMail";
        _preferredEmail = queued.FromEmail;
        ToBox.Text = string.Join(", ", queued.To);
        if (queued.Cc.Count > 0) { CcBox.Text = string.Join(", ", queued.Cc); ShowCc(); }
        foreach (var a in queued.KeepAsIs) _keepAsIs.Add(a);
        SubjectBox.Text = queued.Subject;
        CopiesBox.Text = queued.Copies.ToString();
        _initialHtmlOverride = queued.Html;
        foreach (var f in queued.Attachments) AddFile(f);
        QueueBtn.Content = "⏸  Update in queue";
        _dirty = false;
    }

    private QueuedEmail? _queued;
    private string? _preferredEmail, _initialHtmlOverride;

    public void SetTo(string address) { ToBox.Text = address; }

    // ---------- prefill ----------

    private void Prefill()
    {
        if (_original is not { } o) { Title = "New email — DarkCipher MaskedMail"; return; }
        var mine = _reg.Aliases.Select(a => a.Email).Concat(_identities.Select(i => i.Email)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        switch (_mode)
        {
            case Mode.Reply:
            case Mode.ReplyAll:
                var to = (o.ReplyTo.Count > 0 ? o.ReplyTo : o.From).Select(a => a.Email).ToList();
                var cc = new List<string>();
                if (_mode == Mode.ReplyAll)
                {
                    to.AddRange(o.To.Select(a => a.Email).Where(e => !mine.Contains(e)));
                    cc.AddRange(o.Cc.Select(a => a.Email).Where(e => !mine.Contains(e)));
                }
                // These came from the delivered message: for SimpleLogin/addy.io they are already reverse aliases.
                foreach (var a in to.Concat(cc)) _keepAsIs.Add(a);
                ToBox.Text = string.Join(", ", to.Distinct(StringComparer.OrdinalIgnoreCase));
                if (cc.Count > 0) { CcBox.Text = string.Join(", ", cc.Distinct(StringComparer.OrdinalIgnoreCase)); ShowCc(); }
                SubjectBox.Text = o.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? o.Subject : "Re: " + o.Subject;
                break;
            case Mode.Forward:
                SubjectBox.Text = o.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? o.Subject : "Fwd: " + o.Subject;
                break;
        }
    }

    private async Task<string> QuotedOriginalAsync()
    {
        if (_original is not { } o) return "";
        var detail = o;
        // Inline images of the original travel with the quote.
        var cids = o.Attachments.Where(a => a.Cid != null && a.Size < 8_000_000 && o.Html.Contains("cid:" + a.Cid, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cids.Count > 0)
        {
            var parts = new Dictionary<string, (string, byte[])>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cids)
                try { parts[c.Cid!] = (c.Type, await _mail.Jmap.DownloadAsync(c.BlobId, c.Type, c.Name)); } catch { }
            detail = new MessageDetail
            {
                Id = o.Id, Subject = o.Subject, From = o.From, To = o.To, Cc = o.Cc, Sent = o.Sent, Text = o.Text,
                Html = HtmlTools.ResolveCids(o.Html, parts), Attachments = o.Attachments,
            };
        }
        if (_mode == Mode.Forward)
        {
            foreach (var a in o.Attachments.Where(a => !(a.Cid != null && cids.Contains(a))))
                try { AddFile(new OutgoingFile { Name = a.Name, Type = a.Type, Data = await _mail.Jmap.DownloadAsync(a.BlobId, a.Type, a.Name) }); }
                catch (Exception ex) { SendStatus.Text = $"⚠ could not include {a.Name}: {ex.Message}"; }
        }
        return HtmlTools.Quote(detail, _mode == Mode.Forward);
    }

    // ---------- senders ----------

    private async Task LoadSendersAsync()
    {
        try { _identities = await _mail.IdentitiesAsync(); }
        catch (Exception ex) { SendStatus.Text = "⚠ could not load sending identities: " + ex.Message; }

        var options = new List<FromOption>();
        foreach (var a in _reg.Aliases.Where(a => a.Enabled && _reg.Get(a.ProviderKey)?.SendMode != SendMode.None))
            options.Add(new FromOption { Label = $"{a.Email}   ·   {a.ProviderName} masked{(a.Description.Length > 0 ? "   ·   " + a.Description : "")}", Email = a.Email, Alias = a });
        foreach (var i in _identities.Where(i => !i.Email.StartsWith('*') && options.All(o => !o.Email.Equals(i.Email, StringComparison.OrdinalIgnoreCase))))
            options.Add(new FromOption { Label = $"{i.Email}   ·   your Fastmail address{(i.Name.Length > 0 ? " (" + i.Name + ")" : "")}", Email = i.Email });
        FromBox.ItemsSource = options;

        var pick = _preferred != null ? options.FirstOrDefault(o => o.Alias == _preferred)
                 : _preferredEmail != null ? options.FirstOrDefault(o => o.Email.Equals(_preferredEmail, StringComparison.OrdinalIgnoreCase)) : null;
        if (_preferredEmail != null && pick == null) SendStatus.Text = $"{_preferredEmail} can no longer send; pick another sender.";
        if (_preferred != null && pick == null)
            SendStatus.Text = _reg.Get(_preferred.ProviderKey)?.SendMode == SendMode.None
                ? $"{_preferred.ProviderName} cannot send from {_preferred.Email}; pick another sender."
                : $"{_preferred.Email} is disabled; pick another sender.";
        FromBox.SelectedItem = pick ?? options.FirstOrDefault(o => o.Alias == null) ?? options.FirstOrDefault();
    }

    private FromOption? From => FromBox.SelectedItem as FromOption;

    private void OnFromChanged(object sender, SelectionChangedEventArgs e)
    {
        if (From is not { } f) { FromHint.Text = ""; return; }
        var p = f.Alias != null ? _reg.Get(f.Alias.ProviderKey) : null;
        FromHint.Text = p?.SendMode switch
        {
            SendMode.Direct => "Sent straight from this masked address. Replies come back to it and are filed in its folder.",
            SendMode.ReverseAlias => $"Sent from {RealSender(f.Alias!)} through {p.Name}: each recipient gets a reverse-alias address, so they only ever see {f.Email}.",
            SendMode.Service => $"Sent from {f.Email} by {p.Name} itself (its outbound mail). No copy is kept in Fastmail's Sent folder.",
            _ => "Sent from your own Fastmail address (recipients see it).",
        };
        if (IsLoaded) Title = (_mode == Mode.New ? "New email" : SubjectBox.Text) + " — from " + f.Email;
    }

    private string RealSender(Alias a) => Outbox.RealSender(a, _identities, _reg);

    // ---------- editor ----------

    private async Task InitEditorAsync()
    {
        try
        {
            await WebHost.InitAsync(Editor, scripts: true);
            Editor.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Editor.CoreWebView2.NavigationStarting += (_, e) => { if (_ready) e.Cancel = true; };
            Editor.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            Editor.CoreWebView2.WebMessageReceived += OnEditorMessage;
            _initialHtml = _initialHtmlOverride ?? await QuotedOriginalAsync();
            Editor.NavigateToString(WebHost.EditorPage());
        }
        catch (Exception ex)
        {
            SendStatus.Text = "⚠ the editor needs the Microsoft Edge WebView2 Runtime: " + ex.Message;
        }
    }

    private async void OnEditorMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = e.TryGetWebMessageAsString();
        switch (msg)
        {
            case "ready":
                _ready = true;
                await Js($"setDefaults({Q(AppSettings.Current.DefaultFont)}, {AppSettings.Current.DefaultFontSize})");
                if (_initialHtml.Length > 0) await Js($"setHtml({Q(_initialHtml)})");
                _dirty = false;
                if (_mode is Mode.Reply or Mode.ReplyAll) { await Js("focusStart()"); Editor.Focus(); }
                else if (ToBox.Text.Length > 0) { await Js("focusStart()"); Editor.Focus(); }
                else ToBox.Focus();
                return;
            case "dirty": _dirty = true; return;
            case "send": OnSend(this, new RoutedEventArgs()); return;
            case "link": OnLink(this, new RoutedEventArgs()); return;
        }
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var r = doc.RootElement;
            switch (r.GetProperty("t").GetString())
            {
                case "state":
                    BoldBtn.IsChecked = r.GetProperty("b").GetBoolean();
                    ItalicBtn.IsChecked = r.GetProperty("i").GetBoolean();
                    UnderBtn.IsChecked = r.GetProperty("u").GetBoolean();
                    StrikeBtn.IsChecked = r.GetProperty("s").GetBoolean();
                    UlBtn.IsChecked = r.GetProperty("ul").GetBoolean();
                    OlBtn.IsChecked = r.GetProperty("ol").GetBoolean();
                    break;
                case "img":
                    var on = r.GetProperty("on").GetBoolean() ? Visibility.Visible : Visibility.Collapsed;
                    ImgSmall.Visibility = ImgHalf.Visibility = ImgFull.Visibility = on;
                    break;
                case "notimage":
                    SendStatus.Text = $"{r.GetProperty("name").GetString()} is not an image — use 📎 to attach it.";
                    break;
                case "toobig":
                    SendStatus.Text = $"{r.GetProperty("name").GetString()} is over 15 MB — attach it with 📎 instead.";
                    break;
            }
        }
        catch (JsonException) { }
    }

    private async Task<string> Js(string script) => _ready ? await Editor.CoreWebView2.ExecuteScriptAsync(script) : "null";
    private static string Q(string s) => JsonSerializer.Serialize(s);

    private bool _suppress;
    private void SuppressToolbar(Action a) { _suppress = true; try { a(); } finally { _suppress = false; } }

    private async void OnCmd(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var parts = tag.Split(':', 2);
        await Js(parts.Length == 2 ? $"cmd({Q(parts[0])}, {Q(parts[1])})" : $"cmd({Q(tag)})");
    }

    private async void OnFont(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || FontBox.SelectedItem is not ComboBoxItem { Content: string f }) return;
        await Js($"cmd('fontName', {Q(f)})");
    }

    private async void OnSize(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || SizeBox.SelectedItem is not ComboBoxItem { Tag: int s }) return;
        await Js($"cmd('fontSize', '{s}')");
    }

    private static readonly string[] Swatches =
    {
        "#000000", "#434343", "#666666", "#999999", "#CCCCCC", "#FFFFFF",
        "#E53935", "#FB8C00", "#FDD835", "#43A047", "#00ACC1", "#1E88E5",
        "#3949AB", "#8E24AA", "#D81B60", "#6D4C41", "#36D2CB", "#0B6E69",
        "#FFCDD2", "#FFE0B2", "#FFF176", "#C8E6C9", "#B2EBF2", "#BBDEFB",
    };

    private void PickColour(FrameworkElement anchor, Action<string> picked)
    {
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(6) };
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        foreach (var hex in Swatches)
        {
            var b = new Button { Width = 22, Height = 22, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(2), ToolTip = hex,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) };
            b.Click += (_, _) => { popup.IsOpen = false; picked(hex); };
            grid.Children.Add(b);
        }
        var border = new Border { Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        popup.Child = border;
        popup.IsOpen = true;
    }

    private void OnTextColour(object sender, RoutedEventArgs e) => PickColour((FrameworkElement)sender, async hex =>
    {
        _textColour = hex; ColourBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        await Js($"cmd('foreColor', {Q(_textColour)})");
    });

    private void OnHighlight(object sender, RoutedEventArgs e) => PickColour((FrameworkElement)sender, async hex =>
    {
        _highlight = hex; HighlightBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        await Js($"cmd('hiliteColor', {Q(_highlight)})");
    });

    private async void OnLink(object sender, RoutedEventArgs e)
    {
        var url = Dialogs.Prompt("Insert link", "Web address (https://…) or email address:", "https://");
        if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
        url = url.Trim();
        if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            url = url.Contains('@') && !url.Contains('/') ? "mailto:" + url : "https://" + url;
        await Js($"link({Q(url)})");
    }

    private async void OnImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Insert image", Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var path in dlg.FileNames)
        {
            var info = new FileInfo(path);
            if (info.Length > 15 * 1024 * 1024) { SendStatus.Text = $"{info.Name} is over 15 MB — attach it with 📎 instead."; continue; }
            var type = MimeOf(path);
            if (!type.StartsWith("image/")) { SendStatus.Text = $"{info.Name} is not an image."; continue; }
            var data = await File.ReadAllBytesAsync(path);
            await Js($"insertImage({Q($"data:{type};base64,{Convert.ToBase64String(data)}")}, {Q(info.Name)})");
        }
    }

    private async void OnImgSize(object sender, RoutedEventArgs e) => await Js($"imgWidth({(string)((Button)sender).Tag})");

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Attach files", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var path in dlg.FileNames)
            AddFile(new OutgoingFile { Name = Path.GetFileName(path), Type = MimeOf(path), Data = File.ReadAllBytes(path) });
    }

    private void AddFile(OutgoingFile f)
    {
        _files.Add(f);
        _dirty = true;
        var chip = new Button { Content = $"📎 {f.Name}  ({f.Data.Length / 1024.0:0} KB)  ×", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Remove this attachment" };
        chip.Click += (_, _) => { _files.Remove(f); AttachList.Children.Remove(chip); };
        AttachList.Children.Add(chip);
        var total = _files.Sum(x => (long)x.Data.Length);
        if (total > 45L * 1024 * 1024) SendStatus.Text = $"⚠ attachments total {total / 1024.0 / 1024.0:0} MB; Fastmail and many receivers reject large mail.";
    }

    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".csv" => "text/csv", ".htm" or ".html" => "text/html", ".zip" => "application/zip",
        ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint", ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".ics" => "text/calendar", ".json" => "application/json", ".xml" => "application/xml", ".mp3" => "audio/mpeg", ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };

    private void OnShowCc(object sender, RoutedEventArgs e) => ShowCc();
    private void ShowCc() { CcLabel.Visibility = CcBox.Visibility = Visibility.Visible; CcToggle.Visibility = Visibility.Collapsed; }
    private void OnSubjectChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) _dirty = true; }

    // ---------- send ----------

    public static List<string> ParseAddresses(string text, out List<string> bad) => Addresses.Parse(text, out bad);

    /// <summary>Checks the form and turns it into queue items: one, or one per recipient when "separate copy" is ticked.
    /// Returns null (with the reason shown) when something is missing.</summary>
    private async Task<List<QueuedEmail>?> CollectAsync(string verb)
    {
        if (!_ready) return null;
        if (From is not { } from) { SendStatus.Text = "Pick who it is from."; return null; }
        var to = ParseAddresses(ToBox.Text, out var badTo);
        var cc = ParseAddresses(CcBox.Text, out var badCc);
        if (badTo.Concat(badCc).Any()) { SendStatus.Text = "Not an email address: " + string.Join(", ", badTo.Concat(badCc)); return null; }
        if (to.Count == 0) { SendStatus.Text = "Add at least one recipient."; ToBox.Focus(); return null; }
        if (!int.TryParse(CopiesBox.Text.Trim(), out var copies) || copies < 1 || copies > 50) { SendStatus.Text = "Copies must be a number from 1 to 50."; CopiesBox.Focus(); return null; }
        if (SubjectBox.Text.Trim().Length == 0 && !Dialogs.Confirm("No subject", $"{verb} without a subject?", verb)) return null;

        var item = new QueuedEmail
        {
            FromEmail = from.Email,
            AliasProvider = from.Alias?.ProviderKey,
            To = to, Cc = cc,
            KeepAsIs = [.. _keepAsIs],
            Subject = SubjectBox.Text.Trim(),
            Html = JsonSerializer.Deserialize<string>(await Js("cleanHtml()")) ?? "",
            Attachments = [.. _files],
            InReplyTo = _mode is Mode.Reply or Mode.ReplyAll ? _original?.MessageId : _queued?.InReplyTo,
            References = _original?.References ?? _queued?.References ?? [],
            Copies = copies,
        };
        if (_queued != null) item.Id = _queued.Id;
        if (SplitBox.IsChecked != true || to.Count + cc.Count < 2) return [item];
        return to.Concat(cc).Distinct(StringComparer.OrdinalIgnoreCase).Select((p, i) =>
        {
            var c = item.Clone();
            if (i == 0 && _queued != null) c.Id = _queued.Id;
            c.To = [p]; c.Cc = [];
            return c;
        }).ToList();
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        var items = await CollectAsync("Send");
        if (items == null) return;
        int total = items.Sum(i => i.Copies);
        if (total > 1 && !Dialogs.Confirm("Send several emails", $"This sends {total} emails ({items.Count} recipient group(s) × copies), {AppSettings.Current.QueueDelaySeconds} s apart.", "Send")) return;

        _sending = true;
        SendBtn.IsEnabled = QueueBtn.IsEnabled = false;
        try
        {
            var identities = _identities.Count > 0 ? _identities : await _mail.IdentitiesAsync();
            var delay = TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.QueueDelaySeconds, 0, 600));
            for (int i = 0; i < items.Count; i++)
            {
                SendStatus.Text = items.Count > 1 ? $"sending {i + 1} of {items.Count}…" : "sending…";
                if (i > 0 && delay > TimeSpan.Zero) await Task.Delay(delay);
                await Outbox.SendItemAsync(items[i], _reg, identities, delay);
            }
            if (_queued != null) Outbox.Remove(_queued.Id);
            _sent = true;
            Sent?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            var done = items.Count(i => i.SentCopies >= i.Copies);
            SendStatus.Text = "✗ " + ex.Message;
            if (done > 0 || items.Any(i => i.SentCopies > 0))
            {
                // Some went out: keep only what is left, in the queue, so nothing is sent twice by trying again.
                var left = items.Where(i => i.SentCopies < i.Copies).ToList();
                foreach (var l in left) { l.Copies -= l.SentCopies; l.SentCopies = 0; l.LastError = ex.Message; Outbox.Put(l); }
                if (_queued != null && left.All(l => l.Id != _queued.Id)) Outbox.Remove(_queued.Id);
                _sent = true;
                Dialogs.Error("Partly sent", $"{ex.Message}\n\nWhat was not sent yet ({left.Sum(l => l.Copies)} email(s)) is in the queue.");
                Close();
            }
            else Dialogs.Error("Not sent", ex.Message);
        }
        finally
        {
            _sending = false;
            SendBtn.IsEnabled = QueueBtn.IsEnabled = true;
        }
    }

    private async void OnQueue(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        var items = await CollectAsync("Queue");
        if (items == null) return;
        if (_queued != null && items.All(i => i.Id != _queued.Id)) Outbox.Remove(_queued.Id);
        foreach (var i in items) Outbox.Put(i);
        _sent = true;
        Queued?.Invoke(items.Count);
        Close();
    }

    public event Action<int>? Queued;

    private void OnDiscard(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_sent || App.ForceExit || !_dirty && _files.Count == 0) return;
        if (!Dialogs.Confirm("Discard this email?", "It has not been sent and will be lost.", "Discard", danger: true)) e.Cancel = true;
    }
}
