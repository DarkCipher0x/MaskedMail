using System.Net;
using System.Text.Json.Nodes;
using DcxMail.Fakes;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;
using Xunit;

namespace DcxMail.Tests;

/// <summary>Disposable inboxes read inside the app: Mail.tm, TempMail.lol and DropMail — and how throwaways and the
/// mail views use them.</summary>
public sealed class DisposableServiceTests
{
    public DisposableServiceTests() => TestEnv.Fresh();

    private static JsonNode Body(HttpRequestMessage r) => JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!;

    // ---------- Mail.tm ----------

    private static FakeRest MailTm(Func<string?, bool> tokenOk, List<string> log)
    {
        var fake = new FakeRest();
        fake.Routes["GET /domains"] = _ => (HttpStatusCode.OK, """{"hydra:member":[{"domain":"off.test","isActive":false},{"domain":"maxx.test","isActive":true}]}""");
        fake.Routes["POST /accounts"] = r => (HttpStatusCode.Created, new JsonObject { ["id"] = "acc1", ["address"] = Body(r)["address"]!.GetValue<string>() }.ToJsonString());
        fake.Routes["POST /token"] = _ => { log.Add("token"); return (HttpStatusCode.OK, """{"token":"JWT-2","id":"acc1"}"""); };
        (HttpStatusCode, string) Authed(HttpRequestMessage r, string body) =>
            tokenOk(r.Headers.Authorization?.Parameter) ? (HttpStatusCode.OK, body) : (HttpStatusCode.Unauthorized, """{"message":"Expired JWT Token"}""");
        fake.Routes["GET /messages"] = r => Authed(r, """{"hydra:member":[{"id":"m1","from":{"address":"hello@shop.test","name":"Shop"},"to":[{"address":"x@maxx.test","name":""}],"subject":"Your code","intro":"Code 482913","seen":false,"hasAttachments":true,"createdAt":"2026-10-20T09:41:00+00:00"}],"hydra:totalItems":1}""");
        fake.Routes["GET /messages/m1"] = r => Authed(r, """{"id":"m1","from":{"address":"hello@shop.test","name":"Shop"},"to":[{"address":"x@maxx.test"}],"subject":"Your code","text":"Your code is 482913","html":["<p>Your code is <b>482913</b></p>"],"createdAt":"2026-10-20T09:41:00+00:00","attachments":[{"id":"att1","filename":"a.txt","contentType":"text/plain","size":1,"downloadUrl":"/messages/m1/attachment/att1"}]}""");
        fake.Routes["PATCH /messages/m1"] = r => { log.Add("patch " + r.Content!.Headers.ContentType!.MediaType + " " + Body(r).ToJsonString()); return Authed(r, "{}"); };
        fake.Routes["DELETE /messages/m1"] = r => { log.Add("delete m1"); return Authed(r, ""); };
        fake.Routes["GET /messages/m1/attachment/att1"] = r => Authed(r, "x");
        fake.Routes["DELETE /accounts/acc1"] = r => { log.Add("delete account"); return Authed(r, ""); };
        return fake;
    }

    [Fact]
    public async Task MailTm_creates_an_account_with_a_random_password_kept_encrypted()
    {
        var log = new List<string>();
        var fake = MailTm(_ => true, log);
        using var p = new MailTmProvider("https://api.mail.test", fake);
        var a = await p.CreateAsync(new CreateRequest { Prefix = "Shop Sign-up" });
        Assert.Equal("shopsign-up@maxx.test", a.Email);                       // name cleaned, inactive domain skipped
        var account = Body(new HttpRequestMessage { Content = new StringContent(fake.Requests.Single(r => r.Url.EndsWith("/accounts", StringComparison.Ordinal)).Body) });
        Assert.Equal(32, account["password"]!.GetValue<string>().Length);
        var raw = File.ReadAllText(Path.Combine(SecureStore.DataDir, AppSettings.FileName));
        Assert.DoesNotContain(account["password"]!.GetValue<string>(), raw);
        Assert.DoesNotContain("JWT-2", raw);
        Assert.Equal("shopsign-up@maxx.test", Assert.Single(await p.ListAsync()).Email);
    }

    [Fact]
    public async Task MailTm_reads_marks_deletes_and_saves_attachments_and_signs_in_again_when_the_token_expires()
    {
        var log = new List<string>();
        var fake = MailTm(token => token == "JWT-2", log);
        using var p = new MailTmProvider("https://api.mail.test", fake);
        var a = await p.CreateAsync(new CreateRequest());
        LocalAliases.Find(MailTmProvider.ProviderKey, a.Email)!.Secrets["token"] = "JWT-OLD";   // as if it expired
        log.Clear();

        var (items, total) = await p.MessagesAsync(a, 0);
        Assert.Equal(1, total);
        Assert.Equal("Your code", items[0].Subject);
        Assert.True(items[0].Unread);
        Assert.Equal(["token"], log);                                          // one sign-in, then the retry worked
        Assert.Equal("JWT-2", LocalAliases.Find(MailTmProvider.ProviderKey, a.Email)!.Secrets["token"]);

        var m = await p.MessageAsync(a, "m1");
        Assert.Contains("482913", m.Html, StringComparison.Ordinal);
        Assert.Equal("a.txt", Assert.Single(m.Attachments).Name);
        Assert.Equal("x"u8.ToArray(), await p.DownloadAsync(a, m.Attachments[0]));
        await p.SetSeenAsync(a, "m1", true);
        Assert.Contains("patch application/merge-patch+json {\"seen\":true}", log);
        await p.DeleteMessageAsync(a, "m1");
        await p.DeleteAsync(a);
        Assert.Contains("delete account", log);
        Assert.Empty(TestEnv.S.LocalAliases);
    }

    [Fact]
    public async Task MailTm_refuses_an_inbox_made_on_the_other_server()
    {
        var log = new List<string>();
        using var tm = new MailTmProvider("https://api.mail.test", MailTm(_ => true, log));
        var a = await tm.CreateAsync(new CreateRequest());
        using var gw = new MailTmProvider("https://api.mail.other", MailTm(_ => true, log));
        var e = await Assert.ThrowsAsync<ProviderException>(() => gw.MessagesAsync(a, 0));
        Assert.Contains("switch", e.Message, StringComparison.Ordinal);
    }

    // ---------- TempMail.lol ----------

    [Fact]
    public async Task TempMailLol_creates_reads_by_token_and_keeps_read_and_deleted_marks_itself()
    {
        var fake = new FakeRest();
        JsonNode? create = null;
        bool expired = false;
        fake.Routes["POST /v2/inbox/create"] = r => { create = Body(r); return (HttpStatusCode.OK, """{"address":"shop1@tml.test","token":"TOK"}"""); };
        fake.Routes["GET /v2/inbox"] = r => (HttpStatusCode.OK, r.RequestUri!.Query == "?token=TOK"
            ? "{\"expired\":" + (expired ? "true" : "false") + ",\"emails\":[{\"from\":\"Shop <hello@shop.test>\",\"to\":\"shop1@tml.test\",\"subject\":\"Confirm\",\"body\":\"Confirm: https://shop.test/verify?t=1\",\"html\":\"<a href='https://shop.test/verify?t=1'>Confirm</a>\",\"date\":1792500000000}]}"
            : """{"expired":true,"emails":[]}""");
        using var p = new TempMailLolProvider("", fake, "https://tml.test");
        var a = await p.CreateAsync(new CreateRequest { Prefix = "Shop1" });
        Assert.Equal("shop1", create?["prefix"]?.GetValue<string>());
        Assert.NotNull(a.Expires);
        Assert.True(a.Expires <= DateTimeOffset.Now.AddHours(1).AddSeconds(5));

        var (items, _) = await p.MessagesAsync(a, 0);
        var item = Assert.Single(items);
        Assert.Equal("hello@shop.test", item.From[0].Email);
        Assert.Equal("Shop", item.From[0].Name);
        Assert.True(item.Unread);
        await p.SetSeenAsync(a, item.Id, true);
        Assert.False((await p.MessagesAsync(a, 0)).Items[0].Unread);
        Assert.Contains("verify", (await p.MessageAsync(a, item.Id)).Html, StringComparison.Ordinal);
        await p.DeleteMessageAsync(a, item.Id);
        Assert.Empty((await p.MessagesAsync(a, 0)).Items);

        expired = true;
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.MessagesAsync(a, 0));
        Assert.Contains("ended", e.Message, StringComparison.Ordinal);
    }

    // ---------- DropMail ----------

    [Fact]
    public async Task DropMail_opens_a_session_reads_it_and_says_when_it_has_ended()
    {
        var fake = new FakeRest();
        var queries = new List<string>();
        bool ended = false;
        fake.Routes["POST /api/graphql/af_TEST"] = r =>
        {
            var q = Body(r)["query"]!.GetValue<string>();
            queries.Add(q);
            if (q.Contains("domains", StringComparison.Ordinal) && !q.Contains("introduceSession", StringComparison.Ordinal))
                return (HttpStatusCode.OK, """{"data":{"domains":[{"id":"RG9tYWluOjE=","name":"drop.test"},{"id":"RG9tYWluOjI=","name":"other.test"}]}}""");
            if (q.Contains("introduceSession", StringComparison.Ordinal))
                return (HttpStatusCode.OK, """{"data":{"introduceSession":{"id":"U2Vzc2lvbjox","expiresAt":"2026-10-20T10:10:00+00:00","addresses":[{"address":"abc@other.test","restoreKey":"rk"}]}}}""");
            return ended
                ? (HttpStatusCode.OK, """{"data":{"session":null},"errors":[{"message":"SESSION_NOT_FOUND"}]}""")
                : (HttpStatusCode.OK, """{"data":{"session":{"expiresAt":"2026-10-20T10:20:00+00:00","mails":[{"id":"mail1","fromAddr":"hello@shop.test","toAddr":"abc@other.test","headerSubject":"Code","text":"482913","html":"<b>482913</b>","receivedAt":"2026-10-20T10:01:00+00:00"}]}}}""");
        };
        using var p = new DropMailProvider("af_TEST", fake, "https://dropmail.test");
        Assert.Equal(["drop.test", "other.test"], await p.DomainsAsync());
        var a = await p.CreateAsync(new CreateRequest { Domain = "other.test" });
        Assert.Equal("abc@other.test", a.Email);
        Assert.Contains("domainId: \"RG9tYWluOjI=\"", queries[^1], StringComparison.Ordinal);    // the value is JSON-quoted
        var (items, _) = await p.MessagesAsync(a, 0);
        Assert.Equal("Code", Assert.Single(items).Subject);
        Assert.Contains("session(id: \"U2Vzc2lvbjox\")", queries[^1], StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T10:20:00+00:00", System.Globalization.CultureInfo.InvariantCulture), LocalAliases.Find(DropMailProvider.ProviderKey, a.Email)!.Expires);

        ended = true;
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.MessagesAsync(a, 0));
        Assert.Equal(410, e.Status);
        Assert.Contains("ended", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropMail_cannot_be_tricked_by_a_session_id_with_quotes()
    {
        var fake = new FakeRest();
        string? query = null;
        fake.Routes["POST /api/graphql/af_T"] = r => { query = Body(r)["query"]!.GetValue<string>(); return (HttpStatusCode.OK, """{"data":{"session":{"mails":[]}}}"""); };
        using var p = new DropMailProvider("af_T", fake, "https://dropmail.test");
        LocalAliases.Add(new LocalAlias { ProviderKey = DropMailProvider.ProviderKey, Id = "s", Email = "x@drop.test", Secrets = new() { ["session"] = "x\") { evil } #" } });
        await p.MessagesAsync(LocalAliases.ToAlias(LocalAliases.Find(DropMailProvider.ProviderKey, "x@drop.test")!), 0);
        // The quote is escaped inside the GraphQL string, so the id can't close it and add its own query.
        Assert.DoesNotContain("x\") { evil", query, StringComparison.Ordinal);
        Assert.Contains("session(id: \"x\\u0022) { evil } #\")", query, StringComparison.Ordinal);
    }

    // ---------- with the rest of the app ----------

    [Fact]
    public async Task A_disposable_inbox_is_read_at_its_service_not_from_a_Fastmail_folder()
    {
        var fake = new FakeRest();
        fake.Routes["POST /v2/inbox/create"] = _ => (HttpStatusCode.OK, """{"address":"t@tml.test","token":"TOK"}""");
        using var reg = new ProviderRegistry(TestEnv.With(("tempmaillol", ServiceInfo.EnabledField, "true")), fake);
        var p = Assert.Single(reg.Providers);
        var (alias, info) = await Throwaways.CreateAsync(reg, TimeSpan.FromDays(3), p);
        Assert.True(info.Expires <= DateTimeOffset.Now.AddHours(1).AddSeconds(5));   // capped to what the service allows
        Assert.False(Throwaways.CanExtend(alias.Email));
        Assert.Throws<ProviderException>(() => Throwaways.Extend(alias.Email, TimeSpan.FromHours(1)));
        Assert.IsType<ProviderInbox>(reg.MailFor(alias));
        Assert.Contains("TempMail.lol", reg.MailFor(alias)!.Attribution, StringComparison.Ordinal);
        Assert.False(TestEnv.S.Folders.ContainsKey(alias.Email));

        info.Expires = DateTimeOffset.Now.AddSeconds(-1);
        Assert.Equal([alias.Email], await Throwaways.EnforceAsync(reg));
        Assert.Empty(TestEnv.S.LocalAliases);
    }

    [Fact]
    public async Task A_DuckDuckGo_throwaway_is_honestly_only_forgotten()
    {
        var fake = new FakeRest();
        fake.Routes["POST /api/email/addresses"] = _ => (HttpStatusCode.OK, """{"address":"one-two"}""");
        using var reg = new ProviderRegistry(TestEnv.With(("duckduckgo", "token", "t")), fake);
        var (alias, info) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(5), reg.Providers[0]);
        info.Expires = DateTimeOffset.Now.AddSeconds(-1);
        await Throwaways.EnforceAsync(reg);
        Assert.Contains(AuditLog.Read(), e => e.Action == "throwaway ended" && e.Detail.Contains("keeps forwarding", StringComparison.Ordinal));
        Assert.Empty(TestEnv.S.LocalAliases);
        Assert.Single(fake.Requests);                                     // nothing pretended to delete it at DuckDuckGo
    }
}
