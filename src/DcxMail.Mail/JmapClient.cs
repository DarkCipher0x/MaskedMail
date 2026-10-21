using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DcxMail.Services;

namespace DcxMail.Mail;

public sealed class JmapException(string message, string? type = null) : Exception(message)
{
    public string? Type { get; } = type;
}

/// <summary>
/// A small JMAP client (RFC 8620/8621) for Fastmail: session discovery, batched method calls with back-references,
/// blob upload and download. The HTTP handler is injectable so the self-test can run against an in-process fake.
/// </summary>
public sealed class JmapClient : IDisposable
{
    public const string Core = "urn:ietf:params:jmap:core";
    public const string MailCap = "urn:ietf:params:jmap:mail";
    public const string Submission = "urn:ietf:params:jmap:submission";
    public const string MaskedEmailCap = "https://www.fastmail.com/dev/maskedemail";

    private readonly HttpClient _http;
    private readonly string _sessionUrl;
    private JsonObject? _session;

    public JmapClient(string sessionUrl, string token, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _sessionUrl = sessionUrl;
        _http = Services.Net.Client(handler, timeout ?? TimeSpan.FromSeconds(60));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    public string AccountId { get; private set; } = "";
    public string Username { get; private set; } = "";
    public bool HasMaskedEmail { get; private set; }
    public bool HasSubmission { get; private set; }
    public bool HasMail { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_session != null) return;
        if (!Services.Net.IsHttps(_sessionUrl)) throw new JmapException("The JMAP session URL must start with https:// (the token is only ever sent over HTTPS).");
        using var res = await _http.GetAsync(_sessionUrl, ct).Timed(_http, "Fastmail", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new JmapException("Fastmail refused the API token (401). Check it in Settings, and that it has not expired or been revoked.");
        res.EnsureSuccessStatusCode();
        var session = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!.AsObject();
        // The token goes to every URL the session names: each must be HTTPS, or nothing is sent.
        foreach (var key in new[] { "apiUrl", "uploadUrl", "downloadUrl" })
        {
            var u = session[key]?.GetValue<string>();
            var probe = u?.Replace("{accountId}", "a").Replace("{blobId}", "b").Replace("{type}", "t").Replace("{name}", "n");
            if (!Services.Net.IsHttps(probe)) throw new JmapException($"The server's {key} is not an https:// address; refusing to send your token to it.");
        }
        _session = session;
        Username = _session["username"]?.GetValue<string>() ?? "";
        var primary = _session["primaryAccounts"]!.AsObject();
        AccountId = (primary[MailCap] ?? primary[MaskedEmailCap] ?? primary.First().Value)!.GetValue<string>();
        var caps = _session["capabilities"]!.AsObject();
        HasMail = caps.ContainsKey(MailCap);
        HasSubmission = caps.ContainsKey(Submission);
        HasMaskedEmail = caps.ContainsKey(MaskedEmailCap);
    }

    private string ApiUrl => _session!["apiUrl"]!.GetValue<string>();

    /// <summary>Runs method calls in one request. Each call is (method, args); call ids are "c0", "c1"… so later calls
    /// can back-reference earlier ones with <see cref="Ref"/>. Returns the response arguments in call order; a method
    /// error throws.</summary>
    public async Task<List<JsonObject>> CallAsync(IEnumerable<(string Method, JsonObject Args)> calls, CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        var list = calls.ToList();
        var methodCalls = new JsonArray();
        for (int i = 0; i < list.Count; i++)
        {
            var args = list[i].Args;
            if (!args.ContainsKey("accountId")) args["accountId"] = AccountId;
            methodCalls.Add(new JsonArray(list[i].Method, args, "c" + i));
        }
        var body = new JsonObject
        {
            ["using"] = new JsonArray(Core, MailCap, Submission, MaskedEmailCap),
            ["methodCalls"] = methodCalls,
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync(ApiUrl, content, ct).Timed(_http, "Fastmail", ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new JmapException($"Fastmail API error {(int)res.StatusCode}: {Trim(text)}");
        var responses = JsonNode.Parse(text)!["methodResponses"]!.AsArray();
        var results = new List<JsonObject>();
        foreach (var r in responses)
        {
            var name = r![0]!.GetValue<string>();
            var args = r[1]!.AsObject();
            if (name == "error")
            {
                var type = args["type"]?.GetValue<string>();
                throw new JmapException($"Fastmail: {type} {args["description"]?.GetValue<string>()}".Trim(), type);
            }
            results.Add(args);
        }
        return results;
    }

    public async Task<JsonObject> CallOneAsync(string method, JsonObject args, CancellationToken ct = default) =>
        (await CallAsync([(method, args)], ct))[0];

    /// <summary>A JMAP result reference to call "c{index}".</summary>
    public static JsonObject Ref(int index, string name, string path) =>
        new() { ["resultOf"] = "c" + index, ["name"] = name, ["path"] = path };

    /// <summary>Throws a readable error when a /set response has notCreated / notUpdated / notDestroyed entries.</summary>
    public static void CheckSet(JsonObject res, string what)
    {
        foreach (var key in new[] { "notCreated", "notUpdated", "notDestroyed" })
            if (res[key] is JsonObject bad && bad.Count > 0)
            {
                var err = bad.First().Value!;
                var type = err["type"]?.GetValue<string>();
                var desc = err["description"]?.GetValue<string>();
                var props = err["properties"] is JsonArray p ? " (" + string.Join(", ", p.Select(x => x!.GetValue<string>())) + ")" : "";
                throw new JmapException($"{what} failed: {type}{props} {desc}".Trim(), type);
            }
    }

    public async Task<string> UploadAsync(byte[] data, string type, CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        var url = _session!["uploadUrl"]!.GetValue<string>().Replace("{accountId}", Uri.EscapeDataString(AccountId));
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var res = await _http.PostAsync(url, content, ct).Timed(_http, "Fastmail", ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new JmapException($"Upload failed ({(int)res.StatusCode}): {Trim(text)}");
        return JsonNode.Parse(text)!["blobId"]!.GetValue<string>();
    }

    public async Task<byte[]> DownloadAsync(string blobId, string type, string name, CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        var url = _session!["downloadUrl"]!.GetValue<string>()
            .Replace("{accountId}", Uri.EscapeDataString(AccountId))
            .Replace("{blobId}", Uri.EscapeDataString(blobId))
            .Replace("{type}", Uri.EscapeDataString(type))
            .Replace("{name}", Uri.EscapeDataString(string.IsNullOrEmpty(name) ? "file" : name));
        using var res = await _http.GetAsync(url, ct).Timed(_http, "Fastmail", ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose() => _http.Dispose();
}
