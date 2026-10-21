using System.Net;
using System.Net.Http;
using System.Security.Authentication;

namespace DcxMail.Services;

/// <summary>
/// The one way this app makes HTTP connections (Fastmail, SimpleLogin, addy.io, Firefox Relay):
/// <list type="bullet">
/// <item>HTTPS only, TLS 1.2 or 1.3, the system's certificate validation (never overridden).</item>
/// <item><b>No redirects.</b> A redirect would carry the request's credentials somewhere else: .NET drops the standard
/// Authorization header on a redirect, but not SimpleLogin's "Authentication" header. A redirect is an error instead.</item>
/// <item>Bounded responses and timeouts, no cookies, no proxy credentials from the request.</item>
/// </list>
/// </summary>
public static class Net
{
    public const long MaxResponseBytes = 64L << 20;

    public static HttpClient Client(HttpMessageHandler? testHandler, TimeSpan timeout)
    {
        var http = testHandler != null
            ? new HttpClient(new RedirectGuard(testHandler, keepInner: true), disposeHandler: true)   // test fakes are shared
            : new HttpClient(new RedirectGuard(Handler(), keepInner: false), disposeHandler: true);
        http.Timeout = timeout;
        http.MaxResponseContentBufferSize = MaxResponseBytes;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DarkCipher-MaskedMail/" + AppInfo.Version);
        return http;
    }

    private static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        MaxResponseHeadersLength = 64,              // KB
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        // Deliberate floor, not a pin to old versions: the OS default can still allow TLS 1.0/1.1 on machines with
        // legacy policy, and every provider we talk to supports 1.2+. Revisit when TLS 1.4 exists.
#pragma warning disable CA5398
        SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
#pragma warning restore CA5398
    };

    /// <summary>Awaits a request, turning the client's own time limit into a <see cref="TimeoutException"/> that names the
    /// service. HttpClient reports a timeout as a cancellation, and the screens let cancellations through on purpose (the
    /// user stopped something), so a slow server used to reach the app's "Something went wrong" box. A cancel by the
    /// caller still throws <see cref="OperationCanceledException"/>.</summary>
    public static async Task<HttpResponseMessage> Timed(this Task<HttpResponseMessage> request, HttpClient http, string service, CancellationToken ct)
    {
        try { return await request; }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && ex.InnerException is TimeoutException)
        {
            throw new TimeoutException($"{service} didn't answer within {http.Timeout.TotalSeconds:0} seconds. Check your connection and try again; if you were changing or sending something, refresh first to see whether it went through.", ex);
        }
    }

    /// <summary>True for an absolute https:// URL.</summary>
    public static bool IsHttps(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>Refuses anything but https:// before a request leaves, and turns a redirect answer into an error
    /// (the test handler goes through it too, so the self-test checks the real rule).</summary>
    private sealed class RedirectGuard(HttpMessageHandler inner, bool keepInner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { if (!keepInner) base.Dispose(disposing); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { Scheme: "https" })
                throw new HttpRequestException($"Refused a non-HTTPS request to {request.RequestUri?.GetLeftPart(UriPartial.Authority)}: credentials are only sent over HTTPS.");
            var res = await base.SendAsync(request, ct);
            if ((int)res.StatusCode is >= 300 and < 400)
            {
                var to = res.Headers.Location?.ToString() ?? "(no location)";
                res.Dispose();
                throw new HttpRequestException($"The server answered with a redirect ({to}); redirects are not followed so your key is never sent anywhere else. Check the server address in Settings.");
            }
            return res;
        }
    }
}
