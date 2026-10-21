namespace DcxMail.Providers;

/// <summary>How mail is sent "from" an address of this service.</summary>
public enum SendMode
{
    /// <summary>Fastmail: send straight from the masked address (a sending identity is created for it).</summary>
    Direct,
    /// <summary>SimpleLogin / addy.io: send from your real mailbox to a per-recipient reverse-alias address; the service
    /// rewrites it so the recipient sees the alias.</summary>
    ReverseAlias,
    /// <summary>The service sends it itself through its own API (Forward Email).</summary>
    Service,
    /// <summary>No sending to new contacts (Firefox Relay, DuckDuckGo, routing-only domains, disposable inboxes).</summary>
    None,
}

/// <summary>One masked, domain or disposable address, from any service.</summary>
public sealed class Alias
{
    public required string ProviderKey { get; init; }
    public required string Id { get; init; }
    public required string Email { get; init; }
    public bool Enabled { get; set; } = true;
    /// <summary>Service wording for the state ("enabled", "disabled", "pending", "deleted").</summary>
    public string State { get; set; } = "enabled";
    public string Description { get; set; } = "";
    /// <summary>The website it was made for (Fastmail forDomain, Relay generated_for).</summary>
    public string Site { get; set; } = "";
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? LastUsed { get; init; }
    /// <summary>When the service itself ends it (time-limited and disposable addresses).</summary>
    public DateTimeOffset? Expires { get; init; }
    public string Stats { get; init; } = "";
    /// <summary>Where its mail goes (SimpleLogin mailbox, addy.io recipient, a forwarding rule's target), when known.</summary>
    public string? ForwardsTo { get; init; }

    public string ProviderName => ServiceInfo.NameOf(ProviderKey);
    public string Title => string.IsNullOrWhiteSpace(Description) ? (string.IsNullOrWhiteSpace(Site) ? Email : Site) : Description;
    public string Subtitle => Email;
    public bool Routed { get; set; }
    /// <summary>"⏱ 54m" for a throwaway address (time left), else empty. Refreshed by the main window.</summary>
    public string ExpiryText { get; set; } = "";
}

/// <summary>What to create. Services use the parts they support (see <see cref="ServiceInfo.Features"/>).</summary>
public sealed class CreateRequest
{
    public string Description { get; set; } = "";
    public string Site { get; set; } = "";
    /// <summary>Wanted start of the address (Fastmail emailPrefix, SimpleLogin prefix, a custom name on a domain).
    /// Empty = the service picks, or a random name the app makes.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>Domain or suffix to use, from <see cref="IDomainChoice.DomainsAsync"/>. Empty = the service's default.</summary>
    public string Domain { get; set; } = "";
    /// <summary>Where its mail should go, from <see cref="IForwardChoice.ForwardTargetsAsync"/>. Empty = the default.</summary>
    public string ForwardTo { get; set; } = "";
    /// <summary>For services that end addresses by themselves (<see cref="Features.NativeExpiry"/>): how long it lives.</summary>
    public TimeSpan? Lifetime { get; set; }
}

/// <summary>A service said no, or could not be reached. <see cref="Status"/> is the HTTP status when there was one.</summary>
public sealed class ProviderException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}
