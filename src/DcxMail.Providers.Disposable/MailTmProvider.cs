using System.Net.Http;
using System.Text.Json.Nodes;
using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>
/// Mail.tm (and its twin Mail.gw, same API) — free disposable inboxes. Each address is a small account with a random
/// password only this app knows; the app signs in with it to read the mail (JWT bearer token, refreshed when it
/// expires). Messages can be marked read, deleted, and their attachments saved; deleting the address deletes the account.
/// </summary>
public sealed class MailTmProvider : RestProvider, IDomainChoice, IInboxProvider
{
    public const string ProviderKey = "mailtm";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "Mail.tm",
        Family = ServiceFamily.Disposable,
        Features = Features.Create | Features.CustomName | Features.ChooseDomain | Features.EditNote | Features.Delete | Features.Inbox | Features.Remembered,
        SendMode = SendMode.None,
        OptIn = true,
        Summary = "Free disposable inboxes from Mail.tm (or Mail.gw, the same service on other domains). The mail stays on their servers and is read here; no account or key needed.",
        Setup = "Nothing to set up: tick \"Use this service\". Choose Mail.gw if Mail.tm's domains are refused by a site.",
        SetupUrl = "https://mail.tm",
        Caution = "Disposable means public-grade: Mail.tm can read everything sent to these inboxes, and many sites refuse their domains. Use them only for sign-ups that don't matter — never for banking, accounts you'd need to recover, or anything personal. Each inbox has a random password that only this app knows (kept encrypted).",
        Attribution = "Disposable inbox by Mail.tm (mail.tm)",
        Fields = [new("server", "SERVER", "", Required: false, Default: "https://api.mail.tm", Choices: ["https://api.mail.tm", "https://api.mail.gw"])],
        Create = (c, h) => new MailTmProvider(c["server"], h),
    });

    private const int PageSize = 30;

    public MailTmProvider(string baseUrl = "https://api.mail.tm", HttpMessageHandler? handler = null) : base(baseUrl, handler) { }

    public override ServiceInfo Info => Service;

    public async Task<List<string>> DomainsAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/domains", null, ct);
        return (res?["hydra:member"] as JsonArray ?? res as JsonArray ?? []).OfType<JsonNode>()
            .Where(d => B(d["isActive"], true)).Select(d => S(d["domain"])).Where(d => d.Length > 0).ToList();
    }

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var domains = await DomainsAsync(ct);
        var domain = domains.FirstOrDefault(d => d.Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) ?? domains.FirstOrDefault()
            ?? throw new ProviderException($"{Name} has no domain available right now.");
        var address = $"{RandomName.Or(req.Prefix)}@{domain}";
        var password = RandomName.Password();
        var account = await SendAsync(HttpMethod.Post, "/accounts", new JsonObject { ["address"] = address, ["password"] = password }, ct);
        var local = new LocalAlias
        {
            ProviderKey = ProviderKey, Id = S(account?["id"]), Email = S(account?["address"]) is { Length: > 0 } a ? a : address,
            Description = req.Description.Trim(), Site = req.Site.Trim(),
            Secrets = new() { ["password"] = password, ["server"] = BaseUrl },
        };
        local.Secrets["token"] = await TokenAsync(local, ct);
        LocalAliases.Add(local);
        return LocalAliases.ToAlias(local);
    }

    private async Task<string> TokenAsync(LocalAlias a, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Post, "/token", new JsonObject { ["address"] = a.Email, ["password"] = a.Secrets.GetValueOrDefault("password", "") }, ct);
        return S(res?["token"]) is { Length: > 0 } t ? t : throw new ProviderException($"{Name} did not sign in to {a.Email}.");
    }

    private LocalAlias Record(Alias alias)
    {
        var a = LocalAliases.Find(ProviderKey, alias.Email) ?? throw new ProviderException($"This app no longer has the password for {alias.Email}.");
        if (a.Secrets.TryGetValue("server", out var server) && !server.Equals(BaseUrl, StringComparison.OrdinalIgnoreCase))
            throw new ProviderException($"{alias.Email} was made on {server}; switch the {Name} server back in Settings to read it.");
        return a;
    }

    /// <summary>Calls with the address's token; signs in again once if the token has expired.</summary>
    private async Task<JsonNode?> AuthedAsync(Alias alias, HttpMethod method, string path, JsonNode? body, string contentType, CancellationToken ct)
    {
        var a = Record(alias);
        try { return await SendAsync(method, path, body, contentType, a.Secrets.GetValueOrDefault("token", ""), ct); }
        catch (ProviderException e) when (e.Status == 401)
        {
            a.Secrets["token"] = await TokenAsync(a, ct);
            LocalAliases.Update();
            return await SendAsync(method, path, body, contentType, a.Secrets["token"], ct);
        }
    }

    public async Task<(List<MessageSummary> Items, int Total)> MessagesAsync(Alias alias, int position, CancellationToken ct = default)
    {
        var page = position / PageSize + 1;
        var res = await AuthedAsync(alias, HttpMethod.Get, $"/messages?page={page}", null, "application/json", ct);
        var list = (res?["hydra:member"] as JsonArray ?? res as JsonArray ?? []).OfType<JsonNode>().Select(m => new MessageSummary
        {
            Id = S(m["id"]),
            Subject = S(m["subject"]),
            From = m["from"] is JsonObject f ? [Addr(f)] : [],
            To = (m["to"] as JsonArray ?? []).OfType<JsonObject>().Select(Addr).ToList(),
            Received = Date(m["createdAt"]) ?? DateTimeOffset.Now,
            Preview = S(m["intro"]),
            Unread = !B(m["seen"], false),
            HasAttachment = B(m["hasAttachments"], false),
        }).Skip(position % PageSize).ToList();
        var total = res?["hydra:totalItems"]?.GetValue<int>() ?? position + list.Count;
        return (list, total);
    }

    private static Addr Addr(JsonObject o) => new(S(o["name"]), S(o["address"]));

    public async Task<MessageDetail> MessageAsync(Alias alias, string id, CancellationToken ct = default)
    {
        var m = await AuthedAsync(alias, HttpMethod.Get, $"/messages/{Uri.EscapeDataString(id)}", null, "application/json", ct)
            ?? throw new ProviderException("The message is gone.");
        return new MessageDetail
        {
            Id = S(m["id"]),
            Subject = S(m["subject"]),
            From = m["from"] is JsonObject f ? [Addr(f)] : [],
            To = (m["to"] as JsonArray ?? []).OfType<JsonObject>().Select(Addr).ToList(),
            Cc = (m["cc"] as JsonArray ?? []).OfType<JsonObject>().Select(Addr).ToList(),
            Sent = Date(m["createdAt"]) ?? DateTimeOffset.Now,
            Html = m["html"] is JsonArray parts ? string.Concat(parts.Select(S)) : S(m["html"]),
            Text = S(m["text"]),
            Attachments = (m["attachments"] as JsonArray ?? []).OfType<JsonNode>()
                .Select(a => new Attachment(S(a["downloadUrl"]), S(a["filename"]), S(a["contentType"]), a["size"]?.GetValue<long>() ?? 0, null, false)).ToList(),
        };
    }

    public async Task SetSeenAsync(Alias alias, string id, bool seen, CancellationToken ct = default) =>
        await AuthedAsync(alias, HttpMethod.Patch, $"/messages/{Uri.EscapeDataString(id)}", new JsonObject { ["seen"] = seen }, "application/merge-patch+json", ct);

    public async Task DeleteMessageAsync(Alias alias, string id, CancellationToken ct = default) =>
        await AuthedAsync(alias, HttpMethod.Delete, $"/messages/{Uri.EscapeDataString(id)}", null, "application/json", ct);

    public async Task<byte[]> DownloadAsync(Alias alias, Attachment attachment, CancellationToken ct = default)
    {
        var a = Record(alias);
        try { return await GetBytesAsync(attachment.BlobId, a.Secrets.GetValueOrDefault("token", ""), ct); }
        catch (ProviderException e) when (e.Status == 401)
        {
            a.Secrets["token"] = await TokenAsync(a, ct);
            LocalAliases.Update();
            return await GetBytesAsync(attachment.BlobId, a.Secrets["token"], ct);
        }
    }

    /// <summary>Deletes the account at Mail.tm (and its mail), then forgets it.</summary>
    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        if (LocalAliases.Find(ProviderKey, alias.Email) != null)
        {
            try { await AuthedAsync(alias, HttpMethod.Delete, $"/accounts/{Uri.EscapeDataString(alias.Id)}", null, "application/json", ct); }
            catch (ProviderException e) when (e.Status is 404 or 401) { /* already gone at Mail.tm */ }
        }
        LocalAliases.Remove(ProviderKey, alias.Email);
        alias.State = "deleted"; alias.Enabled = false;
    }

    /// <summary>Notes are kept by the app.</summary>
    public override Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        if (LocalAliases.Find(ProviderKey, alias.Email) is { } l) { l.Description = description.Trim(); l.Site = site.Trim(); LocalAliases.Update(); }
        alias.Description = description.Trim(); alias.Site = site.Trim();
        return Task.CompletedTask;
    }
}
