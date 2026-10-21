using System.Net.Http;
using System.Net.Http.Headers;

namespace DcxMail.Providers;

/// <summary>
/// DuckDuckGo Email Protection — Private Duck Addresses (@duck.com) that forward to your real inbox with trackers
/// removed. DuckDuckGo publishes no API: this uses the one call its own browser extension makes (Bitwarden and
/// 1Password use the same), which can only create addresses. So the app remembers the ones it made (encrypted), and
/// turning one off is done from the DuckDuckGo footer of a forwarded email.
/// </summary>
public sealed class DuckDuckGoProvider : RestProvider
{
    public const string ProviderKey = "duckduckgo";

    public static readonly ServiceInfo Service = ServiceInfo.Define(new()
    {
        Key = ProviderKey,
        Name = "DuckDuckGo",
        Family = ServiceFamily.Masked,
        Features = Features.Create | Features.EditNote | Features.EditSite | Features.Remembered,
        SendMode = SendMode.None,
        Summary = "DuckDuckGo Email Protection: Private Duck Addresses (@duck.com) forward to your real inbox with email trackers removed. Free. Only creating addresses is possible; the app keeps its own list of the ones it made.",
        Setup = "Sign in at duckduckgo.com/email in a desktop browser, open Autofill, press Generate Private Duck Address with the developer tools' Network tab open, and copy the token after \"Bearer\" from the \"addresses\" request.",
        SetupUrl = "https://duckduckgo.com/email/settings/autofill",
        Caution = "Unofficial interface: DuckDuckGo has no public API. This is the call its own extension makes (password managers use it too), so DuckDuckGo could change it without notice. The token can create addresses on your Duck account — treat it like a password. To turn an address off, use the link in the DuckDuckGo footer of a forwarded email.",
        Fields = [new("token", "TOKEN", "The part after \"Bearer \".", Secret: true)],
        Create = (c, h) => new DuckDuckGoProvider(c["token"], h),
    });

    public DuckDuckGoProvider(string token, HttpMessageHandler? handler = null, string baseUrl = "https://quack.duckduckgo.com") : base(baseUrl, handler) =>
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

    public override ServiceInfo Info => Service;

    public override async Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, "/api/email/addresses", null, ct);
        var name = S(res?["address"]).Trim();
        if (name.Length == 0 || name.Contains('@', StringComparison.Ordinal) || name.Any(char.IsWhiteSpace))
            throw new ProviderException("DuckDuckGo did not return an address.");
        var local = new LocalAlias { ProviderKey = ProviderKey, Id = name, Email = name + "@duck.com", Description = req.Description.Trim(), Site = req.Site.Trim() };
        LocalAliases.Add(local);
        return LocalAliases.ToAlias(local);
    }

    /// <summary>Notes are kept by the app (DuckDuckGo has no notes).</summary>
    public override Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default)
    {
        if (LocalAliases.Find(ProviderKey, alias.Email) is { } l) { l.Description = description.Trim(); l.Site = site.Trim(); LocalAliases.Update(); }
        alias.Description = description.Trim(); alias.Site = site.Trim();
        return Task.CompletedTask;
    }

    /// <summary>Removes it from the app's list only: DuckDuckGo keeps forwarding until it is turned off there.</summary>
    public override Task DeleteAsync(Alias alias, CancellationToken ct = default)
    {
        LocalAliases.Remove(ProviderKey, alias.Email);
        alias.State = "removed from list";
        return Task.CompletedTask;
    }
}
