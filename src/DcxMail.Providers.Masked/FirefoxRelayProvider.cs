using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace DcxMail.Providers;

/// <summary>Firefox Relay — REST API with "Authorization: Token &lt;API key&gt;". Relay can't send to new contacts, so
/// its masks are create and manage only; their incoming mail is still filed.</summary>
public sealed class FirefoxRelayProvider : RestProvider
{
    public const string ProviderKey = "relay";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "Firefox Relay",
        Family = ServiceFamily.Masked,
        Features = Features.List | Features.Create | Features.EditNote | Features.EditSite | Features.Disable | Features.Delete,
        SendMode = SendMode.None,
        Summary = "Firefox Relay by Mozilla. Masks on mozmail.com forward to your Firefox account's email; their mail is filed like any other. Relay can't send to new contacts.",
        Setup = "relay.firefox.com → Settings → API key.",
        SetupUrl = "https://relay.firefox.com/accounts/settings/",
        Fields = [new("key", "API KEY", Secret: true), new("url", "SERVER", Required: false, Default: "https://relay.firefox.com")],
        Create = (c, h) => new FirefoxRelayProvider(c["url"], c["key"], h),
    });

    public FirefoxRelayProvider(string baseUrl, string apiKey, HttpMessageHandler? handler = null) : base(baseUrl, handler) =>
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", apiKey.Trim());

    public override ServiceInfo Info => Service;

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/v1/relayaddresses/", null, ct);
        var list = res as JsonArray ?? res?["results"] as JsonArray ?? [];
        return list.Select(a => ToAlias(a!)).ToList();
    }

    private static Alias ToAlias(JsonNode a) => new()
    {
        ProviderKey = ProviderKey,
        Id = S(a["id"]),
        Email = S(a["full_address"]) is { Length: > 0 } full ? full : S(a["address"]) + "@mozmail.com",
        Enabled = B(a["enabled"], true),
        State = B(a["enabled"], true) ? "enabled" : "disabled",
        Description = S(a["description"]),
        Site = S(a["generated_for"]),
        Created = Date(a["created_at"]),
        LastUsed = Date(a["last_used_at"]),
        Stats = $"{S(a["num_forwarded"])} forwarded · {S(a["num_blocked"])} blocked",
    };

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, "/api/v1/relayaddresses/", new JsonObject
        {
            ["enabled"] = true,
            ["description"] = req.Description.Trim(),
            ["generated_for"] = req.Site.Trim(),
        }, ct);
        return ToAlias(res ?? throw new ProviderException("Firefox Relay returned an empty response."));
    }

    public override async Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Patch, $"/api/v1/relayaddresses/{alias.Id}/", new JsonObject { ["enabled"] = enabled }, ct);
        alias.Enabled = enabled; alias.State = enabled ? "enabled" : "disabled";
    }

    public override async Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Patch, $"/api/v1/relayaddresses/{alias.Id}/", new JsonObject { ["description"] = description.Trim(), ["generated_for"] = site.Trim() }, ct);
        alias.Description = description.Trim(); alias.Site = site.Trim();
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Delete, $"/api/v1/relayaddresses/{alias.Id}/", null, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }

    public override Task<string> ReverseAddressAsync(Alias alias, string recipient, CancellationToken ct = default) =>
        throw new ProviderException("Firefox Relay cannot send to new contacts. Reply to mail Relay forwarded to you instead.");
}
