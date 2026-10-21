using System.Net;
using System.Text.Json.Nodes;
using DcxMail.Fakes;
using DcxMail.Mail;
using DcxMail.Providers;
using Xunit;

namespace DcxMail.Tests;

/// <summary>Your own domains: Forward Email, Cloudflare Email Routing, ImprovMX, Migadu and mailcow, each against an
/// in-process fake that answers like the documented API.</summary>
public sealed class DomainServiceTests
{
    public DomainServiceTests() => TestEnv.Fresh();

    private static JsonNode Body(HttpRequestMessage r) => JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!;

    // ---------- Forward Email ----------

    [Fact]
    public async Task ForwardEmail_lists_creates_turns_off_with_the_chosen_code_notes_deletes_and_sends()
    {
        var fake = new FakeRest();
        JsonNode? created = null, put = null, sent = null;
        bool deleted = false;
        fake.Routes["GET /v1/domains"] = _ => (HttpStatusCode.OK, """[{"name":"mine.org"}]""");
        fake.Routes["GET /v1/domains/mine.org/aliases"] = _ => (HttpStatusCode.OK, """[{"id":"a1","name":"shop","recipients":["me@fm.com"],"description":"Shop","is_enabled":true},{"id":"a2","name":"*","recipients":["me@fm.com"]}]""");
        fake.Routes["POST /v1/domains/mine.org/aliases"] = r => { created = Body(r); return (HttpStatusCode.OK, """{"id":"a3","name":"new","recipients":["me@fm.com"],"is_enabled":true}"""); };
        fake.Routes["PUT /v1/domains/mine.org/aliases/a1"] = r => { put = Body(r); return (HttpStatusCode.OK, "{}"); };
        fake.Routes["DELETE /v1/domains/mine.org/aliases/a1"] = _ => { deleted = true; return (HttpStatusCode.OK, "{}"); };
        fake.Routes["POST /v1/emails"] = r => { sent = Body(r); return (HttpStatusCode.OK, """{"id":"e1"}"""); };
        using var p = new ForwardEmailProvider("fe-token", ["me@fm.com"], [], "bounce", fake, "https://fe.test");

        var list = await p.ListAsync();
        var shop = Assert.Single(list);                                  // the catch-all "*" is not an address
        Assert.Equal("shop@mine.org", shop.Email);
        Assert.StartsWith("Basic ", fake.Requests[0].Auth, StringComparison.Ordinal);

        await p.CreateAsync(new CreateRequest { Prefix = "New", Description = "note", Site = "site.com" });
        Assert.Equal("new", created?["name"]?.GetValue<string>());
        Assert.Equal("me@fm.com", created?["recipients"]?[0]?.GetValue<string>());
        Assert.Equal(550, created?["error_code_if_disabled"]?.GetValue<int>());
        Assert.Equal("note (site.com)", created?["description"]?.GetValue<string>());

        await p.SetEnabledAsync(shop, false);
        Assert.False(put?["is_enabled"]?.GetValue<bool>());
        Assert.Equal(550, put?["error_code_if_disabled"]?.GetValue<int>());

        await p.SendAsync(shop, new OutgoingMessage { FromName = "Sam \"X\"\r\nBcc: evil@x.com", To = ["a@b.org"], Subject = "Hi", Html = "<p>Hello</p>", Attachments = [new OutgoingFile { Name = "f.txt", Type = "text/plain", Data = "x"u8.ToArray() }] });
        var from = sent?["from"]?.GetValue<string>();
        Assert.Equal("\"Sam XBcc: evil@x.com\" <shop@mine.org>", from);     // quotes and line breaks removed: no header injection
        Assert.Equal("eA==", sent?["attachments"]?[0]?["content"]?.GetValue<string>());
        Assert.Contains("Hello", sent?["text"]?.GetValue<string>(), StringComparison.Ordinal);

        await p.DeleteAsync(shop);
        Assert.True(deleted);
    }

    [Fact]
    public async Task ForwardEmail_needs_somewhere_to_forward()
    {
        var fake = new FakeRest();
        using var p = new ForwardEmailProvider("t", [], ["mine.org"], "drop", fake, "https://fe.test");
        await Assert.ThrowsAsync<ProviderException>(() => p.CreateAsync(new CreateRequest()));
        Assert.Empty(fake.Requests);
    }

    // ---------- Cloudflare ----------

    private static FakeRest Cloudflare(List<JsonNode> writes)
    {
        var fake = new FakeRest();
        fake.Routes["GET /zones"] = _ => (HttpStatusCode.OK, """{"success":true,"result":[{"id":"z1","name":"mine.org","account":{"id":"acc"}}],"result_info":{"page":1,"total_pages":1}}""");
        fake.Routes["GET /zones/z1/email/routing/rules"] = _ => (HttpStatusCode.OK, """
            {"success":true,"result":[
              {"id":"r1","name":"Shop","enabled":true,"matchers":[{"type":"literal","field":"to","value":"shop@mine.org"}],"actions":[{"type":"forward","value":["me@fm.com"]}],"priority":5},
              {"id":"r2","name":"catch","enabled":true,"matchers":[{"type":"all"}],"actions":[{"type":"drop"}]}],"result_info":{"page":1,"total_pages":1}}
            """);
        fake.Routes["GET /zones/z1/email/routing/rules/r1"] = _ => (HttpStatusCode.OK, """{"success":true,"result":{"id":"r1","name":"Shop","enabled":true,"matchers":[{"type":"literal","field":"to","value":"shop@mine.org"}],"actions":[{"type":"forward","value":["me@fm.com"]}],"priority":5}}""");
        fake.Routes["PUT /zones/z1/email/routing/rules/r1"] = r => { writes.Add(Body(r)); return (HttpStatusCode.OK, """{"success":true,"result":{}}"""); };
        fake.Routes["POST /zones/z1/email/routing/rules"] = r =>
        {
            var b = Body(r); writes.Add(b);
            b["id"] = "r9";
            return (HttpStatusCode.OK, "{\"success\":true,\"result\":" + b.ToJsonString() + "}");
        };
        fake.Routes["GET /accounts/acc/email/routing/addresses"] = _ => (HttpStatusCode.OK, """{"success":true,"result":[{"email":"me@fm.com","verified":"2026-01-01T00:00:00Z"},{"email":"pending@x.org","verified":null}],"result_info":{"total_pages":1}}""");
        return fake;
    }

    [Fact]
    public async Task Cloudflare_lists_exact_address_rules_and_creates_forwarding_rules()
    {
        var writes = new List<JsonNode>();
        using var p = new CloudflareRoutingProvider("cf-token", "me@fm.com", [], Cloudflare(writes), "https://cf.test");
        var list = await p.ListAsync();
        var shop = Assert.Single(list);                                  // the catch-all rule is not an address
        Assert.Equal("shop@mine.org", shop.Email);
        Assert.Equal("me@fm.com", shop.ForwardsTo);
        Assert.Equal(["me@fm.com"], await p.ForwardTargetsAsync());     // the unverified destination is not offered

        var a = await p.CreateAsync(new CreateRequest { Prefix = "news", Description = "Newsletters" });
        Assert.Equal("news@mine.org", a.Email);
        var rule = writes[^1];
        Assert.Equal("literal", rule["matchers"]?[0]?["type"]?.GetValue<string>());
        Assert.Equal("news@mine.org", rule["matchers"]?[0]?["value"]?.GetValue<string>());
        Assert.Equal("me@fm.com", rule["actions"]?[0]?["value"]?[0]?.GetValue<string>());
    }

    [Fact]
    public async Task Cloudflare_turns_off_and_renames_by_rewriting_the_whole_rule()
    {
        var writes = new List<JsonNode>();
        using var p = new CloudflareRoutingProvider("cf-token", "me@fm.com", [], Cloudflare(writes), "https://cf.test");
        var shop = (await p.ListAsync())[0];
        await p.SetEnabledAsync(shop, false);
        Assert.False(writes[^1]["enabled"]?.GetValue<bool>());
        Assert.Equal("shop@mine.org", writes[^1]["matchers"]?[0]?["value"]?.GetValue<string>());   // matchers and actions kept
        Assert.Equal(5, writes[^1]["priority"]?.GetValue<int>());
        await p.UpdateAsync(shop, "Shopping", "");
        Assert.Equal("Shopping", writes[^1]["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Cloudflare_reports_a_refusal_inside_a_200_answer()
    {
        var fake = new FakeRest();
        fake.Routes["GET /zones"] = _ => (HttpStatusCode.OK, """{"success":false,"errors":[{"message":"Authentication error"}]}""");
        using var p = new CloudflareRoutingProvider("bad", "me@fm.com", [], fake, "https://cf.test");
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.ListAsync());
        Assert.Contains("Authentication error", e.Message, StringComparison.Ordinal);
    }

    // ---------- ImprovMX ----------

    [Fact]
    public async Task ImprovMx_lists_creates_deletes_and_shows_the_delivery_log()
    {
        var fake = new FakeRest();
        JsonNode? created = null;
        bool deleted = false;
        fake.Routes["GET /domains"] = _ => (HttpStatusCode.OK, """{"domains":[{"domain":"mine.org"}],"success":true}""");
        fake.Routes["GET /domains/mine.org/aliases"] = _ => (HttpStatusCode.OK, """{"aliases":[{"id":11,"alias":"shop","forward":"me@fm.com","created":1700000000},{"id":12,"alias":"*","forward":"me@fm.com"}],"success":true}""");
        fake.Routes["POST /domains/mine.org/aliases"] = r => { created = Body(r); return (HttpStatusCode.OK, """{"alias":{"id":13,"alias":"x7","forward":"me@fm.com"},"success":true}"""); };
        fake.Routes["DELETE /domains/mine.org/aliases/11"] = _ => { deleted = true; return (HttpStatusCode.NoContent, ""); };
        fake.Routes["GET /domains/mine.org/logs/shop"] = _ => (HttpStatusCode.OK, """{"logs":[{"created":"2026-10-20T09:00:00Z","sender":{"email":"news@site.com"},"forward":{"email":"me@fm.com"},"subject":"Hello","events":[{"status":"QUEUED"},{"status":"DELIVERED"}]}],"success":true}""");
        using var p = new ImprovMxProvider("imx", "me@fm.com", [], fake, "https://imx.test");

        var shop = Assert.Single(await p.ListAsync());
        Assert.Equal("shop@mine.org", shop.Email);
        Assert.Equal("Basic " + Convert.ToBase64String("api:imx"u8.ToArray()), fake.Requests[0].Auth);
        await p.CreateAsync(new CreateRequest());
        Assert.Matches("^[a-z][a-z0-9]{11}$", created?["alias"]?.GetValue<string>());
        var log = Assert.Single(await p.ActivityAsync(shop));
        Assert.Equal("delivered", log.What);
        Assert.Equal("news@site.com", log.From);
        Assert.Equal("Hello", log.Subject);
        await p.DeleteAsync(shop);
        Assert.True(deleted);
        await Assert.ThrowsAsync<ProviderException>(() => p.SetEnabledAsync(shop, false));
    }

    // ---------- Migadu ----------

    [Fact]
    public async Task Migadu_creates_aliases_for_a_mailbox_on_the_same_domain_only()
    {
        var fake = new FakeRest();
        JsonNode? created = null;
        fake.Routes["GET /domains/mine.org/aliases"] = _ => (HttpStatusCode.OK, """{"address_aliases":[{"local_part":"shop","address":"shop@mine.org","destinations":["me@mine.org"],"is_internal":false}]}""");
        fake.Routes["POST /domains/mine.org/aliases"] = r => { created = Body(r); return (HttpStatusCode.OK, """{"local_part":"x","address":"x@mine.org","destinations":["me@mine.org"]}"""); };
        using var p = new MigaduProvider("admin@mine.org", "key", ["mine.org"], "me@mine.org", fake, "https://migadu.test");
        Assert.Equal("shop@mine.org", Assert.Single(await p.ListAsync()).Email);
        await p.CreateAsync(new CreateRequest { Prefix = "x" });
        Assert.Equal("me@mine.org", created?["destinations"]?.GetValue<string>());
        await Assert.ThrowsAsync<ProviderException>(() => p.CreateAsync(new CreateRequest { ForwardTo = "me@fm.com" }));
    }

    // ---------- mailcow ----------

    private static FakeRest Mailcow(List<(string Path, JsonNode Body)> posts, List<JsonNode> tla)
    {
        var fake = new FakeRest();
        fake.Routes["GET /api/v1/get/domain/all"] = _ => (HttpStatusCode.OK, """[{"domain_name":"other.org"},{"domain_name":"mine.org"}]""");
        fake.Routes["GET /api/v1/get/alias/all"] = _ => (HttpStatusCode.OK, """[{"id":6,"address":"shop@mine.org","goto":"me@mine.org","active":1,"domain":"mine.org","private_comment":"Shop"},{"id":7,"address":"@mine.org","goto":"me@mine.org","active":1,"is_catch_all":1}]""");
        fake.Routes["GET /api/v1/get/time_limited_aliases/me%40mine.org"] = _ => (HttpStatusCode.OK, new JsonArray(tla.Select(t => t.DeepClone()).ToArray()).ToJsonString());
        foreach (var path in new[] { "/api/v1/add/time_limited_alias", "/api/v1/add/alias", "/api/v1/edit/alias", "/api/v1/delete/alias", "/api/v1/delete/time_limited_alias" })
            fake.Routes["POST " + path] = r =>
            {
                var b = Body(r);
                posts.Add((path, b));
                if (path.EndsWith("add/time_limited_alias", StringComparison.Ordinal))
                    tla.Add(JsonNode.Parse("""{"address":"spam42@mine.org","goto":"me@mine.org","validity":1893456000,"created":"2026-10-20 10:00:00"}""")!);
                return (HttpStatusCode.OK, """[{"type":"success","msg":["ok"]}]""");
            };
        return fake;
    }

    [Fact]
    public async Task Mailcow_lists_aliases_and_time_limited_ones_but_not_catch_alls()
    {
        var posts = new List<(string, JsonNode)>();
        var tla = new List<JsonNode> { JsonNode.Parse("""{"address":"old@mine.org","goto":"me@mine.org","validity":1893456000,"created":"2026-10-01 10:00:00"}""")! };
        using var p = new MailcowProvider("https://mail.mine.test", "mc-key", "me@mine.org", [], Mailcow(posts, tla));
        var list = await p.ListAsync();
        Assert.Equal(["shop@mine.org", "old@mine.org"], list.Select(a => a.Email));
        Assert.Equal("Shop", list[0].Description);
        Assert.NotNull(list[1].Expires);
        Assert.Equal(["mine.org", "other.org"], await p.DomainsAsync());   // the mailbox's own domain first
    }

    [Fact]
    public async Task Mailcow_makes_the_server_end_a_throwaway_itself()
    {
        var posts = new List<(string Path, JsonNode Body)>();
        using var p = new MailcowProvider("https://mail.mine.test", "mc-key", "me@mine.org", [], Mailcow(posts, []));
        var a = await p.CreateAsync(new CreateRequest { Lifetime = TimeSpan.FromMinutes(90), Description = "Throwaway" });
        Assert.Equal("spam42@mine.org", a.Email);
        var add = posts.Single(x => x.Path.EndsWith("add/time_limited_alias", StringComparison.Ordinal)).Body;
        Assert.Equal(2, add["validity"]?.GetValue<int>());                // hours, rounded up
        Assert.Equal("me@mine.org", add["username"]?.GetValue<string>());
        await Assert.ThrowsAsync<ProviderException>(() => p.SetEnabledAsync(a, false));
        await p.DeleteAsync(a);
        Assert.Equal("spam42@mine.org", posts[^1].Body[0]?.GetValue<string>());
        Assert.EndsWith("delete/time_limited_alias", posts[^1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mailcow_ordinary_aliases_turn_off_and_keep_notes()
    {
        var posts = new List<(string Path, JsonNode Body)>();
        using var p = new MailcowProvider("https://mail.mine.test", "mc-key", "me@mine.org", [], Mailcow(posts, []));
        var shop = (await p.ListAsync())[0];
        await p.SetEnabledAsync(shop, false);
        Assert.Equal("0", posts[^1].Body["attr"]?["active"]?.GetValue<string>());
        Assert.Equal("6", posts[^1].Body["items"]?[0]?.GetValue<string>());
        await p.UpdateAsync(shop, "Shopping", "");
        Assert.Equal("Shopping", posts[^1].Body["attr"]?["private_comment"]?.GetValue<string>());
    }

    [Fact]
    public async Task Mailcow_reports_a_refusal_inside_a_200_answer()
    {
        var fake = new FakeRest();
        fake.Routes["GET /api/v1/get/domain/all"] = _ => (HttpStatusCode.OK, """[{"domain_name":"mine.org"}]""");
        fake.Routes["POST /api/v1/add/alias"] = _ => (HttpStatusCode.OK, """[{"type":"danger","msg":["alias_invalid","x"]}]""");
        using var p = new MailcowProvider("https://mail.mine.test", "k", "me@mine.org", [], fake);
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.CreateAsync(new CreateRequest { Prefix = "x" }));
        Assert.Contains("alias_invalid", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_self_hosted_server_must_be_https()
    {
        using var p = new MailcowProvider("http://mail.mine.test", "k", "me@mine.org", [], new FakeRest());
        var e = await Assert.ThrowsAnyAsync<Exception>(() => p.ListAsync());
        Assert.Contains("HTTPS", e.Message, StringComparison.OrdinalIgnoreCase);
    }
}
