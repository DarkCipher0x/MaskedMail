using System.Net.Http;
using System.Text.Json.Nodes;

namespace DcxMail.Providers;

/// <summary>
/// mailcow — your own (self-hosted) mail server, through its API with an X-API-Key. Ordinary aliases (create, list,
/// turn off, note, delete) and mailcow's own time-limited aliases ("spam aliases"), which the server itself ends after
/// a set time — the most robust kind of throwaway, because it expires even if this app never runs again.
/// </summary>
public sealed class MailcowProvider : RestProvider, IDomainChoice, IForwardChoice
{
    public const string ProviderKey = "mailcow";
    private const string TimeLimited = "tla:";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "mailcow",
        Family = ServiceFamily.OwnDomain,
        Features = Features.List | Features.Create | Features.CustomName | Features.ChooseDomain | Features.ForwardTo
                   | Features.EditNote | Features.Disable | Features.Delete | Features.NativeExpiry,
        SendMode = SendMode.None,
        Summary = "Your own mailcow server. Ordinary aliases, and mailcow's time-limited aliases that the server deletes by itself when their time is up (used for throwaways).",
        Setup = "mailcow admin → System → Configuration → Access → Edit administrator details → API: enable a read-write key and allow only your PC's IP address.",
        Caution = "Use a read-write API key limited to your IP address. The key is an administrator key for the whole server; it is stored encrypted and sent only to the server below, over HTTPS.",
        Fields =
        [
            new("url", "SERVER", "Your mailcow address, https:// only, e.g. https://mail.example.com"),
            new("key", "API KEY (read-write)", Secret: true),
            new("mailbox", "YOUR MAILBOX", "The mailbox time-limited aliases belong to, and where new aliases deliver by default."),
            new("domains", "DOMAINS", "Leave empty to use every domain on the server.", Required: false),
        ],
        Create = (c, h) => new MailcowProvider(c["url"], c["key"], c["mailbox"], c.List("domains"), h),
    });

    private readonly string _mailbox;
    private readonly List<string> _only;

    public MailcowProvider(string baseUrl, string apiKey, string mailbox, List<string> domains, HttpMessageHandler? handler = null) : base(baseUrl, handler)
    {
        Http.DefaultRequestHeaders.Add("X-API-Key", apiKey.Trim());
        _mailbox = mailbox.Trim();
        _only = domains;
    }

    public override ServiceInfo Info => Service;

    /// <summary>mailcow answers changes with [{type: success|danger|error, msg}]; a 200 can still be a refusal.</summary>
    private async Task<JsonNode?> ChangeAsync(string path, JsonNode body, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Post, path, body, ct);
        var items = res as JsonArray ?? new JsonArray(res?.DeepClone());
        var bad = items.OfType<JsonNode>().FirstOrDefault(i => S(i["type"]) is "danger" or "error");
        if (bad != null) throw new ProviderException("mailcow: " + (bad["msg"] is JsonArray m ? string.Join(" ", m.Select(S)) : S(bad["msg"])));
        return res;
    }

    public async Task<List<string>> DomainsAsync(CancellationToken ct = default)
    {
        if (_only.Count > 0) return _only;
        var res = await SendAsync(HttpMethod.Get, "/api/v1/get/domain/all", null, ct);
        var list = (res as JsonArray ?? []).Select(d => S(d?["domain_name"])).Where(d => d.Length > 0).ToList();
        var own = _mailbox[(_mailbox.LastIndexOf('@') + 1)..];
        if (list.Remove(own)) list.Insert(0, own);
        return list;
    }

    public Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default) => Task.FromResult(new List<string> { _mailbox });

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<Alias>();
        var aliases = await SendAsync(HttpMethod.Get, "/api/v1/get/alias/all", null, ct);
        foreach (var a in (aliases as JsonArray ?? []).OfType<JsonNode>())
        {
            var address = S(a["address"]);
            if (address.StartsWith('@') || B(a["is_catch_all"], false)) continue;
            if (_only.Count > 0 && !_only.Contains(S(a["domain"]), StringComparer.OrdinalIgnoreCase)) continue;
            all.Add(new Alias
            {
                ProviderKey = ProviderKey,
                Id = S(a["id"]),
                Email = address,
                Enabled = B(a["active"], true),
                State = B(a["active"], true) ? "enabled" : "disabled",
                Description = S(a["private_comment"]) is { Length: > 0 } pc ? pc : S(a["public_comment"]),
                Created = Date(a["created"]),
                ForwardsTo = S(a["goto"]),
            });
        }
        foreach (var t in await TimeLimitedAsync(ct)) all.Add(t);
        return all;
    }

    private async Task<List<Alias>> TimeLimitedAsync(CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/v1/get/time_limited_aliases/{Uri.EscapeDataString(_mailbox)}", null, ct);
        return (res as JsonArray ?? []).OfType<JsonNode>().Select(t => new Alias
        {
            ProviderKey = ProviderKey,
            Id = TimeLimited + S(t["address"]),
            Email = S(t["address"]),
            Description = S(t["description"]) is { Length: > 0 } d ? d : "time-limited alias",
            Created = Date(t["created"]),
            Expires = Date(t["validity"]),
            ForwardsTo = S(t["goto"]),
        }).ToList();
    }

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var domains = await DomainsAsync(ct);
        var domain = domains.FirstOrDefault(d => d.Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) ?? domains.FirstOrDefault()
            ?? throw new ProviderException("mailcow: no domain to create the alias on.");
        var note = string.IsNullOrWhiteSpace(req.Site) ? req.Description.Trim() : $"{req.Description.Trim()} ({req.Site.Trim()})".Trim();

        if (req.Lifetime is { } life)
        {
            // A time-limited alias: mailcow picks the name and deletes it itself when the time is up.
            var before = (await TimeLimitedAsync(ct)).Select(t => t.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hours = (int)Math.Clamp(Math.Ceiling(life.TotalHours), 1, 87600);
            await ChangeAsync("/api/v1/add/time_limited_alias", new JsonObject
            {
                ["username"] = _mailbox, ["domain"] = domain, ["description"] = note, ["validity"] = hours, ["permanent"] = false,
            }, ct);
            return (await TimeLimitedAsync(ct)).Where(t => !before.Contains(t.Email)).OrderByDescending(t => t.Created).FirstOrDefault()
                ?? throw new ProviderException("mailcow created the time-limited alias but didn't list it.");
        }

        var address = $"{RandomName.Or(req.Prefix)}@{domain}";
        await ChangeAsync("/api/v1/add/alias", new JsonObject
        {
            ["address"] = address, ["goto"] = req.ForwardTo.Length > 0 ? req.ForwardTo : _mailbox, ["active"] = "1",
        }, ct);
        var created = (await ListAsync(ct)).FirstOrDefault(a => a.Email.Equals(address, StringComparison.OrdinalIgnoreCase))
            ?? throw new ProviderException("mailcow created the alias but didn't list it.");
        if (note.Length > 0) await UpdateAsync(created, note, "", ct);
        return created;
    }

    private static bool IsTimeLimited(Alias a) => a.Id.StartsWith(TimeLimited, StringComparison.Ordinal);

    public override async Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default)
    {
        if (IsTimeLimited(alias)) throw new ProviderException("mailcow time-limited aliases can't be turned off, only deleted.");
        await ChangeAsync("/api/v1/edit/alias", new JsonObject { ["items"] = new JsonArray(alias.Id), ["attr"] = new JsonObject { ["active"] = enabled ? "1" : "0" } }, ct);
        alias.Enabled = enabled; alias.State = enabled ? "enabled" : "disabled";
    }

    public override async Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        if (IsTimeLimited(alias)) throw new ProviderException("mailcow time-limited aliases keep the note they were made with.");
        await ChangeAsync("/api/v1/edit/alias", new JsonObject { ["items"] = new JsonArray(alias.Id), ["attr"] = new JsonObject { ["private_comment"] = description.Trim() } }, ct);
        alias.Description = description.Trim();
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        if (IsTimeLimited(alias)) await ChangeAsync("/api/v1/delete/time_limited_alias", new JsonArray(alias.Email), ct);
        else await ChangeAsync("/api/v1/delete/alias", new JsonArray(alias.Id), ct);
        alias.State = "deleted"; alias.Enabled = false;
    }
}
