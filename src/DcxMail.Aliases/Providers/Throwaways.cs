using System.Net;
using System.Text.RegularExpressions;
using DcxMail.Mail;
using DcxMail.Services;

namespace DcxMail.Providers;

/// <summary>
/// Throwaway addresses: one click makes a random masked address with a lifetime, its own folder and a live inbox. When
/// it expires the address is deleted (mail to it then bounces) or disabled, and its mail goes to Trash with the folder
/// removed — as chosen in Settings → Throwaway addresses. Expiry is checked every 30 s while the app runs (the tray
/// is enough) and at every start, so one that ran out while the PC was off is dealt with at the next start.
/// </summary>
public static class Throwaways
{
    public static readonly (int Minutes, string Label)[] Lifetimes =
    {
        (10, "10 minutes"), (60, "1 hour"), (24 * 60, "1 day"), (7 * 24 * 60, "7 days"), (30 * 24 * 60, "30 days"),
    };

    public static event Action? Changed;

    public static ThrowawayInfo? Find(string email) => AppSettings.Current.Throwaways.TryGetValue(email, out var t) ? t : null;
    public static bool IsThrowaway(Alias a) => AppSettings.Current.Throwaways.ContainsKey(a.Email);

    public static string Describe(TimeSpan left) => ThrowawayInfo.Describe(left);

    /// <summary>"45m", "6h", "3d" or a bare number of minutes → a lifetime between 1 minute and 365 days, else null.</summary>
    public static TimeSpan? ParseLifetime(string text)
    {
        var s = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (s.Length == 0) return null;
        var unit = char.IsLetter(s[^1]) ? s[^1] : 'm';
        if (!double.TryParse(char.IsLetter(s[^1]) ? s[..^1] : s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        TimeSpan? span = unit switch { 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), 'd' => TimeSpan.FromDays(n), _ => null };
        return span is { } v && v >= TimeSpan.FromMinutes(1) && v <= TimeSpan.FromDays(365) ? v : null;
    }

    /// <summary>The service new throwaways use: the one chosen in Settings, else Fastmail, else the first that can create.</summary>
    public static IAliasProvider? DefaultProvider(ProviderRegistry reg) =>
        reg.Get(AppSettings.Current.ThrowawayProvider) is { } chosen && chosen.Info.Has(Features.Create) ? chosen
        : reg.Get(FastmailProvider.ProviderKey) ?? reg.Providers.FirstOrDefault(p => p.Info.Has(Features.Create));

    /// <summary>The lifetime a throwaway on this service can really have (disposable inboxes end on their own).</summary>
    public static TimeSpan Cap(IAliasProvider provider, TimeSpan life) =>
        provider.Info.MaxLifetime is { } max && life > max ? max : life;

    /// <summary>Creates a random throwaway address, gives it a folder (when its mail comes to Fastmail) and records its
    /// expiry. A service that ends addresses itself (mailcow time-limited aliases) is asked to, so it ends even if
    /// this app never runs again; a disposable inbox's lifetime is capped at what its service allows.</summary>
    public static async Task<(Alias Alias, ThrowawayInfo Info)> CreateAsync(ProviderRegistry reg, TimeSpan life, IAliasProvider? provider = null, string note = "", CancellationToken ct = default)
    {
        provider ??= DefaultProvider(reg) ?? throw new ProviderException("Add a masked-email service in Settings first.");
        if (!provider.Info.Has(Features.Create)) throw new ProviderException($"{provider.Name} can't create addresses.");
        if (life < TimeSpan.FromMinutes(1) || life > TimeSpan.FromDays(365)) throw new ArgumentOutOfRangeException(nameof(life), "A throwaway lives between 1 minute and 1 year.");
        life = Cap(provider, life);
        var expires = DateTimeOffset.Now + life;
        var req = new CreateRequest { Description = (note.Trim().Length > 0 ? note.Trim() + " · " : "") + $"Throwaway · expires {expires.LocalDateTime:d MMM HH:mm}" };
        if (provider.Info.Has(Features.NativeExpiry)) req.Lifetime = life;
        var alias = await provider.CreateAsync(req, ct);
        if (alias.Expires is { } own && own < expires) expires = own;
        reg.Aliases.Insert(0, alias);
        var info = new ThrowawayInfo { Email = alias.Email, ProviderKey = alias.ProviderKey, AliasId = alias.Id, Expires = expires };
        AppSettings.Current.Throwaways[alias.Email] = info;
        AppSettings.Save();
        // Its own folder, whatever the routing setting: the throwaway inbox shows that folder. (Disposable inboxes are
        // read at their service instead.)
        if (reg.Mail != null && !provider.Info.Has(Features.Inbox))
        {
            try { await reg.EnsureFolderAsync(alias, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { info.LastError = "folder: " + ex.Message; }
        }
        AuditLog.Write("throwaway address created", $"{alias.ProviderName}: {alias.Email} · expires {expires.LocalDateTime:yyyy-MM-dd HH:mm}");
        Changed?.Invoke();
        return (alias, info);
    }

    /// <summary>Whether a throwaway's time can be extended: not on services that end addresses themselves.</summary>
    public static bool CanExtend(string email) =>
        Find(email) is { } t && ServiceCatalog.Find(t.ProviderKey)?.Has(Features.NativeExpiry) != true;

    public static void Extend(string email, TimeSpan by)
    {
        if (Find(email) is not { } t) return;
        if (!CanExtend(email)) throw new ProviderException($"{ProviderRegistry.NameOf(t.ProviderKey)} ends its addresses itself, so this one can't be extended.");
        var from = t.Expired ? DateTimeOffset.Now : t.Expires;
        t.Expires = from + by;
        AppSettings.Save();
        AuditLog.Write("throwaway extended", $"{email} · now expires {t.Expires.LocalDateTime:yyyy-MM-dd HH:mm}");
        Changed?.Invoke();
    }

    /// <summary>Turns a throwaway into an ordinary masked address: it no longer expires.</summary>
    public static void Keep(string email)
    {
        if (!AppSettings.Current.Throwaways.Remove(email)) return;
        AppSettings.Save();
        AuditLog.Write("throwaway kept as a normal address", email);
        Changed?.Invoke();
    }

    /// <summary>Ends a throwaway now: delete or disable the address, then (if chosen) move its mail to Trash and remove
    /// its folder. The record stays, with the error, if the service can't be reached — the next check tries again.</summary>
    public static async Task<string> BurnAsync(ProviderRegistry reg, string email, string reason, CancellationToken ct = default)
    {
        var t = Find(email);
        var s = AppSettings.Current;
        var alias = reg.Aliases.FirstOrDefault(a => a.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        var provider = reg.Get(t?.ProviderKey ?? alias?.ProviderKey ?? "");
        var done = new List<string>();
        try
        {
            if (alias != null && provider != null)
                done.Add(await EndAtServiceAsync(reg, provider, alias, s.ThrowawayDelete, ct));
            else if (provider == null && t != null)
                throw new ProviderException($"{ProviderRegistry.NameOf(t.ProviderKey)} is no longer set up, so {email} couldn't be removed there.");
            if (s.ThrowawayCleanMail && reg.Mail != null && s.Folders.TryGetValue(email, out var folder))
            {
                var moved = await reg.Mail.EmptyAndRemoveFolderAsync(folder, MailService.FolderName(email), ct);
                s.Folders.Remove(email);
                done.Add(moved switch
                {
                    < 0 => "its folder was renamed or reused, so it was left as it is",
                    0 => "folder removed",
                    _ => $"{moved} message(s) moved to Trash, folder removed",
                });
            }
            else if (s.ThrowawayDelete) s.RouteOff.Add(email);
            s.Throwaways.Remove(email);
            AppSettings.Save();
            reg.MarkRouted();
            var what = done.Count > 0 ? string.Join(", ", done) : "already gone";
            AuditLog.Write("throwaway ended", $"{email} · {reason} · {what}");
            Changed?.Invoke();
            return what;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (t != null) { t.LastError = ex.Message; AppSettings.Save(); }
            AuditLog.Write("throwaway could not be ended", $"{email} · {ex.Message}");
            Changed?.Invoke();
            throw;
        }
    }

    /// <summary>Ends the address at its service in the strongest way the service has, and says what was done: delete
    /// (when chosen, or when it can't be turned off), else turn off. A service that can only be forgotten (DuckDuckGo)
    /// is said so plainly; one that already ended it by itself counts as done.</summary>
    private static async Task<string> EndAtServiceAsync(ProviderRegistry reg, IAliasProvider p, Alias a, bool preferDelete, CancellationToken ct)
    {
        var info = p.Info;
        try
        {
            if (info.Has(Features.Remembered) && !info.Has(Features.Delete))
            {
                await p.DeleteAsync(a, ct);
                reg.Aliases.Remove(a);
                return $"removed from the list — {p.Name} keeps forwarding it until you turn it off there";
            }
            if (info.Has(Features.Delete) && (preferDelete || !info.Has(Features.Disable)))
            {
                await p.DeleteAsync(a, ct);
                reg.Aliases.Remove(a);
                return "deleted";
            }
            if (info.Has(Features.Disable))
            {
                if (a.Enabled) await p.SetEnabledAsync(a, false, ct);
                return "disabled";
            }
            return $"{p.Name} can't delete or turn it off through its API — do that at {p.Name}";
        }
        catch (ProviderException e) when (e.Status is 404 or 410)
        {
            reg.Aliases.Remove(a);
            return "already ended at " + p.Name;
        }
    }

    /// <summary>Ends every expired throwaway. Returns the addresses ended; failures stay and are retried next time.</summary>
    public static async Task<List<string>> EnforceAsync(ProviderRegistry reg, CancellationToken ct = default)
    {
        var ended = new List<string>();
        foreach (var t in AppSettings.Current.Throwaways.Values.Where(t => t.Expired).ToList())
        {
            try { await BurnAsync(reg, t.Email, "expired", ct); ended.Add(t.Email); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* kept with LastError; retried next check */ }
        }
        return ended;
    }

    // ---------- reading what arrives ----------

    private static readonly Regex CodeNearWord = new(
        @"(?:code|otp|passcode|pin|verification|verify|one[-\s]?time|security|confirmation|login|sign[-\s]?in|2fa|token)\b[^\n\r\d]{0,40}?\b((?:[A-Z]{1,4}-)?\d{3,8}(?:[\s-]\d{3,4})?|(?=[A-Z]*\d)[A-Z0-9]{6,8})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeAlone = new(@"^\s*((?:[A-Z]{1,4}-)?\d{4,8}|\d{3}[\s-]\d{3})\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Likely verification codes in a message (near words like "code", "OTP", "verify", or alone on a line).
    /// Years, prices and times are skipped.</summary>
    public static List<string> FindCodes(string subject, string text)
    {
        var all = subject + "\n" + text;
        var found = new List<string>();
        void Add(string c)
        {
            c = c.Trim();
            var digits = new string(c.Where(char.IsDigit).ToArray());
            if (c.Length < 4 || found.Contains(c)) return;
            if (c.All(char.IsLetter)) return;                                          // a word, not a code
            if (digits.Length == 4 && int.TryParse(digits, out var y) && y is >= 1990 and <= 2100 && c.Length == 4) return;   // a year
            if (c.Any(char.IsLetter) && !c.Any(char.IsDigit)) return;
            found.Add(c);
        }
        foreach (Match m in CodeNearWord.Matches(all)) Add(m.Groups[1].Value);
        foreach (Match m in CodeAlone.Matches(text)) Add(m.Groups[1].Value);
        return found.Take(3).ToList();
    }

    private static readonly string[] LinkHints = { "verify", "verif", "confirm", "activate", "activation", "magic", "login", "signin", "sign-in", "auth", "token", "reset", "validate", "complete", "register", "onboard" };
    private static readonly string[] LinkNoise = { "unsubscribe", "privacy", "terms", "preferences", "help", "support", "facebook.", "twitter.", "x.com/", "instagram.", "linkedin.", "youtube.", ".png", ".jpg", ".gif" };

    /// <summary>Links worth acting on (verification, confirmation, sign-in), most likely first; footers and images skipped.</summary>
    public static List<string> FindLinks(string html, string text)
    {
        var urls = new List<string>();
        foreach (Match m in Regex.Matches(html, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)) urls.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
        foreach (Match m in Regex.Matches(text, @"https?://[^\s<>""')\]]+")) urls.Add(m.Value);
        return urls.Select(u => u.Trim()).Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(u => !LinkNoise.Any(n => u.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Select(u => (Url: u, Score: LinkHints.Count(h => u.Contains(h, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).Select(x => x.Url).Take(3).ToList();
    }
}
