using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>
/// TempMail.lol — disposable inboxes that end by themselves (about an hour; the service decides). Free without a key;
/// a TempMail Plus/Ultra API key adds its paid options. Each inbox has a token only this app keeps; mail is read with
/// it. The service has no "read" flag or message delete, so those are kept by the app for this session.
/// </summary>
public sealed class TempMailLolProvider : RestProvider, IInboxProvider
{
    public const string ProviderKey = "tempmaillol";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "TempMail.lol",
        Family = ServiceFamily.Disposable,
        Features = Features.Create | Features.CustomName | Features.EditNote | Features.Delete | Features.Inbox | Features.NativeExpiry | Features.Remembered,
        SendMode = SendMode.None,
        OptIn = true,
        MaxLifetime = TimeSpan.FromHours(1),
        Summary = "Free disposable inboxes from TempMail.lol that end by themselves after about an hour. The mail stays on their servers and is read here.",
        Setup = "Nothing to set up: tick \"Use this service\". A TempMail Plus or Ultra API key is optional.",
        SetupUrl = "https://tempmail.lol",
        Caution = "Disposable means public-grade: TempMail.lol can read everything sent to these inboxes, and they vanish after about an hour (the service decides, not the app). Use them only for sign-ups that don't matter. Each inbox's token is kept encrypted by this app (TempMail.lol's API takes it in the address of each request, over HTTPS).",
        Attribution = "Disposable inbox by TempMail.lol (tempmail.lol)",
        Fields = [new("key", "API KEY", "Only for TempMail Plus or Ultra.", Secret: true, Required: false)],
        Create = (c, h) => new TempMailLolProvider(c["key"], h),
    });

    private readonly InboxState _state = new();

    public TempMailLolProvider(string apiKey = "", HttpMessageHandler? handler = null, string baseUrl = "https://api.tempmail.lol") : base(baseUrl, handler)
    {
        if (apiKey.Trim().Length > 0) Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public override ServiceInfo Info => Service;

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var body = new JsonObject();
        if (RandomName.Clean(req.Prefix) is { Length: > 0 } p) body["prefix"] = p;
        var res = await SendAsync(HttpMethod.Post, "/v2/inbox/create", body, ct);
        var address = S(res?["address"]);
        var token = S(res?["token"]);
        if (address.Length == 0 || token.Length == 0) throw new ProviderException("TempMail.lol did not return an inbox.");
        var local = new LocalAlias
        {
            ProviderKey = ProviderKey, Id = address, Email = address, Description = req.Description.Trim(), Site = req.Site.Trim(),
            Expires = DateTimeOffset.Now + Service.MaxLifetime!.Value,
            Secrets = new() { ["token"] = token },
        };
        LocalAliases.Add(local);
        return LocalAliases.ToAlias(local);
    }

    private async Task<List<MessageDetail>> FetchAsync(Alias alias, CancellationToken ct)
    {
        var a = LocalAliases.Find(ProviderKey, alias.Email) ?? throw new ProviderException($"This app no longer has the token for {alias.Email}.");
        JsonNode? res;
        try { res = await SendAsync(HttpMethod.Get, "/v2/inbox?token=" + Uri.EscapeDataString(a.Secrets.GetValueOrDefault("token", "")), null, ct); }
        catch (ProviderException e) when (e.Status == 404) { throw new ProviderException("This TempMail.lol inbox has ended (they last about an hour).", 404); }
        if (B(res?["expired"], false)) throw new ProviderException("This TempMail.lol inbox has ended (they last about an hour).", 410);
        return (res?["emails"] as JsonArray ?? []).OfType<JsonNode>().Select(e =>
        {
            var when = Date(e["date"]) ?? DateTimeOffset.Now;
            var from = S(e["from"]);
            var subject = S(e["subject"]);
            return new MessageDetail
            {
                Id = Id(when, from, subject),
                Subject = subject,
                From = [InboxState.Parse(from)],
                To = [InboxState.Parse(S(e["to"]))],
                Sent = when,
                Text = S(e["body"]),
                Html = S(e["html"]),
            };
        }).ToList();
    }

    /// <summary>The service gives messages no id, so one is made from what identifies them.</summary>
    private static string Id(DateTimeOffset when, string from, string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{when.ToUnixTimeMilliseconds()}\n{from}\n{subject}")))[..16];

    public async Task<(List<MessageSummary> Items, int Total)> MessagesAsync(Alias alias, int position, CancellationToken ct = default) =>
        _state.Page(alias, await FetchAsync(alias, ct), position);

    public Task<MessageDetail> MessageAsync(Alias alias, string id, CancellationToken ct = default) => Task.FromResult(_state.Get(alias, id));
    public Task SetSeenAsync(Alias alias, string id, bool seen, CancellationToken ct = default) { _state.SetSeen(alias, id, seen); return Task.CompletedTask; }
    public Task DeleteMessageAsync(Alias alias, string id, CancellationToken ct = default) { _state.Hide(alias, id); return Task.CompletedTask; }
    public Task<byte[]> DownloadAsync(Alias alias, Attachment attachment, CancellationToken ct = default) => throw Unsupported("download attachments");

    /// <summary>Forgets the inbox (TempMail.lol ends it by itself).</summary>
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
