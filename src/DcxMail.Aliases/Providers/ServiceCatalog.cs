namespace DcxMail.Providers;

/// <summary>
/// Every service the app can use besides Fastmail, in the order Settings shows them. Adding a service = one file in
/// DcxMail.Providers.Masked, .Domains or .Disposable with its <c>Service</c> description, plus one line here.
/// </summary>
public static class ServiceCatalog
{
    public static IReadOnlyList<ServiceInfo> All { get; } =
    [
        // Masked-email services
        SimpleLoginProvider.Service,
        AddyProvider.Service,
        FirefoxRelayProvider.Service,
        DuckDuckGoProvider.Service,
        // Your own domains
        ForwardEmailProvider.Service,
        CloudflareRoutingProvider.Service,
        ImprovMxProvider.Service,
        MigaduProvider.Service,
        MailcowProvider.Service,
        // Disposable inboxes
        MailTmProvider.Service,
        TempMailLolProvider.Service,
        DropMailProvider.Service,
    ];

    /// <summary>Fastmail plus every catalog service, for looking any of them up by key.</summary>
    public static ServiceInfo? Find(string key) =>
        key.Equals(FastmailProvider.ProviderKey, StringComparison.OrdinalIgnoreCase) ? FastmailProvider.Service
        : All.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static string FamilyTitle(ServiceFamily f) => f switch
    {
        ServiceFamily.Masked => "Masked-email services",
        ServiceFamily.OwnDomain => "Your own domains",
        _ => "Disposable inboxes",
    };

    /// <summary>Services that were looked at and deliberately left out, with the reason (shown in Settings and the docs).</summary>
    public static IReadOnlyList<(string Name, string Why)> NotIncluded { get; } =
    [
        ("Proton Pass", "Its aliases are SimpleLogin aliases: add your SimpleLogin API key and they appear here. Proton Pass itself has no public API."),
        ("Apple iCloud Hide My Email", "No public API."),
        ("Guerrilla Mail, Maildrop, Mailinator and Mailsac (public inboxes)", "Anyone who knows or guesses the address can read its inbox, so a password-reset link sent there could be read by a stranger. Guerrilla Mail's API is also documented as plain HTTP."),
        ("TrashMail.com", "Its API is not publicly documented, so it can't be checked."),
        ("33mail, Spamgourmet, Cloaked, Burner Mail, Blur", "No public, documented API for creating or managing addresses could be found."),
        ("Purelymail", "Routing rules are managed only in its web portal (no API)."),
    ];
}
