using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxMail.Services;

namespace DcxMail.Providers;

/// <summary>
/// The base for every web-API service: JSON over HTTPS through the app's one hardened client (HTTPS only, redirects
/// refused so a key can't follow one to another host, TLS 1.2+; see Services/Net.cs), responses capped in size, and
/// errors turned into plain sentences. Operations a service doesn't have fail with a clear message; each service only
/// overrides what it supports.
/// </summary>
public abstract class RestProvider : IAliasProvider, IDisposable
{
    /// <summary>No API answer we use comes near this; anything bigger is refused rather than buffered.</summary>
    private const long MaxResponseBytes = 16 * 1024 * 1024;

    protected readonly HttpClient Http;
    protected readonly string BaseUrl;

    protected RestProvider(string baseUrl, HttpMessageHandler? handler)
    {
        BaseUrl = baseUrl.Trim().TrimEnd('/');
        Http = Services.Net.Client(handler, TimeSpan.FromSeconds(45));
        Http.MaxResponseContentBufferSize = MaxResponseBytes;
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public abstract ServiceInfo Info { get; }
    public string Key => Info.Key;
    public string Name => Info.Name;
    public SendMode SendMode => Info.SendMode;

    // ---- the operations; unsupported ones say so ----

    public virtual Task<List<Alias>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(LocalAliases.For(Key).Select(LocalAliases.ToAlias).ToList());

    public abstract Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default);

    public virtual Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default) =>
        throw Unsupported(enabled ? "turn addresses back on" : "turn addresses off");

    public virtual Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default) =>
        throw Unsupported("keep a note on an address");

    public virtual Task DeleteAsync(Alias alias, CancellationToken ct = default) =>
        throw Unsupported("delete addresses");

    public virtual Task<string> ReverseAddressAsync(Alias alias, string recipient, CancellationToken ct = default) =>
        throw Unsupported("send to new contacts");

    protected ProviderException Unsupported(string what) => new($"{Name} can't {what} through its API.");

    // ---- HTTP ----

    protected Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken ct = default) =>
        SendAsync(method, path, body, "application/json", ct);

    protected Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, string contentType, CancellationToken ct) =>
        SendAsync(method, path, body, contentType, bearer: null, ct);

    /// <summary>With <paramref name="bearer"/>: a per-address token for this one request (disposable inboxes).</summary>
    protected async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, string contentType, string? bearer, CancellationToken ct)
    {
        using var req = Request(method, path, bearer);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType);
        using var res = await Http.SendAsync(req, ct).Timed(Http, Name, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw Failure((int)res.StatusCode, text);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { throw new ProviderException($"{Name} sent an answer that isn't JSON."); }
    }

    /// <summary>A file (an attachment) from the service, size-capped like every answer.</summary>
    protected async Task<byte[]> GetBytesAsync(string path, string? bearer, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, path, bearer);
        using var res = await Http.SendAsync(req, ct).Timed(Http, Name, ct);
        if (!res.IsSuccessStatusCode) throw Failure((int)res.StatusCode, "");
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? bearer)
    {
        // Paths are relative to the service; a full URL is only accepted on the service's own host.
        var url = path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : BaseUrl + path;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var b) || !u.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase))
            throw new ProviderException($"{Name}: refused a link to another server ({(u?.Host ?? "?")}).");
        var req = new HttpRequestMessage(method, u);
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    protected ProviderException Failure(int status, string body)
    {
        string detail = body;
        try
        {
            var j = JsonNode.Parse(body);
            detail = j?["error"]?.ToString() ?? j?["message"]?.ToString() ?? j?["detail"]?.ToString()
                     ?? j?["hydra:description"]?.ToString() ?? j?["errors"]?.ToJsonString() ?? body;
        }
        catch (JsonException) { }
        detail = detail.Trim();
        if (detail.Length > 300) detail = detail[..300] + "…";
        return new ProviderException(status switch
        {
            401 or 403 => $"{Name} refused the key ({status}). Check it in Settings." + (detail.Length > 0 && detail.Length < 160 ? " (" + detail + ")" : ""),
            404 => $"{Name}: not found (404). {detail}".Trim(),
            429 => $"{Name} is rate-limiting requests (429). Try again in a minute.",
            _ => $"{Name} error {status}: {detail}",
        }, status);
    }

    protected static void Basic(HttpClient http, string user, string password) =>
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));

    // ---- JSON helpers ----

    protected static DateTimeOffset? Date(JsonNode? n)
    {
        if (n == null) return null;
        if (n is JsonValue v && v.TryGetValue<long>(out var unix)) return unix > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
        return DateTimeOffset.TryParse(n.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    protected static string S(JsonNode? n) => n?.ToString() ?? "";

    protected static bool B(JsonNode? n, bool fallback)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<int>(out var i)) return i != 0;
        var s = v.ToString();
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || (s != "0" && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && fallback);
    }

    public void Dispose()
    {
        Http.Dispose();
        GC.SuppressFinalize(this);
    }
}
