using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>Fastmail Masked Email over JMAP (capability https://www.fastmail.com/dev/maskedemail).</summary>
public sealed class FastmailProvider(JmapClient jmap) : IAliasProvider
{
    public const string ProviderKey = "fastmail";

    /// <summary>Fastmail is the mailbox the app is built around, so it is set up on its own Settings page (its token
    /// also covers reading and sending); this describes what its masked addresses can do.</summary>
    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "Fastmail",
        Family = ServiceFamily.Masked,
        Features = Features.List | Features.Create | Features.CustomName | Features.EditNote | Features.EditSite | Features.Disable | Features.Delete,
        SendMode = SendMode.Direct,
        Summary = "Fastmail Masked Email: addresses on fastmail.com that deliver to this account. Their mail is filed into a folder each, and you send from them directly.",
        Setup = "Settings → Fastmail.",
    });

    public ServiceInfo Info => Service;
    public string Key => ProviderKey;
    public string Name => Service.Name;
    public SendMode SendMode => SendMode.Direct;

    private async Task Ready(CancellationToken ct)
    {
        await jmap.ConnectAsync(ct);
        if (!jmap.HasMaskedEmail)
            throw new ProviderException("This Fastmail API token has no Masked Email access. Create a token with the \"Masked Email\" scope.");
    }

    public async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        await Ready(ct);
        var res = await jmap.CallOneAsync("MaskedEmail/get", new JsonObject { ["ids"] = null }, ct);
        return res["list"]!.AsArray().Select(m => ToAlias(m!.AsObject()))
            .Where(a => a.State != "deleted").ToList();
    }

    private static Alias ToAlias(JsonObject m) => new()
    {
        ProviderKey = ProviderKey,
        Id = m["id"]!.GetValue<string>(),
        Email = m["email"]!.GetValue<string>(),
        State = m["state"]?.GetValue<string>() ?? "",
        Enabled = m["state"]?.GetValue<string>() is "enabled" or "pending",
        Description = m["description"]?.GetValue<string>() ?? "",
        Site = m["forDomain"]?.GetValue<string>() ?? "",
        Created = DateTimeOffset.TryParse(m["createdAt"]?.GetValue<string>(), out var c) ? c : null,
        LastUsed = DateTimeOffset.TryParse(m["lastMessageAt"]?.GetValue<string>(), out var l) ? l : null,
        Stats = m["createdBy"]?.GetValue<string>() is { Length: > 0 } by ? "made by " + by : "",
    };

    /// <summary>Fastmail's emailPrefix rule: lower-case a-z, 0-9 and underscore, at most 64 characters.</summary>
    public static string CleanPrefix(string prefix)
    {
        var p = Regex.Replace(prefix.Trim().ToLowerInvariant().Replace('-', '_').Replace('.', '_').Replace(' ', '_'), "[^a-z0-9_]", "");
        return p.Length > 64 ? p[..64] : p;
    }

    /// <summary>forDomain should be an origin (scheme + host, no path).</summary>
    public static string Origin(string site)
    {
        site = site.Trim();
        if (site.Length == 0) return "";
        if (!site.Contains("://")) site = "https://" + site;
        return Uri.TryCreate(site, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}" : "";
    }

    public async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        await Ready(ct);
        // "enabled" straight away: a "pending" address is deleted after 24 h if no mail arrives.
        var create = new JsonObject { ["state"] = "enabled", ["description"] = req.Description.Trim() };
        var origin = Origin(req.Site);
        if (origin.Length > 0) create["forDomain"] = origin;
        var prefix = CleanPrefix(req.Prefix);
        if (prefix.Length > 0) create["emailPrefix"] = prefix;
        var res = await jmap.CallOneAsync("MaskedEmail/set", new JsonObject { ["create"] = new JsonObject { ["new"] = create } }, ct);
        JmapClient.CheckSet(res, "Creating the masked email");
        var created = res["created"]!["new"]!.AsObject();
        // The create response carries the server-set properties (id, email, createdAt…); merge with what we sent.
        foreach (var (k, v) in create) if (!created.ContainsKey(k) && k != "emailPrefix") created[k] = v?.DeepClone();
        return ToAlias(created);
    }

    private async Task Update(Alias a, JsonObject patch, CancellationToken ct)
    {
        await Ready(ct);
        var res = await jmap.CallOneAsync("MaskedEmail/set", new JsonObject { ["update"] = new JsonObject { [a.Id] = patch } }, ct);
        JmapClient.CheckSet(res, "Updating the masked email");
    }

    public async Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default)
    {
        await Update(alias, new JsonObject { ["state"] = enabled ? "enabled" : "disabled" }, ct);
        alias.Enabled = enabled; alias.State = enabled ? "enabled" : "disabled";
    }

    public async Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        var patch = new JsonObject { ["description"] = description.Trim() };
        var origin = Origin(site);
        patch["forDomain"] = origin;
        await Update(alias, patch, ct);
        alias.Description = description.Trim(); alias.Site = origin;
    }

    public async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        await Update(alias, new JsonObject { ["state"] = "deleted" }, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }

    public Task<string> ReverseAddressAsync(Alias alias, string recipient, CancellationToken ct = default) => Task.FromResult(recipient);
}
