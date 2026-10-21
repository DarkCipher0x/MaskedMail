using System.Net.Http;
using System.Text.Json.Nodes;

namespace DcxMail.Providers;

/// <summary>
/// ImprovMX — free email forwarding for your own domains (REST API v4, Basic auth "api:&lt;key&gt;"). Aliases are created,
/// listed, pointed somewhere else and deleted; each alias's delivery log is its activity. No notes, no off switch
/// (ImprovMX has neither), and receive only (its SMTP sending is outside this app).
/// </summary>
public sealed class ImprovMxProvider : RestProvider, IDomainChoice, IForwardChoice, IActivityProvider
{
    public const string ProviderKey = "improvmx";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "ImprovMX",
        Family = ServiceFamily.OwnDomain,
        Features = Features.List | Features.Create | Features.CustomName | Features.ChooseDomain | Features.ForwardTo
                   | Features.Delete | Features.Activity,
        SendMode = SendMode.None,
        Summary = "ImprovMX forwards mail for your own domains. Addresses are created, listed and deleted here, and each one's delivery log is shown. Receive only.",
        Setup = "ImprovMX → API → Generate API key. Your domain must already be added and verified at ImprovMX.",
        SetupUrl = "https://app.improvmx.com/api",
        Fields =
        [
            new("key", "API KEY", Secret: true),
            new("forward_to", "NEW ADDRESSES FORWARD TO", "Your real address (several: comma-separated)."),
            new("domains", "DOMAINS", "Leave empty to use every domain on the account.", Required: false),
        ],
        Create = (c, h) => new ImprovMxProvider(c["key"], c["forward_to"], c.List("domains"), h),
    });

    private readonly string _forwardTo;
    private readonly List<string> _only;

    public ImprovMxProvider(string apiKey, string forwardTo, List<string> domains, HttpMessageHandler? handler = null, string baseUrl = "https://api.improvmx.com/v4")
        : base(baseUrl, handler)
    {
        Basic(Http, "api", apiKey.Trim());
        _forwardTo = forwardTo.Trim();
        _only = domains;
    }

    public override ServiceInfo Info => Service;

    private static string D(string s) => Uri.EscapeDataString(s);

    public async Task<List<string>> DomainsAsync(CancellationToken ct = default)
    {
        if (_only.Count > 0) return _only;
        var res = await SendAsync(HttpMethod.Get, "/domains?limit=200", null, ct);
        return (res?["domains"] as JsonArray ?? []).Select(d => S(d?["domain"])).Where(d => d.Length > 0).ToList();
    }

    public Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default) =>
        Task.FromResult(_forwardTo.Length > 0 ? new List<string> { _forwardTo } : []);

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<Alias>();
        foreach (var domain in await DomainsAsync(ct))
            for (int page = 1; page <= 100; page++)
            {
                var res = await SendAsync(HttpMethod.Get, $"/domains/{D(domain)}/aliases?limit=200&page={page}", null, ct);
                var list = (res?["aliases"] as JsonArray ?? []).OfType<JsonNode>().ToList();
                all.AddRange(list.Where(a => S(a["alias"]) is { Length: > 0 } n && n != "*").Select(a => ToAlias(a, domain)));
                if (list.Count < 200) break;
            }
        return all;
    }

    private static Alias ToAlias(JsonNode a, string domain) => new()
    {
        ProviderKey = ProviderKey,
        Id = domain + "/" + S(a["id"]),
        Email = $"{S(a["alias"])}@{domain}",
        Created = Date(a["created"] ?? a["created_at"]),
        ForwardsTo = S(a["forward"]),
    };

    private static (string Domain, string Id) Split(Alias a) => a.Id.IndexOf('/', StringComparison.Ordinal) is var i and > 0 ? (a.Id[..i], a.Id[(i + 1)..]) : (a.Email[(a.Email.LastIndexOf('@') + 1)..], a.Id);

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var domains = await DomainsAsync(ct);
        var domain = domains.FirstOrDefault(d => d.Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) ?? domains.FirstOrDefault()
            ?? throw new ProviderException("ImprovMX: no domain to create the address on.");
        var to = req.ForwardTo.Length > 0 ? req.ForwardTo : _forwardTo;
        if (to.Length == 0) throw new ProviderException("ImprovMX: set where new addresses forward to in Settings.");
        var res = await SendAsync(HttpMethod.Post, $"/domains/{D(domain)}/aliases", new JsonObject { ["alias"] = RandomName.Or(req.Prefix), ["forward"] = to }, ct);
        return ToAlias(res?["alias"] ?? throw new ProviderException("ImprovMX returned an empty response."), domain);
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        var (domain, id) = Split(alias);
        await SendAsync(HttpMethod.Delete, $"/domains/{D(domain)}/aliases/{D(id)}", null, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }

    /// <summary>The address's latest deliveries from ImprovMX's log: when, the final status, sender, subject.</summary>
    public async Task<List<ActivityEntry>> ActivityAsync(Alias alias, CancellationToken ct = default)
    {
        var (domain, _) = Split(alias);
        var local = alias.Email[..alias.Email.LastIndexOf('@')];
        var res = await SendAsync(HttpMethod.Get, $"/domains/{D(domain)}/logs/{D(local)}", null, ct);
        return (res?["logs"] as JsonArray ?? []).OfType<JsonNode>().Select(l => new ActivityEntry(
            Date(l["created"] ?? l["created_at"]) ?? DateTimeOffset.MinValue,
            S((l["events"] as JsonArray)?.LastOrDefault()?["status"]).ToLowerInvariant() is { Length: > 0 } st ? st : "received",
            Who(l["sender"]), Who(l["forward"] ?? l["recipient"]), S(l["subject"]))).ToList();
    }

    private static string Who(JsonNode? n) => n is JsonObject o ? S(o["email"]) : S(n);
}
