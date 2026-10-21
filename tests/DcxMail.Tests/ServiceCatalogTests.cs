using System.Net;
using System.Text.Json;
using DcxMail.Fakes;
using DcxMail.Providers;
using DcxMail.Services;
using Xunit;

namespace DcxMail.Tests;

/// <summary>The rules every service must keep: what it declares matches what it implements, disposable inboxes are
/// opt-in with a caution, settings move over from 0.2, and no service can be sent to another host.</summary>
public sealed class ServiceCatalogTests
{
    public ServiceCatalogTests() => TestEnv.Fresh();

    public static TheoryData<string> Keys()
    {
        var d = new TheoryData<string>();
        foreach (var s in ServiceCatalog.All) d.Add(s.Key);
        return d;
    }

    /// <summary>Builds a service with every required field filled in.</summary>
    private static IAliasProvider Build(ServiceInfo info, HttpMessageHandler? handler = null)
    {
        var values = info.Fields.ToDictionary(f => f.Id, f => f.Id == "url" ? "https://example.test" : f.Required ? "x@example.test" : f.Default);
        return info.Create!(new ServiceConfig(info, values), handler ?? new FakeRest());
    }

    [Fact]
    public void Every_service_is_listed_once_with_a_name_setup_and_fields()
    {
        Assert.Equal(ServiceCatalog.All.Count, ServiceCatalog.All.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ServiceCatalog.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.Summary));
            Assert.False(string.IsNullOrWhiteSpace(s.Setup));
            Assert.NotNull(s.Create);
            Assert.True(s.SetupUrl == null || s.SetupUrl.StartsWith("https://", StringComparison.Ordinal), s.Key + ": set-up links must be https");
            Assert.Equal(s.Fields.Count, s.Fields.Select(f => f.Id).Distinct().Count());
        });
        Assert.Equal(12, ServiceCatalog.All.Count);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void What_a_service_declares_matches_what_it_implements(string key)
    {
        var info = ServiceCatalog.Find(key)!;
        var p = Build(info);
        try
        {
            Assert.Equal(key, p.Key);
            Assert.Equal(info.Has(Features.ChooseDomain), p is IDomainChoice);
            Assert.Equal(info.Has(Features.ForwardTo), p is IForwardChoice);
            Assert.Equal(info.Has(Features.Inbox), p is IInboxProvider);
            Assert.Equal(info.Has(Features.Activity), p is IActivityProvider);
            Assert.Equal(info.SendMode == SendMode.Service, p is ISendingProvider);
            Assert.True(info.Has(Features.List) || info.Has(Features.Remembered), key + " must either list its addresses or have the app remember them");
            Assert.True(info.Has(Features.Create));
        }
        finally { (p as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void Disposable_inboxes_and_unofficial_interfaces_are_opt_in_or_cautioned()
    {
        Assert.All(ServiceCatalog.All.Where(s => s.Family == ServiceFamily.Disposable), s =>
        {
            Assert.True(s.OptIn, s.Key + " must need an explicit tick");
            Assert.False(string.IsNullOrWhiteSpace(s.Caution));
            Assert.False(string.IsNullOrWhiteSpace(s.Attribution));
        });
        Assert.Contains("Unofficial", DuckDuckGoProvider.Service.Caution, StringComparison.Ordinal);
        // An opt-in service is off until it is ticked, even with nothing else to fill in.
        Assert.False(MailTmProvider.Service.IsConfigured(new Dictionary<string, string>()));
        Assert.True(MailTmProvider.Service.IsConfigured(new Dictionary<string, string> { [ServiceInfo.EnabledField] = "true" }));
        Assert.False(DropMailProvider.Service.IsConfigured(new Dictionary<string, string> { [ServiceInfo.EnabledField] = "true" }));   // still needs its token
    }

    [Fact]
    public void Only_configured_services_are_built()
    {
        using var reg = new ProviderRegistry(TestEnv.With(("addy", "key", "k"), ("mailtm", "server", "https://api.mail.tm"), ("duckduckgo", "token", "")), new FakeRest());
        Assert.Equal(["addy"], reg.Providers.Select(p => p.Key));
    }

    [Fact]
    public void Settings_from_0_2_move_into_the_service_list_and_are_never_written_back()
    {
        var json = """{"SimpleLoginKey":"sl-old","SimpleLoginUrl":"https://sl.self","AddyKey":"addy-old","RelayKey":"relay-old"}""";
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        s.Normalize();
        Assert.Equal("sl-old", s.Services["simplelogin"]["key"]);
        Assert.Equal("https://sl.self", s.Services["simplelogin"]["url"]);
        Assert.Equal("addy-old", s.Services["addy"]["key"]);
        Assert.Equal("relay-old", s.Services["relay"]["key"]);
        Assert.Null(s.SimpleLoginKey);
        var written = JsonSerializer.Serialize(s);
        Assert.DoesNotContain("SimpleLoginKey", written, StringComparison.Ordinal);
        Assert.DoesNotContain("AddyKey", written, StringComparison.Ordinal);
        using var reg = new ProviderRegistry(s, new FakeRest());
        Assert.Equal(["simplelogin", "addy", "relay"], reg.Providers.Select(p => p.Key));
    }

    [Fact]
    public void Service_keys_are_stored_encrypted()
    {
        TestEnv.S.Services["cloudflare"] = new() { ["token"] = "cf-SECRET-TOKEN", ["forward_to"] = "me@example.org" };
        AppSettings.Save();
        Assert.DoesNotContain("SECRET", File.ReadAllText(Path.Combine(SecureStore.DataDir, AppSettings.FileName)));
        AppSettings.Load();
        Assert.Equal("cf-SECRET-TOKEN", TestEnv.S.ServiceFields("CLOUDFLARE")["TOKEN"]);
    }

    [Fact]
    public async Task A_service_can_never_be_sent_to_another_host()
    {
        // Mail.tm hands out attachment links; one pointing elsewhere must not get the inbox's token.
        var fake = new FakeRest();
        using var p = new MailTmProvider("https://api.mail.test", fake);
        LocalAliases.Add(new LocalAlias { ProviderKey = MailTmProvider.ProviderKey, Id = "1", Email = "a@mail.test", Secrets = new() { ["token"] = "T", ["password"] = "P", ["server"] = "https://api.mail.test" } });
        var alias = LocalAliases.ToAlias(LocalAliases.Find(MailTmProvider.ProviderKey, "a@mail.test")!);
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.DownloadAsync(alias, new Mail.Attachment("https://evil.test/steal", "x", "x", 1, null, false)));
        Assert.Contains("another server", e.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void Random_names_are_unguessable_and_wanted_names_are_cleaned()
    {
        var names = Enumerable.Range(0, 200).Select(_ => RandomName.New()).ToList();
        Assert.Equal(200, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[a-z][a-z0-9]{11}$", n));
        Assert.Equal("shop.co-uk_1", RandomName.Clean(" Shop.Co-UK_1!! "));
        Assert.Equal(32, RandomName.Password().Length);
        Assert.Matches("^[a-z][a-z0-9]{11}$", RandomName.Or("!!!"));
    }

    [Fact]
    public void Services_left_out_are_listed_with_a_reason()
    {
        Assert.NotEmpty(ServiceCatalog.NotIncluded);
        Assert.All(ServiceCatalog.NotIncluded, n => Assert.False(string.IsNullOrWhiteSpace(n.Why)));
        Assert.Contains(ServiceCatalog.NotIncluded, n => n.Name.Contains("Guerrilla", StringComparison.Ordinal));
    }

    internal static string Json(object o) => JsonSerializer.Serialize(o);
    internal static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);
}
