using System.Net.Http;
using System.Text.Json.Nodes;

namespace DcxMail.Providers;

/// <summary>
/// Migadu — hosted mail for your own domains (REST API v1, Basic auth: your Migadu login email and an API key). Its
/// aliases deliver to mailboxes on the same domain, so this is for addresses that land in a Migadu mailbox. Create,
/// list, point elsewhere and delete.
/// </summary>
public sealed class MigaduProvider : RestProvider, IDomainChoice, IForwardChoice
{
    public const string ProviderKey = "migadu";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "Migadu",
        Family = ServiceFamily.OwnDomain,
        Features = Features.List | Features.Create | Features.CustomName | Features.ChooseDomain | Features.ForwardTo | Features.Delete,
        SendMode = SendMode.None,
        Summary = "Migadu hosts mail for your own domains. Aliases are made for a Migadu mailbox on the same domain (Migadu's rule), so their mail lands there, not in Fastmail.",
        Setup = "Migadu admin → My Account → API Keys → create a key. Use the email you sign in to Migadu with.",
        SetupUrl = "https://admin.migadu.com/account/api/keys",
        Fields =
        [
            new("user", "MIGADU LOGIN EMAIL"),
            new("key", "API KEY", Secret: true),
            new("domains", "DOMAINS", "The domain(s) to manage, comma-separated."),
            new("forward_to", "NEW ALIASES DELIVER TO", "A mailbox address on the same domain."),
        ],
        Create = (c, h) => new MigaduProvider(c["user"], c["key"], c.List("domains"), c["forward_to"], h),
    });

    private readonly List<string> _domains;
    private readonly string _forwardTo;

    public MigaduProvider(string user, string apiKey, List<string> domains, string forwardTo, HttpMessageHandler? handler = null, string baseUrl = "https://api.migadu.com/v1")
        : base(baseUrl, handler)
    {
        Basic(Http, user.Trim(), apiKey.Trim());
        _domains = domains;
        _forwardTo = forwardTo.Trim();
    }

    public override ServiceInfo Info => Service;

    private static string D(string s) => Uri.EscapeDataString(s);

    public Task<List<string>> DomainsAsync(CancellationToken ct = default) => Task.FromResult(_domains.ToList());
    public Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default) => Task.FromResult(new List<string> { _forwardTo });

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<Alias>();
        foreach (var domain in _domains)
        {
            var res = await SendAsync(HttpMethod.Get, $"/domains/{D(domain)}/aliases", null, ct);
            var list = res as JsonArray ?? res?["address_aliases"] as JsonArray ?? res?["aliases"] as JsonArray ?? [];
            all.AddRange(list.OfType<JsonNode>().Select(a => ToAlias(a, domain)));
        }
        return all;
    }

    private static Alias ToAlias(JsonNode a, string domain)
    {
        var local = S(a["local_part"]);
        var dest = a["destinations"] is JsonArray arr ? string.Join(", ", arr.Select(S)) : S(a["destinations"]);
        return new Alias
        {
            ProviderKey = ProviderKey,
            Id = domain + "/" + local,
            Email = S(a["address"]) is { Length: > 0 } addr ? addr : $"{local}@{domain}",
            ForwardsTo = dest,
            Expires = B(a["expireable"], false) ? Date(a["expires_on"]) : null,
            Stats = B(a["is_internal"], false) ? "internal only" : "",
        };
    }

    private static (string Domain, string Local) Split(Alias a) => a.Id.IndexOf('/', StringComparison.Ordinal) is var i and > 0 ? (a.Id[..i], a.Id[(i + 1)..]) : ("", a.Id);

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var domain = _domains.FirstOrDefault(d => d.Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) ?? _domains.FirstOrDefault()
            ?? throw new ProviderException("Migadu: add your domain in Settings.");
        var to = req.ForwardTo.Length > 0 ? req.ForwardTo : _forwardTo;
        if (!to.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase))
            throw new ProviderException($"Migadu aliases can only deliver to a mailbox on {domain}.");
        var res = await SendAsync(HttpMethod.Post, $"/domains/{D(domain)}/aliases", new JsonObject { ["local_part"] = RandomName.Or(req.Prefix), ["destinations"] = to }, ct);
        return ToAlias(res ?? throw new ProviderException("Migadu returned an empty response."), domain);
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        var (domain, local) = Split(alias);
        await SendAsync(HttpMethod.Delete, $"/domains/{D(domain)}/aliases/{D(local)}", null, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }
}
