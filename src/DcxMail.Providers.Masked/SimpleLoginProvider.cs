using System.Net.Http;
using System.Text.Json.Nodes;

namespace DcxMail.Providers;

/// <summary>SimpleLogin (Proton) — REST API with the "Authentication" header. Also covers Proton Pass aliases, which
/// are SimpleLogin aliases. Self-hosted servers work too.</summary>
public sealed class SimpleLoginProvider : RestProvider, IDomainChoice, IForwardChoice, IActivityProvider
{
    public const string ProviderKey = "simplelogin";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "SimpleLogin",
        Family = ServiceFamily.Masked,
        Features = Features.List | Features.Create | Features.CustomName | Features.ChooseDomain | Features.ForwardTo
                   | Features.EditNote | Features.Disable | Features.Delete | Features.Activity,
        SendMode = SendMode.ReverseAlias,
        Summary = "SimpleLogin by Proton. Your Proton Pass aliases are SimpleLogin aliases too, so they appear here. Aliases forward to your mailboxes; if one is this Fastmail account, their mail is filed and you can send through reverse aliases.",
        Setup = "SimpleLogin → API Keys → Create (or Proton Pass → Settings → Aliases → SimpleLogin). Self-hosted servers work too.",
        SetupUrl = "https://app.simplelogin.io/dashboard/api_key",
        Fields = [new("key", "API KEY", Secret: true), new("url", "SERVER", "Change only for a self-hosted server.", Required: false, Default: "https://app.simplelogin.io")],
        Create = (c, h) => new SimpleLoginProvider(c["url"], c["key"], h),
    });

    public SimpleLoginProvider(string baseUrl, string apiKey, HttpMessageHandler? handler = null) : base(baseUrl, handler) =>
        Http.DefaultRequestHeaders.Add("Authentication", apiKey.Trim());

    public override ServiceInfo Info => Service;

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<Alias>();
        for (int page = 0; page < 500; page++)
        {
            var res = await SendAsync(HttpMethod.Get, $"/api/v2/aliases?page_id={page}", null, ct);
            var list = res?["aliases"] as JsonArray ?? [];
            all.AddRange(list.Select(a => ToAlias(a!)));
            if (list.Count < 20) break;   // 20 per page
        }
        return all;
    }

    private static Alias ToAlias(JsonNode a) => new()
    {
        ProviderKey = ProviderKey,
        Id = S(a["id"]),
        Email = S(a["email"]),
        Enabled = B(a["enabled"], true),
        State = B(a["enabled"], true) ? "enabled" : "disabled",
        Description = S(a["note"]),
        Created = Date(a["creation_timestamp"]),
        LastUsed = Date(a["latest_activity"]?["timestamp"]),
        Stats = $"{S(a["nb_forward"])} forwarded · {S(a["nb_reply"])} replied · {S(a["nb_block"])} blocked",
        ForwardsTo = (a["mailboxes"] as JsonArray)?.FirstOrDefault()?["email"]?.ToString() ?? a["mailbox"]?["email"]?.ToString(),
    };

    private static string HostQuery(string site)
    {
        if (site.Trim().Length == 0) return "";
        var host = Uri.TryCreate(site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site, UriKind.Absolute, out var u) ? u.Host : "";
        return host.Length > 0 ? "?hostname=" + Uri.EscapeDataString(host) : "";
    }

    /// <summary>The suffixes a custom alias can end with (".x7@simplelogin.com", "@your-domain.com"); free ones first.</summary>
    public async Task<List<string>> DomainsAsync(CancellationToken ct = default) =>
        (await SuffixesAsync("", ct)).Select(s => S(s["suffix"])).ToList();

    private async Task<List<JsonNode>> SuffixesAsync(string hostQuery, CancellationToken ct)
    {
        var options = await SendAsync(HttpMethod.Get, "/api/v5/alias/options" + hostQuery, null, ct);
        if (options?["can_create"]?.GetValue<bool>() == false) throw new ProviderException("SimpleLogin says this account cannot create more aliases.");
        return (options?["suffixes"] as JsonArray ?? []).OfType<JsonNode>()
            .OrderBy(s => B(s["is_premium"], false)).ToList();
    }

    /// <summary>Your mailboxes (verified ones), the default first.</summary>
    public async Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default) =>
        (await MailboxesAsync(ct)).Select(m => m.Email).ToList();

    private async Task<List<(int Id, string Email)>> MailboxesAsync(CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/v2/mailboxes", null, ct);
        return (res?["mailboxes"] as JsonArray ?? []).OfType<JsonNode>()
            .Where(b => B(b["verified"], true))
            .OrderByDescending(b => B(b["default"], false))
            .Select(b => (b["id"]!.GetValue<int>(), S(b["email"]))).ToList();
    }

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var hostQ = HostQuery(req.Site);
        var boxes = req.ForwardTo.Length > 0 || req.Prefix.Trim().Length > 0 ? await MailboxesAsync(ct) : [];
        var box = boxes.FirstOrDefault(b => b.Email.Equals(req.ForwardTo, StringComparison.OrdinalIgnoreCase));
        if (box.Email == null && boxes.Count > 0) box = boxes[0];
        JsonNode? created;
        if (req.Prefix.Trim().Length > 0)
        {
            // Custom alias: a signed suffix from the options call, and the mailbox to deliver to.
            var suffixes = await SuffixesAsync(hostQ, ct);
            var suffix = suffixes.FirstOrDefault(s => S(s["suffix"]).Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) ?? suffixes.FirstOrDefault()
                ?? throw new ProviderException("SimpleLogin returned no alias suffixes.");
            if (box.Email == null) throw new ProviderException("SimpleLogin returned no verified mailboxes.");
            created = await SendAsync(HttpMethod.Post, "/api/v3/alias/custom/new" + hostQ, new JsonObject
            {
                ["alias_prefix"] = RandomName.Clean(req.Prefix),
                ["signed_suffix"] = S(suffix["signed_suffix"] ?? suffix["signed-suffix"]),
                ["mailbox_ids"] = new JsonArray(box.Id),
                ["note"] = req.Description.Trim(),
            }, ct);
        }
        else
        {
            created = await SendAsync(HttpMethod.Post, "/api/alias/random/new" + hostQ, new JsonObject { ["note"] = req.Description.Trim() }, ct);
            // A random alias goes to the default mailbox; move it if another one was chosen.
            if (created != null && box.Email != null && boxes.Count > 0 && box.Id != boxes[0].Id)
                await SendAsync(HttpMethod.Patch, $"/api/aliases/{S(created["id"])}", new JsonObject { ["mailbox_ids"] = new JsonArray(box.Id) }, ct);
        }
        return ToAlias(created ?? throw new ProviderException("SimpleLogin returned an empty response."));
    }

    public override async Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default)
    {
        if (alias.Enabled == enabled) return;
        var res = await SendAsync(HttpMethod.Post, $"/api/aliases/{alias.Id}/toggle", null, ct);
        alias.Enabled = res?["enabled"]?.GetValue<bool>() ?? enabled;
        alias.State = alias.Enabled ? "enabled" : "disabled";
    }

    public override async Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Patch, $"/api/aliases/{alias.Id}", new JsonObject { ["note"] = description.Trim() }, ct);
        alias.Description = description.Trim();
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Delete, $"/api/aliases/{alias.Id}", null, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }

    public override async Task<string> ReverseAddressAsync(Alias alias, string recipient, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, $"/api/aliases/{alias.Id}/contacts", new JsonObject { ["contact"] = recipient.Trim() }, ct);
        var addr = S(res?["reverse_alias_address"]);
        return addr.Length > 0 ? addr : throw new ProviderException("SimpleLogin did not return a reverse alias for " + recipient);
    }

    /// <summary>The latest forwards, replies and blocks (first page, 20 entries).</summary>
    public async Task<List<ActivityEntry>> ActivityAsync(Alias alias, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/aliases/{alias.Id}/activities?page_id=0", null, ct);
        return (res?["activities"] as JsonArray ?? []).OfType<JsonNode>()
            .Select(a => new ActivityEntry(Date(a["timestamp"]) ?? DateTimeOffset.MinValue, S(a["action"]), S(a["from"]), S(a["to"]))).ToList();
    }
}
