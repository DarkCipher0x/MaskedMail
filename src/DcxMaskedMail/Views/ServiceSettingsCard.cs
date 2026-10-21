using System.Windows;
using System.Windows.Controls;
using DcxMail.Providers;
using static DcxMail.Views.SettingsUi;

namespace DcxMail.Views;

/// <summary>
/// One service's Settings card, built from what the service declares about itself (<see cref="ServiceInfo"/>): what
/// it is, any caution, the "use this service" tick for opt-in services, its fields, a connection test and a link to
/// where the key is made. Saving reads the values back.
/// </summary>
internal sealed class ServiceSettingsCard
{
    private readonly Dictionary<string, Func<string>> _values = [];
    private readonly CheckBox? _use;

    public ServiceInfo Info { get; }
    public UIElement[] Controls { get; }

    public ServiceSettingsCard(ServiceInfo info, IReadOnlyDictionary<string, string> saved)
    {
        Info = info;
        var c = new List<UIElement>();
        if (info.Caution != null) c.Add(Caution(info.Caution));
        if (info.OptIn)
        {
            _use = Check($"Use {info.Name}" + (info.Family == ServiceFamily.Disposable ? " (I've read the note above)" : ""));
            _use.IsChecked = saved.TryGetValue(ServiceInfo.EnabledField, out var on) && on == "true";
            c.Add(_use);
        }
        foreach (var f in info.Fields)
        {
            var value = saved.TryGetValue(f.Id, out var v) ? v : "";
            c.Add(Label(f.Label + (f.Required ? "" : "  (optional)")));
            if (f.Choices != null)
            {
                var box = Choice(f.Choices, value.Length > 0 ? value : f.Default);
                _values[f.Id] = () => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                c.Add(box);
            }
            else if (f.Secret)
            {
                var box = Pw(); box.Password = value;
                _values[f.Id] = () => box.Password.Trim();
                c.Add(box);
            }
            else
            {
                var box = Field(); box.Text = value.Length > 0 ? value : f.Default;
                _values[f.Id] = () => box.Text.Trim();
                c.Add(box);
            }
            if (f.Hint.Length > 0) c.Add(Hint(f.Hint));
        }
        c.Add(Hint("Set-up: " + info.Setup));
        c.Add(TestRow(TestAsync));
        if (info.SetupUrl != null) c.Add(LinkRow($"Open {info.Name}", info.SetupUrl));
        Controls = [.. c];
    }

    /// <summary>The values to save (empty when nothing is filled in, so an unused service leaves no trace).</summary>
    public Dictionary<string, string> Values()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, read) in _values)
            if (read() is { Length: > 0 } v && v != Info.Fields.First(f => f.Id == id).Default) d[id] = v;
        if (_use?.IsChecked == true) d[ServiceInfo.EnabledField] = "true";
        return d;
    }

    /// <summary>Talks to the service with the values as typed (not yet saved) and says what it found.</summary>
    private async Task<string> TestAsync()
    {
        var values = Values();
        if (Info.OptIn) values[ServiceInfo.EnabledField] = "true";
        if (!Info.IsConfigured(values)) return "Fill in the required fields first.";
        var p = Info.Create!(new ServiceConfig(Info, values), null);
        try
        {
            if (Info.Has(Features.List)) return $"✓ connected · {(await p.ListAsync()).Count} address(es)";
            if (p is IDomainChoice d) return $"✓ connected · domains: {string.Join(", ", (await d.DomainsAsync()).Take(6))}";
            return "✓ settings complete. This service can only be checked by creating an address.";
        }
        finally { (p as IDisposable)?.Dispose(); }
    }
}
