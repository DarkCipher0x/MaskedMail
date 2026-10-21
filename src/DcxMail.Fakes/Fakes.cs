using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using DcxMail.Mail;

namespace DcxMail.Fakes;

// In-process fake servers. They answer like the real services for the calls the app makes, and never touch the network.

public sealed class FakeRest : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, (HttpStatusCode, string)>> Routes { get; } = [];
    public Action<HttpRequestMessage>? OnSend { get; set; }
    /// <summary>Location sent with any 3xx answer (hardening test).</summary>
    public string? RedirectTo { get; set; }
    /// <summary>Every request seen: method, full URL, body (for asserting what was sent).</summary>
    public List<(string Method, string Url, string Body, string? Auth)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        OnSend?.Invoke(request);
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), request.Content?.ReadAsStringAsync(ct).Result ?? "", request.Headers.Authorization?.ToString()));
        var key = request.Method.Method + " " + request.RequestUri!.AbsolutePath;
        var (code, body) = Routes.TryGetValue(key, out var h) ? h(request) : (HttpStatusCode.NotFound, "{\"error\":\"no route " + key + "\"}");
        var res = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if ((int)code is >= 300 and < 400 && RedirectTo != null) res.Headers.Location = new Uri(RedirectTo);
        return Task.FromResult(res);
    }
}

public sealed record FakeBox(string Id, string Name, string? Parent, string? Role);
public sealed class FakeEmail(string id, string box, string json)
{
    public string Id { get; } = id;
    public string Box { get; set; } = box;
    public JsonObject Props { get; } = JsonNode.Parse(json)!.AsObject();
}

/// <summary>Enough of Fastmail's JMAP for the flows the app uses.</summary>
public sealed class FakeJmap : HttpMessageHandler
{
    public bool MaskedEmailScope { get; init; } = true;
    /// <summary>Hardening test: the session names a plain-http API URL.</summary>
    public string ApiUrl { get; init; } = "https://fake.test/jmap/api/";
    public List<FakeBox> Boxes { get; } = [new("inbox", "Inbox", null, "inbox"), new("drafts", "Drafts", null, "drafts"), new("sent", "Sent", null, "sent"), new("trash", "Trash", null, "trash")];
    public List<JsonObject> Masked { get; } = [JsonNode.Parse("""{"id":"m0","email":"old123@fastmail.com","state":"enabled","description":"old","forDomain":"","createdAt":"2026-01-01T00:00:00Z"}""")!.AsObject()];
    public List<FakeEmail> Emails { get; } = [];
    public List<string> Identities { get; } = ["me@fm.com"];
    public JsonObject? LastMaskedCreate, LastEmailCreate, LastSubmission, LastOnSuccess;
    public int Uploads, Submissions, MailboxGets;
    private int _n;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (req.Headers.Authorization?.Scheme != "Bearer") return Json(HttpStatusCode.Unauthorized, "{}");
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/jmap/session")
        {
            var caps = new JsonObject { [JmapClient.Core] = new JsonObject(), [JmapClient.MailCap] = new JsonObject(), [JmapClient.Submission] = new JsonObject() };
            if (MaskedEmailScope) caps[JmapClient.MaskedEmailCap] = new JsonObject();
            var s = new JsonObject
            {
                ["username"] = "me@fm.com", ["capabilities"] = caps,
                ["primaryAccounts"] = new JsonObject { [JmapClient.MailCap] = "u1" },
                ["apiUrl"] = ApiUrl, ["uploadUrl"] = "https://fake.test/jmap/upload/{accountId}/",
                ["downloadUrl"] = "https://fake.test/jmap/download/{accountId}/{blobId}/{name}?type={type}",
            };
            return Json(HttpStatusCode.OK, s.ToJsonString());
        }
        if (path.StartsWith("/jmap/upload/")) { Uploads++; return Json(HttpStatusCode.Created, $"{{\"blobId\":\"B{++_n}\"}}"); }
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
        var responses = new JsonArray();
        var results = new Dictionary<string, JsonObject>();
        foreach (var call in body["methodCalls"]!.AsArray())
        {
            var (name, args, id) = (call![0]!.GetValue<string>(), call[1]!.AsObject(), call[2]!.GetValue<string>());
            // resolve "#ids" back-references
            if (args["#ids"] is JsonObject r) args["ids"] = results[r["resultOf"]!.GetValue<string>()]["ids"]!.DeepClone();
            var res = Handle(name, args);
            results[id] = res;
            responses.Add(new JsonArray(res.ContainsKey("type") && !res.ContainsKey("accountId") ? "error" : name, res, id));
        }
        return Json(HttpStatusCode.OK, new JsonObject { ["methodResponses"] = responses }.ToJsonString());
    }

    private JsonObject Handle(string name, JsonObject a)
    {
        var ok = new JsonObject { ["accountId"] = "u1" };
        switch (name)
        {
            case "MaskedEmail/get":
                if (!MaskedEmailScope) return new JsonObject { ["type"] = "unknownMethod" };
                ok["list"] = new JsonArray(Masked.Select(m => (JsonNode)m.DeepClone()).ToArray());
                return ok;
            case "MaskedEmail/set":
                if (a["create"] is JsonObject c)
                {
                    var (k, v) = c.First(); LastMaskedCreate = v!.AsObject();
                    var prefix = LastMaskedCreate["emailPrefix"]?.GetValue<string>() ?? "rand";
                    var m = new JsonObject { ["id"] = "m" + (++_n), ["email"] = $"{prefix}.{_n}x@fastmail.com", ["createdAt"] = DateTime.UtcNow.ToString("o") };
                    var stored = m.DeepClone().AsObject();
                    foreach (var (pk, pv) in LastMaskedCreate) if (pk != "emailPrefix") stored[pk] = pv?.DeepClone();
                    Masked.Add(stored);
                    ok["created"] = new JsonObject { [k] = m };
                }
                if (a["update"] is JsonObject u)
                    foreach (var (id, patch) in u)
                        foreach (var (pk, pv) in patch!.AsObject()) Masked.Single(m => m["id"]!.GetValue<string>() == id)[pk] = pv?.DeepClone();
                return ok;
            case "Mailbox/get":
                MailboxGets++;
                ok["list"] = new JsonArray(Boxes.Select(b => (JsonNode)new JsonObject { ["id"] = b.Id, ["name"] = b.Name, ["parentId"] = b.Parent, ["role"] = b.Role, ["totalEmails"] = 0, ["unreadEmails"] = 0 }).ToArray());
                return ok;
            case "Mailbox/set" when a["destroy"] is JsonArray destroy:
            {
                // Like the real server: a folder that still holds mail is refused unless onDestroyRemoveEmails.
                var destroyed = new JsonArray();
                foreach (var d in destroy.Select(x => x!.GetValue<string>()))
                {
                    if (Emails.Any(e => e.Box == d)) { ok["notDestroyed"] = new JsonObject { [d] = new JsonObject { ["type"] = "mailboxHasEmail" } }; continue; }
                    Boxes.RemoveAll(b => b.Id == d); destroyed.Add(d);
                }
                ok["destroyed"] = destroyed;
                return ok;
            }
            case "Mailbox/set":
            {
                var (k, v) = a["create"]!.AsObject().First();
                var nb = new FakeBox("mb" + (++_n), v!["name"]!.GetValue<string>(), v["parentId"]?.GetValue<string>(), null);
                if (nb.Parent != null && Boxes.All(b => b.Id != nb.Parent)) { ok["notCreated"] = new JsonObject { [k] = new JsonObject { ["type"] = "invalidProperties" } }; return ok; }
                Boxes.Add(nb);
                ok["created"] = new JsonObject { [k] = new JsonObject { ["id"] = nb.Id } };
                return ok;
            }
            case "Email/query":
            {
                var inBox = a["filter"]!["inMailbox"]!.GetValue<string>();
                ok["ids"] = new JsonArray(Emails.Where(e => e.Box == inBox).Select(e => (JsonNode)e.Id).ToArray());
                return ok;
            }
            case "Email/get":
                ok["list"] = new JsonArray(Emails.Where(e => a["ids"]!.AsArray().Any(i => i!.GetValue<string>() == e.Id))
                    .Select(e => { var o = e.Props.DeepClone().AsObject(); o["id"] = e.Id; return (JsonNode)o; }).ToArray());
                return ok;
            case "Email/set":
                if (a["update"] is JsonObject eu)
                    foreach (var (id, patch) in eu)
                        foreach (var (pk, pv) in patch!.AsObject())
                            if (pk.StartsWith("mailboxIds/") && pv?.GetValue<bool>() == true)
                            {
                                var target = pk["mailboxIds/".Length..];
                                if (Boxes.All(b => b.Id != target)) { ok["notUpdated"] = new JsonObject { [id] = new JsonObject { ["type"] = "invalidProperties" } }; return ok; }
                                Emails.Single(e => e.Id == id).Box = target;
                            }
                            else if (pk == "mailboxIds" && pv is JsonObject set && set.Count > 0)
                                Emails.Single(e => e.Id == id).Box = set.First().Key;
                if (a["create"] is JsonObject ec) { LastEmailCreate = ec.First().Value!.AsObject(); ok["created"] = new JsonObject { ["draft"] = new JsonObject { ["id"] = "E" + (++_n) } }; }
                return ok;
            case "Identity/get":
                ok["list"] = new JsonArray(Identities.Select((e, i) => (JsonNode)new JsonObject { ["id"] = "I" + i, ["name"] = "", ["email"] = e }).ToArray());
                return ok;
            case "Identity/set":
                Identities.Add(a["create"]!["i"]!["email"]!.GetValue<string>());
                ok["created"] = new JsonObject { ["i"] = new JsonObject { ["id"] = "I" + (Identities.Count - 1) } };
                return ok;
            case "EmailSubmission/set":
                Submissions++;
                LastSubmission = a["create"]!["send"]!.AsObject();
                LastOnSuccess = a["onSuccessUpdateEmail"]?.AsObject();
                ok["created"] = new JsonObject { ["send"] = new JsonObject { ["id"] = "S1" } };
                return ok;
        }
        return new JsonObject { ["type"] = "unknownMethod" };
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
