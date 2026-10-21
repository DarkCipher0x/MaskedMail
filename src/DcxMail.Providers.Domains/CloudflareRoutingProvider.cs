using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace DcxMail.Providers;

/// <summary>
/// Cloudflare Email Routing — addresses on your own domains (zones on Cloudflare) are routing rules: "mail to X →
/// forward to Y". Free. Create, list, turn off, rename (the rule's name is the note) and delete. Mail can only be
/// forwarded to destination addresses you have verified with Cloudflare.
/// </summary>
public sealed class CloudflareRoutingProvider : RestProvider, IDomainChoice, IForwardChoice
{
    public const string ProviderKey = "cloudflare";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "Cloudflare Email Routing",
        Family = ServiceFamily.OwnDomain,
        Features = Features.List | Features.Create | Features.CustomName | Features.ChooseDomain | Features.ForwardTo
                   | Features.EditNote | Features.Disable | Features.Delete,
        SendMode = SendMode.None,
        Summary = "Free addresses on your own domains whose DNS is on Cloudflare: each one is an Email Routing rule that forwards to a verified address (your Fastmail address, say). Receive only.",
        Setup = "Cloudflare → My Profile → API Tokens → Create Token (custom) with: Zone · Zone · Read, Zone · Email Routing Rules · Edit, Account · Email Routing Addresses · Read. Limit it to the zones you want. Email Routing must be on for the domain, and the forward-to address verified.",
        SetupUrl = "https://dash.cloudflare.com/profile/api-tokens",
        Fields =
        [
            new("token", "API TOKEN", "Scoped to Email Routing only (see above) — never a Global API Key.", Secret: true),
            new("forward_to", "NEW ADDRESSES FORWARD TO", "A destination address already verified in Cloudflare Email Routing."),
            new("domains", "DOMAINS", "Leave empty to use every zone the token can see.", Required: false),
        ],
        Create = (c, h) => new CloudflareRoutingProvider(c["token"], c["forward_to"], c.List("domains"), h),
    });

    private readonly string _forwardTo;
    private readonly List<string> _only;
    private List<(string Id, string Name, string AccountId)>? _zones;

    public CloudflareRoutingProvider(string token, string forwardTo, List<string> domains, HttpMessageHandler? handler = null, string baseUrl = "https://api.cloudflare.com/client/v4")
        : base(baseUrl, handler)
    {
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _forwardTo = forwardTo.Trim();
        _only = domains;
    }

    public override ServiceInfo Info => Service;

    /// <summary>Cloudflare answers {success, errors, result, result_info}; a 200 can still be a failure.</summary>
    private async Task<JsonNode?> CallAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var res = await SendAsync(method, path, body, ct);
        if (res?["success"]?.GetValue<bool>() == false)
            throw new ProviderException("Cloudflare: " + string.Join("; ", (res["errors"] as JsonArray ?? []).Select(e => S(e?["message"]))));
        return res;
    }

    private async Task<List<JsonNode>> PagesAsync(string path, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        var sep = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        for (int page = 1; page <= 50; page++)
        {
            var res = await CallAsync(HttpMethod.Get, $"{path}{sep}per_page=50&page={page}", null, ct);
            all.AddRange((res?["result"] as JsonArray ?? []).OfType<JsonNode>());
            var total = res?["result_info"]?["total_pages"]?.GetValue<int>() ?? page;
            if (page >= total) break;
        }
        return all;
    }

    private async Task<List<(string Id, string Name, string AccountId)>> ZonesAsync(CancellationToken ct)
    {
        _zones ??= (await PagesAsync("/zones", ct))
            .Select(z => (S(z["id"]), S(z["name"]), S(z["account"]?["id"])))
            .Where(z => _only.Count == 0 || _only.Contains(z.Item2, StringComparer.OrdinalIgnoreCase)).ToList();
        return _zones;
    }

    public async Task<List<string>> DomainsAsync(CancellationToken ct = default) => (await ZonesAsync(ct)).Select(z => z.Name).ToList();

    /// <summary>The verified destination addresses (the configured one first).</summary>
    public async Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default)
    {
        var list = new List<string>();
        if (_forwardTo.Length > 0) list.Add(_forwardTo);
        foreach (var account in (await ZonesAsync(ct)).Select(z => z.AccountId).Where(a => a.Length > 0).Distinct())
        {
            try
            {
                foreach (var d in await PagesAsync($"/accounts/{account}/email/routing/addresses", ct))
                    if (S(d["verified"]).Length > 0 && !list.Contains(S(d["email"]), StringComparer.OrdinalIgnoreCase)) list.Add(S(d["email"]));
            }
            catch (ProviderException) { /* the token may not have the address-read scope; the configured one still works */ }
        }
        return list;
    }

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<Alias>();
        foreach (var zone in await ZonesAsync(ct))
            foreach (var r in await PagesAsync($"/zones/{zone.Id}/email/routing/rules", ct))
                if (ToAlias(r, zone.Id) is { } a) all.Add(a);
        return all;
    }

    /// <summary>A rule for one exact address (catch-all and worker rules are not addresses).</summary>
    private static Alias? ToAlias(JsonNode r, string zoneId)
    {
        var m = (r["matchers"] as JsonArray)?.FirstOrDefault(x => S(x?["type"]) == "literal" && S(x?["field"]) == "to");
        if (m == null) return null;
        var forward = (r["actions"] as JsonArray)?.FirstOrDefault(x => S(x?["type"]) == "forward");
        var id = S(r["id"]) is { Length: > 0 } i ? i : S(r["tag"]);
        return new Alias
        {
            ProviderKey = ProviderKey,
            Id = zoneId + "/" + id,
            Email = S(m["value"]),
            Enabled = B(r["enabled"], true),
            State = B(r["enabled"], true) ? "enabled" : "disabled",
            Description = S(r["name"]),
            ForwardsTo = (forward?["value"] as JsonArray)?.FirstOrDefault()?.ToString(),
            Stats = forward == null ? "drops mail" : "",
        };
    }

    private static (string Zone, string Rule) Split(Alias a) => a.Id.IndexOf('/', StringComparison.Ordinal) is var i and > 0 ? (a.Id[..i], a.Id[(i + 1)..]) : ("", a.Id);

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var zones = await ZonesAsync(ct);
        var zone = zones.FirstOrDefault(z => z.Name.Equals(req.Domain, StringComparison.OrdinalIgnoreCase));
        if (zone.Id == null) zone = zones.FirstOrDefault();
        if (zone.Id == null) throw new ProviderException("Cloudflare: the token can't see any zone. Check its zone access in Settings.");
        var to = req.ForwardTo.Length > 0 ? req.ForwardTo : _forwardTo;
        if (to.Length == 0) throw new ProviderException("Cloudflare: set where new addresses forward to in Settings.");
        var address = $"{RandomName.Or(req.Prefix)}@{zone.Name}";
        var note = string.IsNullOrWhiteSpace(req.Site) ? req.Description.Trim() : $"{req.Description.Trim()} ({req.Site.Trim()})".Trim();
        var res = await CallAsync(HttpMethod.Post, $"/zones/{zone.Id}/email/routing/rules", Rule(address, to, note.Length > 0 ? note : address, true), ct);
        return ToAlias(res?["result"] ?? throw new ProviderException("Cloudflare returned an empty response."), zone.Id)
            ?? throw new ProviderException("Cloudflare returned an unexpected rule.");
    }

    private static JsonObject Rule(string address, string forwardTo, string name, bool enabled) => new()
    {
        ["name"] = name.Length > 256 ? name[..256] : name,
        ["enabled"] = enabled,
        ["matchers"] = new JsonArray(new JsonObject { ["type"] = "literal", ["field"] = "to", ["value"] = address }),
        ["actions"] = new JsonArray(new JsonObject { ["type"] = "forward", ["value"] = new JsonArray(forwardTo) }),
    };

    /// <summary>Rules are replaced whole: read the rule, change one thing, write it back (keeping its actions).</summary>
    private async Task ChangeAsync(Alias alias, Action<JsonObject> change, CancellationToken ct)
    {
        var (zone, rule) = Split(alias);
        var current = (await CallAsync(HttpMethod.Get, $"/zones/{zone}/email/routing/rules/{rule}", null, ct))?["result"] as JsonObject
            ?? throw new ProviderException("Cloudflare: the rule no longer exists.");
        var body = new JsonObject
        {
            ["name"] = S(current["name"]),
            ["enabled"] = B(current["enabled"], true),
            ["matchers"] = current["matchers"]?.DeepClone(),
            ["actions"] = current["actions"]?.DeepClone(),
        };
        if (current["priority"] != null) body["priority"] = current["priority"]!.DeepClone();
        change(body);
        await CallAsync(HttpMethod.Put, $"/zones/{zone}/email/routing/rules/{rule}", body, ct);
    }

    public override async Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default)
    {
        await ChangeAsync(alias, b => b["enabled"] = enabled, ct);
        alias.Enabled = enabled; alias.State = enabled ? "enabled" : "disabled";
    }

    public override async Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        var name = description.Trim().Length > 0 ? description.Trim() : alias.Email;
        await ChangeAsync(alias, b => b["name"] = name.Length > 256 ? name[..256] : name, ct);
        alias.Description = description.Trim();
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        var (zone, rule) = Split(alias);
        await CallAsync(HttpMethod.Delete, $"/zones/{zone}/email/routing/rules/{rule}", null, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }
}
