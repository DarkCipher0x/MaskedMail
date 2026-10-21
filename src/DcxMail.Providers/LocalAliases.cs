using DcxMail.Services;

namespace DcxMail.Providers;

/// <summary>The addresses the app remembers itself (see <see cref="LocalAlias"/>), kept in the encrypted settings.</summary>
public static class LocalAliases
{
    public static IEnumerable<LocalAlias> For(string providerKey) =>
        AppSettings.Current.LocalAliases.Where(a => a.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase)).ToList();

    public static LocalAlias? Find(string providerKey, string email) =>
        For(providerKey).FirstOrDefault(a => a.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

    public static void Add(LocalAlias a)
    {
        AppSettings.Current.LocalAliases.RemoveAll(x => x.ProviderKey == a.ProviderKey && x.Email.Equals(a.Email, StringComparison.OrdinalIgnoreCase));
        AppSettings.Current.LocalAliases.Add(a);
        AppSettings.Save();
    }

    public static void Remove(string providerKey, string email)
    {
        if (AppSettings.Current.LocalAliases.RemoveAll(x => x.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase) && x.Email.Equals(email, StringComparison.OrdinalIgnoreCase)) > 0)
            AppSettings.Save();
    }

    /// <summary>Saves a changed record (a refreshed token, a note).</summary>
    public static void Update() => AppSettings.Save();

    public static Alias ToAlias(LocalAlias a) => new()
    {
        ProviderKey = a.ProviderKey,
        Id = a.Id,
        Email = a.Email,
        Description = a.Description,
        Site = a.Site,
        Created = a.Created,
        Expires = a.Expires,
        Enabled = a.Expires is not { } e || e > DateTimeOffset.Now,
        State = a.Expires is { } x && x <= DateTimeOffset.Now ? "expired" : "enabled",
    };
}
