namespace DcxMail.Providers;

/// <summary>An address the app remembers itself, kept in the encrypted settings: one from a service that can't list
/// its addresses (DuckDuckGo), or a disposable inbox together with what it takes to open it again (account password,
/// token or session). (In Core so the settings model has no upward dependency.)</summary>
public sealed class LocalAlias
{
    public string ProviderKey { get; set; } = "";
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string Description { get; set; } = "";
    public string Site { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    /// <summary>When the service itself ends it (disposable inboxes), if it says.</summary>
    public DateTimeOffset? Expires { get; set; }
    /// <summary>The service's secrets for this address (password, token, session id). Never shown, never logged.</summary>
    public Dictionary<string, string> Secrets { get; set; } = [];
}
