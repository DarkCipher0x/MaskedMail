using System.Net.Http;
using DcxMail.Mail;
using DcxMail.Services;

namespace DcxMail.Providers;

/// <summary>
/// Builds the configured providers from settings and holds the app-wide state: the Fastmail mail connection, every
/// alias from every service, and the routing (one folder per alias under the routing parent, Inbox mail moved there).
/// </summary>
public sealed class ProviderRegistry : IDisposable
{
    public static string NameOf(string key) => ServiceCatalog.Find(key)?.Name ?? key;

    private readonly HttpMessageHandler? _handler;
    private readonly List<IDisposable> _owned = [];

    public JmapClient? Jmap { get; }
    public MailService? Mail { get; }
    public List<IAliasProvider> Providers { get; } = [];
    public List<Alias> Aliases { get; private set; } = [];
    /// <summary>Errors from the last refresh, per provider name.</summary>
    public Dictionary<string, string> Errors { get; } = [];

    public ProviderRegistry(AppSettings s, HttpMessageHandler? handler = null)
    {
        _handler = handler;
        if (s.FastmailToken.Trim().Length > 0)
        {
            Jmap = new JmapClient(s.FastmailSessionUrl, s.FastmailToken, handler);
            _owned.Add(Jmap);
            Mail = new MailService(Jmap);
            Providers.Add(new FastmailProvider(Jmap));
        }
        foreach (var info in ServiceCatalog.All)
        {
            var fields = s.ServiceFields(info.Key);
            if (info.Create == null || !info.IsConfigured(fields)) continue;
            var p = info.Create(new ServiceConfig(info, fields), handler);
            Providers.Add(p);
            if (p is IDisposable d) _owned.Add(d);
        }
    }

    public IAliasProvider? Get(string key) => Providers.FirstOrDefault(p => p.Key == key);

    /// <summary>Where an address's mail is read: its service's own inbox (disposable inboxes), else its Fastmail
    /// folder, else nowhere yet.</summary>
    public IMailSource? MailFor(Alias alias)
    {
        if (Get(alias.ProviderKey) is IInboxProvider inbox) return new ProviderInbox(inbox, Get(alias.ProviderKey)!, alias);
        if (Mail != null && AppSettings.Current.Folders.TryGetValue(alias.Email, out var folder))
            return new FastmailFolder(Mail, folder, AppSettings.Current.RouteParent + "/" + MailService.FolderName(alias.Email));
        return null;
    }

    /// <summary>Loads aliases from every provider in parallel; one failing service does not hide the others.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        Errors.Clear();
        var tasks = Providers.Select(async p =>
        {
            try { return await p.ListAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { lock (Errors) Errors[p.Name] = ex.Message; return []; }
        }).ToList();
        var lists = await Task.WhenAll(tasks);
        Aliases = lists.SelectMany(l => l).OrderByDescending(a => a.Created ?? DateTimeOffset.MinValue).ToList();
        MarkRouted();
    }

    public void MarkRouted()
    {
        foreach (var a in Aliases)
            a.Routed = AppSettings.Current.Folders.ContainsKey(a.Email) && !AppSettings.Current.RouteOff.Contains(a.Email);
    }

    /// <summary>Creates (or finds) &lt;RouteParent&gt;/&lt;address&gt; and remembers it for the sweep.</summary>
    public async Task<string> EnsureFolderAsync(Alias alias, CancellationToken ct = default)
    {
        if (Mail == null) throw new ProviderException("Folders need a Fastmail account: add your Fastmail API token in Settings.");
        var path = AppSettings.Current.RouteParent.Trim('/', ' ') + "/" + MailService.FolderName(alias.Email);
        var id = await Mail.EnsurePathAsync(path, ct);
        AppSettings.Current.Folders[alias.Email] = id;
        AppSettings.Current.RouteOff.Remove(alias.Email);
        AppSettings.Save();
        alias.Routed = true;
        return id;
    }

    /// <summary>Creates a new alias with the chosen provider, then its folder, then files any mail already waiting.</summary>
    public async Task<(Alias Alias, string? FolderError)> CreateAsync(IAliasProvider provider, CreateRequest req, CancellationToken ct = default)
    {
        var alias = await provider.CreateAsync(req, ct);
        Aliases.Insert(0, alias);
        string? folderError = null;
        if (AppSettings.Current.RoutingEnabled && Mail != null && !provider.Info.Has(Features.Inbox))   // disposable inboxes are read at their service
        {
            try { await EnsureFolderAsync(alias, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { folderError = ex.Message; }
        }
        return (alias, folderError);
    }

    /// <summary>The routing map for the sweep: address → folder id, minus the ones routed off.</summary>
    public Dictionary<string, string> RouteMap() =>
        AppSettings.Current.Folders.Where(kv => !AppSettings.Current.RouteOff.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (Mail == null || !AppSettings.Current.RoutingEnabled) return 0;
        var map = RouteMap();
        if (map.Count == 0) return 0;
        var moved = await Mail.SweepAsync(map, ct: ct);
        // Forget folders that were deleted in Fastmail (the sweep never moves mail into them; it checks the folder list
        // only when there is mail to move, so a quiet sweep is a single request).
        var gone = map.Where(kv => Mail.LastMissingFolders.Contains(kv.Value)).Select(kv => kv.Key).ToList();
        if (gone.Count > 0)
        {
            foreach (var g in gone) AppSettings.Current.Folders.Remove(g);
            AppSettings.Save();
            MarkRouted();
        }
        return moved;
    }

    public void Dispose() { foreach (var d in _owned) d.Dispose(); }
}
