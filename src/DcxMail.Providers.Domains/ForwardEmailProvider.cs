using System.Net.Http;
using System.Text.Json.Nodes;
using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>
/// Forward Email (forwardemail.net, open source) — aliases on your own domains through its REST API (Basic auth: the
/// API token as user name, empty password). Create, list, turn off (silently drop or bounce), note, delete, and send
/// from an alias through its outbound email API.
/// </summary>
public sealed class ForwardEmailProvider : RestProvider, IDomainChoice, IForwardChoice, ISendingProvider
{
    public const string ProviderKey = "forwardemail";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "Forward Email",
        Family = ServiceFamily.OwnDomain,
        Features = Features.List | Features.Create | Features.CustomName | Features.ChooseDomain | Features.ForwardTo
                   | Features.EditNote | Features.Disable | Features.Delete,
        SendMode = SendMode.Service,
        Summary = "Forward Email (open source). Aliases on your own domains forward wherever you say; you can turn one off (drop or bounce its mail), keep a note on it, and send from it through Forward Email's outbound email.",
        Setup = "Forward Email → My Account → Security → Developer Access → API token. Your domain must already be set up there. Sending needs outbound SMTP turned on for the domain.",
        SetupUrl = "https://forwardemail.net/my-account/security",
        Fields =
        [
            new("token", "API TOKEN", Secret: true),
            new("forward_to", "NEW ALIASES FORWARD TO", "Your real address, e.g. your Fastmail address (several: comma-separated)."),
            new("domains", "DOMAINS", "Leave empty to use every domain on the account.", Required: false),
            new("when_off", "WHEN AN ALIAS IS TURNED OFF", "drop = accept and discard silently; bounce = reject (senders see an error); defer = soft-fail.", Required: false, Default: "drop", Choices: ["drop", "bounce", "defer"]),
        ],
        Create = (c, h) => new ForwardEmailProvider(c["token"], c.List("forward_to"), c.List("domains"), c["when_off"], h),
    });

    private readonly List<string> _forwardTo, _domains;
    private readonly int _offCode;

    public ForwardEmailProvider(string token, List<string> forwardTo, List<string> domains, string whenOff, HttpMessageHandler? handler = null, string baseUrl = "https://api.forwardemail.net")
        : base(baseUrl, handler)
    {
        Basic(Http, token.Trim(), "");
        _forwardTo = forwardTo;
        _domains = domains;
        _offCode = whenOff switch { "bounce" => 550, "defer" => 421, _ => 250 };
    }

    public override ServiceInfo Info => Service;

    public async Task<List<string>> DomainsAsync(CancellationToken ct = default)
    {
        if (_domains.Count > 0) return _domains;
        var res = await SendAsync(HttpMethod.Get, "/v1/domains?paginate=true&limit=50", null, ct);
        return (res as JsonArray ?? []).Select(d => S(d?["name"])).Where(n => n.Length > 0).ToList();
    }

    public Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default) => Task.FromResult(_forwardTo.ToList());

    private static string D(string domain) => Uri.EscapeDataString(domain);

    public override async Task<List<Alias>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<Alias>();
        foreach (var domain in await DomainsAsync(ct))
        {
            var seen = new HashSet<string>();
            for (int page = 1; page <= 100; page++)
            {
                var res = await SendAsync(HttpMethod.Get, $"/v1/domains/{D(domain)}/aliases?paginate=true&page={page}&limit=50", null, ct);
                var list = (res as JsonArray ?? []).OfType<JsonNode>().Where(a => seen.Add(S(a["id"]))).ToList();
                all.AddRange(list.Where(a => S(a["name"]) is { Length: > 0 } n && n != "*" && !n.StartsWith('/')).Select(a => ToAlias(a, domain)));
                if (list.Count < 50) break;
            }
        }
        return all;
    }

    private static Alias ToAlias(JsonNode a, string domain) => new()
    {
        ProviderKey = ProviderKey,
        Id = domain + "/" + S(a["id"]),
        Email = $"{S(a["name"])}@{domain}",
        Enabled = B(a["is_enabled"], true),
        State = B(a["is_enabled"], true) ? "enabled" : "disabled",
        Description = S(a["description"]),
        Created = Date(a["created_at"]),
        ForwardsTo = (a["recipients"] as JsonArray)?.FirstOrDefault()?.ToString(),
    };

    private static (string Domain, string Id) Split(Alias a)
    {
        var slash = a.Id.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? (a.Id[..slash], a.Id[(slash + 1)..]) : (a.Email[(a.Email.LastIndexOf('@') + 1)..], a.Id);
    }

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var domains = await DomainsAsync(ct);
        var domain = domains.FirstOrDefault(d => d.Equals(req.Domain, StringComparison.OrdinalIgnoreCase)) ?? domains.FirstOrDefault()
            ?? throw new ProviderException("Forward Email: no domain to create the alias on. Add one in Settings or at forwardemail.net.");
        var to = req.ForwardTo.Length > 0 ? [req.ForwardTo] : _forwardTo;
        if (to.Count == 0) throw new ProviderException("Forward Email: set where new aliases forward to in Settings.");
        var note = string.IsNullOrWhiteSpace(req.Site) ? req.Description.Trim() : $"{req.Description.Trim()} ({req.Site.Trim()})".Trim();
        var res = await SendAsync(HttpMethod.Post, $"/v1/domains/{D(domain)}/aliases", new JsonObject
        {
            ["name"] = RandomName.Or(req.Prefix),
            ["recipients"] = new JsonArray(to.Select(t => (JsonNode)t).ToArray()),
            ["description"] = note,
            ["is_enabled"] = true,
            ["error_code_if_disabled"] = _offCode,
        }, ct);
        return ToAlias(res ?? throw new ProviderException("Forward Email returned an empty response."), domain);
    }

    public override async Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default)
    {
        var (domain, id) = Split(alias);
        await SendAsync(HttpMethod.Put, $"/v1/domains/{D(domain)}/aliases/{Uri.EscapeDataString(id)}", new JsonObject { ["is_enabled"] = enabled, ["error_code_if_disabled"] = _offCode }, ct);
        alias.Enabled = enabled; alias.State = enabled ? "enabled" : "disabled";
    }

    public override async Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        var (domain, id) = Split(alias);
        await SendAsync(HttpMethod.Put, $"/v1/domains/{D(domain)}/aliases/{Uri.EscapeDataString(id)}", new JsonObject { ["description"] = description.Trim() }, ct);
        alias.Description = description.Trim();
    }

    public override async Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        var (domain, id) = Split(alias);
        await SendAsync(HttpMethod.Delete, $"/v1/domains/{D(domain)}/aliases/{Uri.EscapeDataString(id)}", null, ct);
        alias.State = "deleted"; alias.Enabled = false;
    }

    /// <summary>A display name without quotes or line breaks (no header injection).</summary>
    private static string Clean(string name) => new(name.Where(c => c != '"' && !char.IsControl(c)).ToArray());

    /// <summary>Sends through Forward Email's outbound API, from the alias itself (inline images stay as data: URLs,
    /// which Forward Email turns into attachments; files go as base64).</summary>
    public async Task SendAsync(Alias from, OutgoingMessage m, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["from"] = string.IsNullOrWhiteSpace(m.FromName) ? from.Email : $"\"{Clean(m.FromName)}\" <{from.Email}>",
            ["to"] = new JsonArray(m.To.Select(t => (JsonNode)t).ToArray()),
            ["subject"] = m.Subject,
            ["html"] = m.Html,
            ["text"] = HtmlTools.ToPlainText(m.Html),
            ["attachDataUrls"] = true,
        };
        if (m.Cc.Count > 0) body["cc"] = new JsonArray(m.Cc.Select(t => (JsonNode)t).ToArray());
        if (m.InReplyTo != null) body["inReplyTo"] = m.InReplyTo;
        if (m.References.Count > 0) body["references"] = string.Join(" ", m.References);
        if (m.Attachments.Count > 0)
            body["attachments"] = new JsonArray(m.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["filename"] = a.Name, ["content"] = Convert.ToBase64String(a.Data), ["encoding"] = "base64", ["contentType"] = a.Type,
            }).ToArray());
        await SendAsync(HttpMethod.Post, "/v1/emails", body, ct);
    }
}
