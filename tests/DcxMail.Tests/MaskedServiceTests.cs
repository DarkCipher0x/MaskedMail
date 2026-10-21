using System.Net;
using System.Text.Json.Nodes;
using DcxMail.Fakes;
using DcxMail.Providers;
using DcxMail.Services;
using Xunit;

namespace DcxMail.Tests;

/// <summary>The masked-email services' newer abilities: DuckDuckGo addresses, SimpleLogin suffix/mailbox choice and
/// activity, addy.io domain/recipient choice and name styles.</summary>
public sealed class MaskedServiceTests
{
    public MaskedServiceTests() => TestEnv.Fresh();

    [Fact]
    public async Task DuckDuckGo_creates_with_the_bearer_token_remembers_encrypted_and_only_forgets()
    {
        var fake = new FakeRest();
        fake.Routes["POST /api/email/addresses"] = _ => (HttpStatusCode.Created, """{"address":"quiet-otter-42"}""");
        using var p = new DuckDuckGoProvider("ddg-token", fake, "https://quack.test");
        var a = await p.CreateAsync(new CreateRequest { Description = "forum" });
        Assert.Equal("quiet-otter-42@duck.com", a.Email);
        Assert.Equal("Bearer ddg-token", fake.Requests[0].Auth);
        Assert.Equal("POST", fake.Requests[0].Method);
        Assert.DoesNotContain("quiet-otter", File.ReadAllText(Path.Combine(SecureStore.DataDir, AppSettings.FileName)));

        var listed = await p.ListAsync();
        Assert.Equal("forum", Assert.Single(listed).Description);
        await p.DeleteAsync(listed[0]);
        Assert.Empty(await p.ListAsync());
        Assert.Single(fake.Requests);                                     // forgetting never calls DuckDuckGo
        await Assert.ThrowsAsync<ProviderException>(() => p.SetEnabledAsync(a, false));
    }

    [Theory]
    [InlineData("""{"address":""}""")]
    [InlineData("""{"address":"a@b.com"}""")]
    [InlineData("""{"nope":1}""")]
    public async Task DuckDuckGo_refuses_odd_answers(string body)
    {
        var fake = new FakeRest();
        fake.Routes["POST /api/email/addresses"] = _ => (HttpStatusCode.OK, body);
        using var p = new DuckDuckGoProvider("t", fake, "https://quack.test");
        await Assert.ThrowsAsync<ProviderException>(() => p.CreateAsync(new CreateRequest()));
        Assert.Empty(TestEnv.S.LocalAliases);
    }

    private static FakeRest SimpleLogin(Action<JsonNode?>? custom = null, Action<string>? patched = null)
    {
        var fake = new FakeRest();
        fake.Routes["GET /api/v5/alias/options"] = _ => (HttpStatusCode.OK, """{"can_create":true,"suffixes":[{"suffix":".pro@sl.co","signed_suffix":"S1","is_premium":true},{"suffix":".x@sl.co","signed_suffix":"S2","is_premium":false},{"suffix":"@mine.org","signed_suffix":"S3","is_premium":false,"is_custom":true}]}""");
        fake.Routes["GET /api/v2/mailboxes"] = _ => (HttpStatusCode.OK, """{"mailboxes":[{"id":7,"email":"other@x.org","default":false,"verified":true},{"id":8,"email":"me@fm.com","default":true,"verified":true},{"id":9,"email":"unverified@x.org","default":false,"verified":false}]}""");
        fake.Routes["POST /api/v3/alias/custom/new"] = r => { custom?.Invoke(JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)); return (HttpStatusCode.Created, """{"id":1,"email":"shop@mine.org","enabled":true}"""); };
        fake.Routes["POST /api/alias/random/new"] = _ => (HttpStatusCode.Created, """{"id":2,"email":"r@sl.co","enabled":true}""");
        fake.Routes["PATCH /api/aliases/2"] = r => { patched?.Invoke(r.Content!.ReadAsStringAsync().Result); return (HttpStatusCode.OK, "{}"); };
        fake.Routes["GET /api/aliases/2/activities"] = _ => (HttpStatusCode.OK, """{"activities":[{"action":"forward","from":"news@site.com","to":"r@sl.co","timestamp":1700000000},{"action":"block","from":"spam@x.com","to":"r@sl.co","timestamp":1700000100}]}""");
        return fake;
    }

    [Fact]
    public async Task SimpleLogin_offers_free_suffixes_first_and_verified_mailboxes_default_first()
    {
        using var p = new SimpleLoginProvider("https://sl.test", "k", SimpleLogin());
        Assert.Equal([".x@sl.co", "@mine.org", ".pro@sl.co"], await p.DomainsAsync());
        Assert.Equal(["me@fm.com", "other@x.org"], await p.ForwardTargetsAsync());
    }

    [Fact]
    public async Task SimpleLogin_custom_alias_uses_the_chosen_suffix_and_mailbox()
    {
        JsonNode? body = null;
        using var p = new SimpleLoginProvider("https://sl.test", "k", SimpleLogin(custom: b => body = b));
        await p.CreateAsync(new CreateRequest { Prefix = "Shop", Domain = "@mine.org", ForwardTo = "other@x.org" });
        Assert.Equal("S3", body?["signed_suffix"]?.GetValue<string>());
        Assert.Equal(7, body?["mailbox_ids"]?[0]?.GetValue<int>());
        Assert.Equal("shop", body?["alias_prefix"]?.GetValue<string>());
    }

    [Fact]
    public async Task SimpleLogin_random_alias_is_moved_to_a_chosen_mailbox_only_when_it_is_not_the_default()
    {
        string? patch = null;
        using var p = new SimpleLoginProvider("https://sl.test", "k", SimpleLogin(patched: b => patch = b));
        await p.CreateAsync(new CreateRequest { ForwardTo = "me@fm.com" });
        Assert.Null(patch);
        await p.CreateAsync(new CreateRequest { ForwardTo = "other@x.org" });
        Assert.Equal(7, JsonNode.Parse(patch!)?["mailbox_ids"]?[0]?.GetValue<int>());
    }

    [Fact]
    public async Task SimpleLogin_activity_lists_forwards_and_blocks()
    {
        using var p = new SimpleLoginProvider("https://sl.test", "k", SimpleLogin());
        var alias = new Alias { ProviderKey = "simplelogin", Id = "2", Email = "r@sl.co" };
        var act = await p.ActivityAsync(alias);
        Assert.Equal(["forward", "block"], act.Select(a => a.What));
        Assert.Equal("news@site.com", act[0].From);
    }

    [Fact]
    public async Task Addy_uses_the_chosen_domain_recipient_and_name_style()
    {
        var fake = new FakeRest();
        JsonNode? created = null;
        fake.Routes["GET /api/v1/domain-options"] = _ => (HttpStatusCode.OK, """{"data":["anonaddy.me","me.addy.io","mine.org"],"defaultAliasDomain":"me.addy.io","defaultAliasFormat":"random_characters"}""");
        fake.Routes["GET /api/v1/recipients"] = _ => (HttpStatusCode.OK, """{"data":[{"id":"r1","email":"me@fm.com"},{"id":"r2","email":"work@x.org"}]}""");
        fake.Routes["POST /api/v1/aliases"] = r => { created = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result); return (HttpStatusCode.Created, """{"data":{"id":"c","email":"x@mine.org","active":true}}"""); };
        using var p = new AddyProvider("https://addy.test", "k", fake) { Format = "random_words" };
        Assert.Equal(["me.addy.io", "anonaddy.me", "mine.org"], await p.DomainsAsync());
        Assert.Equal(["me@fm.com", "work@x.org"], await p.ForwardTargetsAsync());

        await p.CreateAsync(new CreateRequest { Domain = "mine.org", ForwardTo = "work@x.org" });
        Assert.Equal("mine.org", created?["domain"]?.GetValue<string>());
        Assert.Equal("random_words", created?["format"]?.GetValue<string>());
        Assert.Equal("r2", created?["recipient_ids"]?[0]?.GetValue<string>());

        await p.CreateAsync(new CreateRequest { Prefix = "Shop" });
        Assert.Equal("custom", created?["format"]?.GetValue<string>());
        Assert.Equal("me.addy.io", created?["domain"]?.GetValue<string>());
        Assert.Null(created?["recipient_ids"]);
    }

    [Fact]
    public async Task An_unknown_addy_name_style_is_not_sent()
    {
        var fake = new FakeRest();
        JsonNode? created = null;
        fake.Routes["GET /api/v1/domain-options"] = _ => (HttpStatusCode.OK, """{"data":["me.addy.io"],"defaultAliasDomain":"me.addy.io"}""");
        fake.Routes["POST /api/v1/aliases"] = r => { created = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result); return (HttpStatusCode.Created, """{"data":{"id":"c","email":"x@me.addy.io"}}"""); };
        using var p = new AddyProvider("https://addy.test", "k", fake) { Format = "<script>" };
        await p.CreateAsync(new CreateRequest());
        Assert.Null(created?["format"]);
    }
}
