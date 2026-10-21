using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;

namespace DcxMail.Views;

/// <summary>
/// The outgoing queue. Every item can be edited in the composer, duplicated, given several copies, split into one copy
/// per recipient, skipped (unticked), reordered or removed; "Send all" sends the ticked items in order with a pause
/// between each. The queue is stored encrypted (outbox.dcx) and survives restarts.
/// </summary>
public sealed class OutboxWindow : Window
{
    private readonly ProviderRegistry _reg;
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(1) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _delay = new() { Width = 48, Padding = new Thickness(4, 3, 4, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly CheckBox _stop = new() { Content = "Stop at the first failure", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Button _sendAll = new() { Content = "➤  Send all", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _sendSel = new() { Content = "Send selected", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _cancel = new() { Content = "Stop", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) };
    private CancellationTokenSource? _cts;

    public event Action<string>? StatusChanged;

    public OutboxWindow(ProviderRegistry reg)
    {
        _reg = reg;
        Title = "Outgoing queue — DarkCipher MaskedMail";
        Width = 1000; Height = 620; MinWidth = 720; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "FsBase");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _sendAll.Style = (Style)Application.Current.Resources["PrimaryButton"];
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        // ---- list ----
        var gv = new GridView();
        var tick = new FrameworkElementFactory(typeof(CheckBox));
        tick.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(QueuedEmail.Enabled)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        tick.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((_, _) => { Outbox.Save(); Refresh(); }));
        tick.SetValue(ToolTipProperty, "Send this one with “Send all” (untick to skip it but keep it)");
        gv.Columns.Add(new GridViewColumn { Header = "Send", Width = 50, CellTemplate = new DataTemplate { VisualTree = tick } });
        gv.Columns.Add(new GridViewColumn { Header = "From", Width = 210, DisplayMemberBinding = new Binding(nameof(QueuedEmail.FromEmail)) });
        gv.Columns.Add(new GridViewColumn { Header = "To", Width = 220, DisplayMemberBinding = new Binding(nameof(QueuedEmail.ToText)) });
        gv.Columns.Add(new GridViewColumn { Header = "Subject", Width = 220, DisplayMemberBinding = new Binding(nameof(QueuedEmail.Subject)) });
        gv.Columns.Add(new GridViewColumn { Header = "Copies", Width = 80, DisplayMemberBinding = new Binding(nameof(QueuedEmail.CopiesText)) });
        gv.Columns.Add(new GridViewColumn { Header = "", Width = 50, DisplayMemberBinding = new Binding(nameof(QueuedEmail.AttachText)) });
        gv.Columns.Add(new GridViewColumn { Header = "Status", Width = 200, DisplayMemberBinding = new Binding(nameof(QueuedEmail.StatusText)) });
        _list.View = gv;
        _list.MouseDoubleClick += (_, _) => Edit();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) Remove(); };

        // ---- toolbar ----
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void Tool(string text, string tip, Action a)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.Resources["SmallButton"], Margin = new Thickness(0, 0, 6, 4) };
            b.Click += (_, _) => a();
            tools.Children.Add(b);
        }
        Tool("＋ New email…", "Write an email and add it to the queue", NewEmail);
        Tool("✎ Edit…", "Open the selected email in the composer (double-click works too)", Edit);
        Tool("⧉ Duplicate", "Add a copy of each selected email right after it", Duplicate);
        Tool("×n Copies…", "Send several identical copies of the selected email(s)", SetCopies);
        Tool("👥 One per recipient", "Split each selected email into one copy per person in To and Cc", Split);
        Tool("✉ Change sender…", "Send the selected email(s) from another address", ChangeSender);
        Tool("↑", "Move up (sent earlier)", () => MoveSel(-1));
        Tool("↓", "Move down (sent later)", () => MoveSel(1));
        Tool("☐ Skip / include", "Tick or untick the selected emails for “Send all”", ToggleSel);
        Tool("🗑 Remove", "Delete the selected emails from the queue (Del)", Remove);

        // ---- options + send bar ----
        _delay.Text = AppSettings.Current.QueueDelaySeconds.ToString();
        _stop.IsChecked = AppSettings.Current.QueueStopOnError;
        _delay.LostFocus += (_, _) => SaveOptions();
        _stop.Click += (_, _) => SaveOptions();
        var options = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        options.Children.Add(new TextBlock { Text = "PAUSE BETWEEN EMAILS (s)", Style = (Style)Application.Current.Resources["Label"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        options.Children.Add(_delay);
        options.Children.Add(_stop);
        _sendAll.Click += async (_, _) => await SendAsync(Outbox.Items.Where(i => i.Enabled).ToList());
        _sendSel.Click += async (_, _) => await SendAsync(Selected());
        _cancel.Click += (_, _) => _cts?.Cancel();
        var sendRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_cancel); right.Children.Add(_sendSel); right.Children.Add(_sendAll);
        // IsCancel only closes dialogs (ShowDialog); this window is shown modeless, so Close and Esc are wired by hand.
        var close = new Button { Content = "Close", Margin = new Thickness(0) };
        close.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_list.IsKeyboardFocusWithin) { e.Handled = true; Close(); } };
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        sendRow.Children.Add(right);
        sendRow.Children.Add(options);

        var root = new DockPanel { Margin = new Thickness(16, 14, 16, 14) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Outgoing queue", Style = (Style)Application.Current.Resources["H1"] });
        head.Children.Add(new TextBlock { Text = "Collect emails here (or several copies of one), then send them together in order. Queued emails are stored encrypted until they are sent.", Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 2, 0, 10) });
        head.Children.Add(tools);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var bottom = new StackPanel();
        bottom.Children.Add(sendRow);
        bottom.Children.Add(_status);
        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Outbox.Changed += OnChanged;
        Closed += (_, _) => { Outbox.Changed -= OnChanged; _cts?.Cancel(); };
        Closing += (_, e) => { if (_cts != null && !App.ForceExit && !Dialogs.Confirm("Stop sending?", "The queue is being sent. Stop after the current email?", "Stop")) e.Cancel = true; };
        Refresh();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var keep = Selected().Select(i => i.Id).ToHashSet();
        _list.ItemsSource = null;
        _list.ItemsSource = Outbox.Items;
        foreach (var i in Outbox.Items.Where(i => keep.Contains(i.Id))) _list.SelectedItems.Add(i);
        var total = Outbox.Items.Where(i => i.Enabled).Sum(i => Math.Max(1, i.Copies) - i.SentCopies);
        if (_cts == null) _status.Text = Outbox.Items.Count == 0 ? "The queue is empty. Use “Add to queue” in the composer, or ＋ New email." :
            $"{Outbox.Items.Count} item(s) · {total} email(s) will be sent by “Send all”" + (Outbox.Items.Any(i => !i.Enabled) ? $" · {Outbox.Items.Count(i => !i.Enabled)} skipped" : "");
        _sendAll.IsEnabled = _cts == null && total > 0;
        _sendSel.IsEnabled = _cts == null;
    }

    private List<QueuedEmail> Selected() => _list.SelectedItems.OfType<QueuedEmail>().ToList();

    private QueuedEmail? One()
    {
        var s = Selected();
        if (s.Count == 1) return s[0];
        Dialogs.Info("Queue", s.Count == 0 ? "Select an email in the queue first." : "Select just one email for this.");
        return null;
    }

    private void SaveOptions()
    {
        AppSettings.Current.QueueDelaySeconds = int.TryParse(_delay.Text, out var d) ? Math.Clamp(d, 0, 600) : 3;
        AppSettings.Current.QueueStopOnError = _stop.IsChecked == true;
        _delay.Text = AppSettings.Current.QueueDelaySeconds.ToString();
        AppSettings.Save();
    }

    private void NewEmail()
    {
        if (_reg.Mail == null) { Dialogs.Info("Fastmail needed", "Add your Fastmail API token in Settings first."); return; }
        new ComposeWindow(_reg, ComposeWindow.Mode.New, null, null) { Owner = this }.Show();
    }

    private void Edit()
    {
        if (One() is not { } q) return;
        if (_reg.Mail == null) { Dialogs.Info("Fastmail needed", "Add your Fastmail API token in Settings first."); return; }
        new ComposeWindow(_reg, q) { Owner = this }.Show();
    }

    private void Duplicate()
    {
        var sel = Selected();
        if (sel.Count == 0) { One(); return; }
        foreach (var q in sel) Outbox.Duplicate(q.Id);
    }

    private void SetCopies()
    {
        var sel = Selected();
        if (sel.Count == 0) { One(); return; }
        if (Dialogs.Prompt("Copies", "How many identical copies of the selected email(s) should be sent? (1–50)", sel[0].Copies.ToString()) is not { } t) return;
        if (!int.TryParse(t.Trim(), out var n) || n < 1 || n > 50) { Dialogs.Error("Copies", "Enter a number from 1 to 50."); return; }
        foreach (var q in sel) { q.Copies = Math.Max(n, q.SentCopies + 1); q.LastError = null; }
        Outbox.Save();
    }

    private void Split()
    {
        var sel = Selected();
        if (sel.Count == 0) { One(); return; }
        int made = 0;
        foreach (var q in sel) made += Outbox.SplitPerRecipient(q.Id);
        _status.Text = $"split into {made} email(s), one per recipient";
    }

    private void ChangeSender()
    {
        var sel = Selected();
        if (sel.Count == 0) { One(); return; }
        var choices = _reg.Aliases.Where(a => a.Enabled && _reg.Get(a.ProviderKey)?.SendMode != SendMode.None).Select(a => a.Email).ToList();
        if (Dialogs.Prompt("Change sender", "Send from (one of your masked addresses or Fastmail addresses):\n" + string.Join(", ", choices.Take(12)) + (choices.Count > 12 ? ", …" : ""), sel[0].FromEmail) is not { } from) return;
        from = from.Trim();
        var alias = _reg.Aliases.FirstOrDefault(a => a.Email.Equals(from, StringComparison.OrdinalIgnoreCase));
        foreach (var q in sel) { q.FromEmail = alias?.Email ?? from; q.AliasProvider = alias?.ProviderKey; q.LastError = null; }
        Outbox.Save();
    }

    private void MoveSel(int delta)
    {
        var sel = Selected();
        if (sel.Count != 1) { One(); return; }
        Outbox.Move(sel[0].Id, delta);
    }

    private void ToggleSel()
    {
        var sel = Selected();
        if (sel.Count == 0) { One(); return; }
        var on = !sel.All(q => q.Enabled);
        foreach (var q in sel) q.Enabled = on;
        Outbox.Save();
    }

    private void Remove()
    {
        var sel = Selected();
        if (sel.Count == 0) { One(); return; }
        if (!Dialogs.Confirm("Remove from queue", $"Delete {sel.Count} queued email(s)? They have not been sent.", "Remove", danger: true)) return;
        foreach (var q in sel) Outbox.Items.Remove(q);
        Outbox.Save();
    }

    private async Task SendAsync(List<QueuedEmail> items)
    {
        if (_cts != null) return;
        if (items.Count == 0) { Dialogs.Info("Queue", "Nothing to send: select emails, or tick them for “Send all”."); return; }
        SaveOptions();
        int total = items.Sum(i => Math.Max(1, i.Copies) - i.SentCopies);
        if (!Dialogs.Confirm("Send the queue", $"Send {total} email(s) now, {AppSettings.Current.QueueDelaySeconds} s apart?", "Send")) return;
        foreach (var q in items) q.LastError = null;
        _cts = new CancellationTokenSource();
        _cancel.Visibility = Visibility.Visible;
        Refresh();
        try
        {
            var progress = new Progress<string>(t => { _status.Text = t; StatusChanged?.Invoke(t); });
            var (sent, failed) = await Outbox.SendAllAsync(_reg, items, TimeSpan.FromSeconds(AppSettings.Current.QueueDelaySeconds), AppSettings.Current.QueueStopOnError, progress, _cts.Token);
            _status.Text = failed == 0 ? $"✓ sent {sent} email(s)" : $"sent {sent} email(s) · {failed} failed and stayed in the queue (see Status)";
        }
        catch (OperationCanceledException) { _status.Text = "stopped — what wasn't sent is still in the queue"; }
        catch (Exception ex) { _status.Text = "✗ " + ex.Message; Dialogs.Error("Queue", ex.Message); }
        finally
        {
            _cts.Dispose(); _cts = null;
            _cancel.Visibility = Visibility.Collapsed;
            var keep = _status.Text;
            Refresh();
            _status.Text = keep;
            StatusChanged?.Invoke(keep);
        }
    }
}
