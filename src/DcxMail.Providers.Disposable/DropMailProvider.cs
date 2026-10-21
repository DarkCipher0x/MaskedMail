using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>
/// DropMail.me — disposable inboxes through its GraphQL API. A free access token (af_…) from dropmail.me/api is
/// required. Each address lives in a session that ends after about 10 minutes without use (each check extends it);
/// when it ends, its mail is gone. Read flags and deletes are kept by the app for this session.
/// </summary>
public sealed class DropMailProvider : RestProvider, IDomainChoice, IInboxProvider
{
    public const string ProviderKey = "dropmail";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "DropMail",
        Family = ServiceFamily.Disposable,
        Features = Features.Create | Features.ChooseDomain | Features.EditNote | Features.Delete | Features.Inbox | Features.NativeExpiry | Features.Remembered,
        SendMode = SendMode.None,
        OptIn = true,
        Summary = "Disposable inboxes from DropMail.me. An address stays alive while it is being checked and ends about 10 minutes after the last check, taking its mail with it.",
        Setup = "Get a free access token (af_…) at dropmail.me/api (no sign-up; choose how long it is valid).",
        SetupUrl = "https://dropmail.me/api/",
        Caution = "Disposable means public-grade: DropMail can read everything sent to these inboxes, and an inbox (with its mail) is gone about 10 minutes after it was last checked. Use them only for quick sign-ups that don't matter. DropMail's API takes the access token in the address of each request (over HTTPS); it is kept encrypted by this app.",
        Attribution = "Disposable inbox by DropMail (dropmail.me)",
        Fields = [new("token", "ACCESS TOKEN", "Starts with af_.", Secret: true)],
        Create = (c, h) => new DropMailProvider(c["token"], h),
    });

    private readonly string _path;
    private readonly InboxState _state = new();

    public DropMailProvider(string token, HttpMessageHandler? handler = null, string baseUrl = "https://dropmail.me") : base(baseUrl, handler) =>
        _path = "/api/graphql/" + Uri.EscapeDataString(token.Trim());

    public override ServiceInfo Info => Service;

    /// <summary>One GraphQL call; values are JSON-encoded into the query, so nothing can break out of a string.</summary>
    private async Task<JsonNode> QueryAsync(string query, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Post, _path, new JsonObject { ["query"] = query }, ct);
        if (res?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var msg = string.Join("; ", errors.Select(e => S(e?["message"])));
            throw new ProviderException(msg.Contains("SESSION_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                ? "This DropMail inbox has ended (about 10 minutes without a check), and its mail with it." : "DropMail: " + msg, 410);
        }
        return res?["data"] ?? throw new ProviderException("DropMail returned no data.");
    }

    private static string Lit(string s) => JsonSerializer.Serialize(s);

    private async Task<List<(string Id, string Name)>> DomainListAsync(CancellationToken ct) =>
        ((await QueryAsync("query { domains { id name } }", ct))["domains"] as JsonArray ?? []).OfType<JsonNode>()
            .Select(d => (S(d["id"]), S(d["name"]))).ToList();

    public async Task<List<string>> DomainsAsync(CancellationToken ct = default) => (await DomainListAsync(ct)).Select(d => d.Name).ToList();

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var input = "withAddress: true";
        if (req.Domain.Length > 0 && (await DomainListAsync(ct)).FirstOrDefault(d => d.Name.Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) is { Id.Length: > 0 } d)
            input += ", domainId: " + Lit(d.Id);
        var data = await QueryAsync($"mutation {{ introduceSession(input: {{ {input} }}) {{ id expiresAt addresses {{ address restoreKey }} }} }}", ct);
        var session = data["introduceSession"];
        var addr = (session?["addresses"] as JsonArray)?.FirstOrDefault();
        var address = S(addr?["address"]);
        if (session == null || address.Length == 0) throw new ProviderException("DropMail did not return an address.");
        var local = new LocalAlias
        {
            ProviderKey = ProviderKey, Id = S(session["id"]), Email = address, Description = req.Description.Trim(), Site = req.Site.Trim(),
            Expires = Date(session["expiresAt"]),
            Secrets = new() { ["session"] = S(session["id"]), ["restoreKey"] = S(addr?["restoreKey"]) },
        };
        LocalAliases.Add(local);
        return LocalAliases.ToAlias(local);
    }

    private async Task<List<MessageDetail>> FetchAsync(Alias alias, CancellationToken ct)
    {
        var a = LocalAliases.Find(ProviderKey, alias.Email) ?? throw new ProviderException($"This app no longer has the session for {alias.Email}.");
        var data = await QueryAsync($"query {{ session(id: {Lit(a.Secrets.GetValueOrDefault("session", ""))}) {{ expiresAt mails {{ id fromAddr toAddr headerSubject text html receivedAt }} }} }}", ct);
        var session = data["session"] ?? throw new ProviderException("This DropMail inbox has ended (about 10 minutes without a check), and its mail with it.", 410);
        if (Date(session["expiresAt"]) is { } until) { a.Expires = until; LocalAliases.Update(); }
        return (session["mails"] as JsonArray ?? []).OfType<JsonNode>().Select(m => new MessageDetail
        {
            Id = S(m["id"]),
            Subject = S(m["headerSubject"]),
            From = [InboxState.Parse(S(m["fromAddr"]))],
            To = [InboxState.Parse(S(m["toAddr"]))],
            Sent = Date(m["receivedAt"]) ?? DateTimeOffset.Now,
            Text = S(m["text"]),
            Html = S(m["html"]),
        }).ToList();
    }

    public async Task<(List<MessageSummary> Items, int Total)> MessagesAsync(Alias alias, int position, CancellationToken ct = default) =>
        _state.Page(alias, await FetchAsync(alias, ct), position);

    public Task<MessageDetail> MessageAsync(Alias alias, string id, CancellationToken ct = default) => Task.FromResult(_state.Get(alias, id));
    public Task SetSeenAsync(Alias alias, string id, bool seen, CancellationToken ct = default) { _state.SetSeen(alias, id, seen); return Task.CompletedTask; }
    public Task DeleteMessageAsync(Alias alias, string id, CancellationToken ct = default) { _state.Hide(alias, id); return Task.CompletedTask; }
    public Task<byte[]> DownloadAsync(Alias alias, Attachment attachment, CancellationToken ct = default) => throw Unsupported("download attachments");

    /// <summary>Forgets the inbox (DropMail ends it by itself once it is no longer checked).</summary>
    public override Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        LocalAliases.Remove(ProviderKey, alias.Email);
        alias.State = "deleted"; alias.Enabled = false;
        return Task.CompletedTask;
    }

    public override Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        if (LocalAliases.Find(ProviderKey, alias.Email) is { } l) { l.Description = description.Trim(); l.Site = site.Trim(); LocalAliases.Update(); }
        alias.Description = description.Trim(); alias.Site = site.Trim();
        return Task.CompletedTask;
    }
}
