using System.Net.Http;

namespace DcxMail.Providers;

/// <summary>The three kinds of service, which is also how Settings groups them.</summary>
public enum ServiceFamily
{
    /// <summary>A masked-email service: addresses on its domains that forward to your real mailbox.</summary>
    Masked,
    /// <summary>Addresses on a domain you own, forwarded by a routing service or your own mail server.</summary>
    OwnDomain,
    /// <summary>A disposable inbox on the service's own servers, read inside the app. The service can read that mail.</summary>
    Disposable,
}

/// <summary>What a service can do; the app only offers what is set here.</summary>
[Flags]
public enum Features
{
    None = 0,
    /// <summary>Lists its addresses (otherwise the app remembers the ones it made: <see cref="Remembered"/>).</summary>
    List = 1 << 0,
    Create = 1 << 1,
    /// <summary>The address can start with a name you choose.</summary>
    CustomName = 1 << 2,
    /// <summary>You choose the domain or suffix (<see cref="IDomainChoice"/>).</summary>
    ChooseDomain = 1 << 3,
    /// <summary>You choose where its mail goes (<see cref="IForwardChoice"/>).</summary>
    ForwardTo = 1 << 4,
    EditNote = 1 << 5,
    EditSite = 1 << 6,
    Disable = 1 << 7,
    Delete = 1 << 8,
    /// <summary>The service ends the address by itself after a set time (mailcow time-limited aliases, disposable inboxes).</summary>
    NativeExpiry = 1 << 9,
    /// <summary>The mail is read from the service itself (<see cref="IInboxProvider"/>), not from Fastmail.</summary>
    Inbox = 1 << 10,
    /// <summary>Recent deliveries can be shown (<see cref="IActivityProvider"/>).</summary>
    Activity = 1 << 11,
    /// <summary>The service can't list addresses, so the app remembers the ones it made (encrypted).</summary>
    Remembered = 1 << 12,
}

/// <summary>One setting a service needs (an API key, a server, a domain…). Secret fields are shown masked; fields with
/// <paramref name="Choices"/> are a drop-down.</summary>
public sealed record ServiceField(string Id, string Label, string Hint = "", bool Secret = false, bool Required = true, string Default = "", string[]? Choices = null);

/// <summary>A service's saved settings, with defaults filled in.</summary>
public sealed class ServiceConfig(ServiceInfo info, IReadOnlyDictionary<string, string> values)
{
    public string this[string id] =>
        values.TryGetValue(id, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim()
        : info.Fields.FirstOrDefault(f => f.Id == id)?.Default ?? "";

    /// <summary>Comma, semicolon or newline separated values (several domains, for example).</summary>
    public List<string> List(string id) =>
        this[id].Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>
/// Everything the app needs to know about a service, declared by the service's own file: its name and kind, what it
/// can do, the settings it needs (Settings builds its card from these), where to get a key, and any caution to show.
/// </summary>
public sealed class ServiceInfo
{
    private static readonly Dictionary<string, ServiceInfo> Defined = new(StringComparer.OrdinalIgnoreCase);

    public required string Key { get; init; }
    public required string Name { get; init; }
    public required ServiceFamily Family { get; init; }
    public required Features Features { get; init; }
    /// <summary>One or two sentences: what it is and how its addresses work.</summary>
    public required string Summary { get; init; }
    /// <summary>Where to get the key or token, and what it needs to be allowed to do.</summary>
    public required string Setup { get; init; }
    public string? SetupUrl { get; init; }
    /// <summary>A security or privacy point the user should know before using it (shown on its card, highlighted).</summary>
    public string? Caution { get; init; }
    /// <summary>Needs an explicit "use this service" tick: no key at all, or an unofficial interface.</summary>
    public bool OptIn { get; init; }
    /// <summary>How the service's own terms ask to be credited, shown in the app where its mail is read.</summary>
    public string? Attribution { get; init; }
    /// <summary>The longest an address of this service lives (disposable inboxes), if it is limited.</summary>
    public TimeSpan? MaxLifetime { get; init; }
    public SendMode SendMode { get; init; } = SendMode.None;
    public IReadOnlyList<ServiceField> Fields { get; init; } = [];
    /// <summary>Builds the service from its saved settings (null for Fastmail, which the registry builds itself).</summary>
    public Func<ServiceConfig, HttpMessageHandler?, IAliasProvider>? Create { get; init; }

    public bool Has(Features f) => (Features & f) == f;

    /// <summary>Set up enough to use: every required field filled in, and ticked if it is opt-in.</summary>
    public bool IsConfigured(IReadOnlyDictionary<string, string> values) =>
        (!OptIn || (values.TryGetValue(EnabledField, out var on) && on == "true"))
        && Fields.Where(f => f.Required).All(f => values.TryGetValue(f.Id, out var v) && !string.IsNullOrWhiteSpace(v));

    /// <summary>The settings field that holds the "use this service" tick of opt-in services.</summary>
    public const string EnabledField = "enabled";

    /// <summary>Registers a service so its name can be shown anywhere from its key. Each service's file calls this
    /// once for its static <c>Info</c>.</summary>
    public static ServiceInfo Define(ServiceInfo info)
    {
        lock (Defined) Defined[info.Key] = info;
        return info;
    }

    public static ServiceInfo? Find(string key) { lock (Defined) return Defined.GetValueOrDefault(key); }
    public static string NameOf(string key) => Find(key)?.Name ?? key;
}
