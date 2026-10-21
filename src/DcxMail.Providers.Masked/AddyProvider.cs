using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace DcxMail.Providers;

/// <summary>addy.io (formerly AnonAddy) — REST API v1 with a Bearer key. Works with self-hosted servers too.</summary>
public sealed class AddyProvider : RestProvider, IDomainChoice, IForwardChoice
{
    public const string ProviderKey = "addy";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "addy.io",
        Family = ServiceFamily.Masked,
        Features = Features.List | Features.Create | Features.CustomName | Features.ChooseDomain | Features.ForwardTo
                   | Features.EditNote | Features.Disable | Features.Delete,
        SendMode = SendMode.ReverseAlias,
        Summary = "addy.io (formerly AnonAddy). Aliases on its shared domains, your username subdomain or your own domains forward to your recipients. Sending from an alias uses addy.io's alias+recipient=domain address.",
        Setup = "addy.io → Settings → API → Create New API Key. Self-hosted servers work too.",
        SetupUrl = "https://app.addy.io/settings/api",
        Fields =
        [
            new("key", "API KEY", Secret: true),
            new("format", "NAME STYLE FOR NEW ALIASES", "Empty = your addy.io default.", Required: false, Choices: ["", "random_characters", "uuid", "random_words", "random_male_name", "random_female_name", "random_noun"]),
            new("url", "SERVER", "Change only for a self-hosted server.", Required: false, Default: "https://app.addy.io"),
        ],
        Create = (c, h) => new AddyProvider(c["url"], c["key"], h) { Format = c["format"] },
    });

    private static readonly string[] Formats = ["random_characters", "uuid", "random_words", "random_male_name", "random_female_name", "random_noun"];
    private List<string>? _domains;

    public AddyProvider(string baseUrl, string apiKey, HttpMessageHandler? handler = null) : base(baseUrl, handler)
    {
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        Http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    }

    public override ServiceInfo Info => Service;

    /// <summary>Name style for random aliases (one of addy.io's formats); empty = the account default.</summary>
    public string Format { get; init; } = "";

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<Alias>();
        for (int page = 1; page < 200; page++)
        {
            var res = await SendAsync(HttpMethod.Get, $"/api/v1/aliases?page[number]={page}&page[size]=100&with=recipients", null, ct);
            var list = res?["data"] as JsonArray ?? [];
            all.AddRange(list.Select(a => ToAlias(a!)));
            var last = res?["meta"]?["last_page"]?.GetValue<int>() ?? page;
            if (list.Count == 0 || page >= last) break;
        }
        return all;
    }

    private static Alias ToAlias(JsonNode a) => new()
    {
        ProviderKey = ProviderKey,
        Id = S(a["id"]),
        Email = S(a["email"]),
        Enabled = B(a["active"], true),
        State = B(a["active"], true) ? "enabled" : "disabled",
        Description = S(a["description"]),
        Created = Date(a["created_at"]),
        LastUsed = Date(a["last_forwarded"]),
        Stats = $"{S(a["emails_forwarded"])} forwarded · {S(a["emails_replied"])} replied · {S(a["emails_sent"])} sent · {S(a["emails_blocked"])} blocked",
        ForwardsTo = (a["recipients"] as JsonArray)?.FirstOrDefault()?["email"]?.ToString(),
    };

    /// <summary>The domains this account can use (shared domains, username subdomains, your own), the default first.</summary>
    public async Task<List<string>> DomainsAsync(CancellationToken ct = default)
    {
        if (_domains != null) return _domains;
        JsonNode? res = null;
        try { res = await SendAsync(HttpMethod.Get, "/api/v1/domain-options", null, ct); }
        catch (ProviderException e) when (e.Status == 404) { /* older self-hosted servers: the account's default below */ }
        var def = S(res?["defaultAliasDomain"]);
        var list = (res?["data"] as JsonArray ?? []).Select(S).Where(d => d.Length > 0).ToList();
        if (def.Length == 0)
        {
            var acct = await SendAsync(HttpMethod.Get, "/api/v1/account-details", null, ct);
            def = S(acct?["data"]?["default_alias_domain"]);
        }
        if (def.Length > 0) { list.Remove(def); list.Insert(0, def); }
        if (list.Count == 0) list.Add("anonaddy.me");
        return _domains = list;
    }

    /// <summary>Your verified recipients.</summary>
    public async Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default) =>
        (await RecipientsAsync(ct)).Select(r => r.Email).ToList();

    private async Task<List<(string Id, string Email)>> RecipientsAsync(CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/v1/recipients?filter[verified]=true", null, ct);
        return (res?["data"] as JsonArray ?? []).OfType<JsonNode>().Select(r => (S(r["id"]), S(r["email"]))).ToList();
    }

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var domains = await DomainsAsync(ct);
        var body = new JsonObject
        {
            ["domain"] = domains.FirstOrDefault(d => d.Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) ?? domains[0],
            ["description"] = string.IsNullOrWhiteSpace(req.Site) ? req.Description.Trim() : $"{req.Description.Trim()} ({req.Site.Trim()})".Trim(),
        };
        var prefix = RandomName.Clean(req.Prefix);
        if (prefix.Length > 0) { body["format"] = "custom"; body["local_part"] = prefix; }
        else if (Formats.Contains(Format)) body["format"] = Format;
        if (req.ForwardTo.Length > 0 && (await RecipientsAsync(ct)).FirstOrDefault(r => r.Email.Equals(req.ForwardTo, StringComparison.OrdinalIgnoreCase)) is { Id: { Length: > 0 } rid })
            body["recipient_ids"] = new JsonArray(rid);
        var res = await SendAsync(HttpMethod.Post, "/api/v1/aliases", body, ct);
        return ToAlias(res?["data"] ?? throw new ProviderException("addy.io returned an empty response."));
    }

    public override async Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default)
    {
        if (enabled) await SendAsync(HttpMethod.Post, "/api/v1/active-aliases", new JsonObject { ["id"] = alias.Id }, ct);
        else await SendAsync(HttpMethod.Delete, $"/api/v1/active-aliases/{alias.Id}", null, ct);
        alias.Enabled = enabled; alias.State = enabled ? "enabled" : "disabled";
    }

    public override async Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Patch, $"/api/v1/aliases/{alias.Id}", new JsonObject { ["description"] = description.Trim() }, ct);
        alias.Description = description.Trim();
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Delete, $"/api/v1/aliases/{alias.Id}", null, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }

    /// <summary>addy.io's documented "send from" format: local+recipient(with @ as =)@alias-domain.
    /// shop@you.addy.io → hello@store.example becomes shop+hello=store.example@you.addy.io.</summary>
    public static string SendFromAddress(string alias, string recipient)
    {
        var at = alias.LastIndexOf('@');
        return $"{alias[..at]}+{recipient.Trim().Replace('@', '=')}@{alias[(at + 1)..]}";
    }

    public override Task<string> ReverseAddressAsync(Alias alias, string recipient, CancellationToken ct = default) =>
        Task.FromResult(SendFromAddress(alias.Email, recipient));
}
