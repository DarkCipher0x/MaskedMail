using System.Net;
using System.Text.Json.Nodes;
using DcxMail.Fakes;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;
using Xunit;

namespace DcxMail.Tests;

/// <summary>Every service against an in-process fake: Fastmail over JMAP, SimpleLogin, addy.io and Firefox Relay over
/// REST, the registry that combines them, the outgoing queue, and the network rules (HTTPS only, no redirects).</summary>
public sealed class ProviderTests
{
    public ProviderTests() => TestEnv.Fresh();

    private static async Task<(FakeJmap Fake, ProviderRegistry Reg, IAliasProvider Fm)> FastmailAsync()
    {
        var fake = new FakeJmap();
        TestEnv.S.RouteParent = "Inbox/automation";
        TestEnv.S.RoutingEnabled = true;
        var reg = new ProviderRegistry(new AppSettings { FastmailToken = "fmu1-test" }, fake);
        await reg.RefreshAsync();
        Assert.Single(reg.Aliases);
        Assert.Empty(reg.Errors);
        return (fake, reg, reg.Get(FastmailProvider.ProviderKey)!);
    }

    [Fact]
    public async Task Fastmail_create_makes_the_folder_tree_sweeps_mail_into_it_and_forgets_deleted_folders()
    {
        var (fake, reg, fm) = await FastmailAsync();
        using var _ = reg;
        var (alias, folderErr) = await reg.CreateAsync(fm, new CreateRequest { Description = "Shop", Site = "shop.example.com/x", Prefix = "Shop" });
        Assert.Null(folderErr);
        Assert.StartsWith("shop", alias.Email, StringComparison.Ordinal);
        Assert.Equal("enabled", alias.State);
        Assert.Equal("https://shop.example.com", fake.LastMaskedCreate?["forDomain"]?.GetValue<string>());
        Assert.Equal("shop", fake.LastMaskedCreate?["emailPrefix"]?.GetValue<string>());

        // Inbox (existing, by role) → automation (new) → address (new), remembered in the settings.
        var auto = Assert.Single(fake.Boxes, b => b.Name == "automation" && b.Parent == "inbox");
        var leaf = Assert.Single(fake.Boxes, b => b.Name == alias.Email && b.Parent == auto.Id);
        Assert.Equal(leaf.Id, TestEnv.S.Folders[alias.Email]);
        await reg.CreateAsync(fm, new CreateRequest { Description = "Two" });
        Assert.Single(fake.Boxes, b => b.Name == "automation");          // reused, not duplicated

        // One for the alias (cc), one for someone else, one via Delivered-To.
        fake.Emails.Add(new FakeEmail("e1", "inbox", $$"""{"to":[{"email":"me@fm.com"}],"cc":[{"email":"{{alias.Email.ToUpperInvariant()}}"}]}"""));
        fake.Emails.Add(new FakeEmail("e2", "inbox", """{"to":[{"email":"other@x.com"}]}"""));
        fake.Emails.Add(new FakeEmail("e3", "inbox", $$"""{"to":[{"email":"list@x.com"}],"header:Delivered-To:asAddresses:all":[[{"email":"{{alias.Email}}"}]]}"""));
        Assert.Equal(2, await reg.SweepAsync());
        Assert.Equal(leaf.Id, fake.Emails.Single(e => e.Id == "e1").Box);
        Assert.Equal("inbox", fake.Emails.Single(e => e.Id == "e2").Box);
        Assert.Equal(leaf.Id, fake.Emails.Single(e => e.Id == "e3").Box);
        Assert.Equal(0, await reg.SweepAsync());

        // A folder deleted in Fastmail is forgotten, never targeted.
        fake.Boxes.RemoveAll(b => b.Id == leaf.Id);
        fake.Emails.Add(new FakeEmail("e4", "inbox", $$"""{"to":[{"email":"{{alias.Email}}"}]}"""));
        Assert.Equal(0, await reg.SweepAsync());
        Assert.False(TestEnv.S.Folders.ContainsKey(alias.Email));

        // Disable and delete use the documented states.
        await fm.SetEnabledAsync(alias, false);
        Assert.Equal("disabled", fake.Masked.Single(m => m["email"]!.GetValue<string>() == alias.Email)["state"]!.GetValue<string>());
        await fm.DeleteAsync(alias);
        await reg.RefreshAsync();
        Assert.DoesNotContain(reg.Aliases, a => a.Email == alias.Email);
    }

    [Fact]
    public async Task Sweeps_reuse_the_folder_list_instead_of_fetching_it_every_time()
    {
        var (fake, reg, fm) = await FastmailAsync();
        using var _ = reg;
        var (alias, _) = await reg.CreateAsync(fm, new CreateRequest { Description = "Shop" });
        await reg.SweepAsync();
        var before = fake.MailboxGets;
        for (int i = 0; i < 5; i++) await reg.SweepAsync();
        Assert.Equal(before, fake.MailboxGets);
        Assert.True(TestEnv.S.Folders.ContainsKey(alias.Email));
    }

    [Fact]
    public async Task Fastmail_sends_plain_attachment_and_inline_image_messages_from_the_masked_address()
    {
        var (fake, reg, fm) = await FastmailAsync();
        using var _ = reg;
        var (alias, _) = await reg.CreateAsync(fm, new CreateRequest { Description = "Shop" });

        // Plain message: the 0.1.1 "node already has a parent" bug.
        await reg.Mail!.SendAsync(new OutgoingMessage { FromEmail = alias.Email, FromIsFastmailMasked = true, To = ["sam@example.org"], Subject = "Plain", Html = "<p>test</p>" });
        Assert.Equal("multipart/alternative", fake.LastEmailCreate?["bodyStructure"]?["type"]?.GetValue<string>());

        // Attachment only: mixed → alternative + file.
        await reg.Mail.SendAsync(new OutgoingMessage
        {
            FromEmail = alias.Email, FromIsFastmailMasked = true, To = ["sam@example.org"], Subject = "File", Html = "<p>see file</p>",
            Attachments = [new OutgoingFile { Name = "b.txt", Type = "text/plain", Data = "y"u8.ToArray() }],
        });
        var fileBody = fake.LastEmailCreate?["bodyStructure"];
        Assert.Equal("multipart/mixed", fileBody?["type"]?.GetValue<string>());
        Assert.Equal("multipart/alternative", fileBody!["subParts"]![0]!["type"]!.GetValue<string>());
        fake.Uploads = 0;

        // Identity created, inline image as cid, attachment, sent copy.
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
        await reg.Mail.SendAsync(new OutgoingMessage
        {
            FromEmail = alias.Email, FromIsFastmailMasked = true, To = ["sam@example.org"], Subject = "Hi",
            Html = $"<p>Hello <b>Sam</b></p><img src=\"data:image/png;base64,{png}\">",
            Attachments = [new OutgoingFile { Name = "a.txt", Type = "text/plain", Data = "x"u8.ToArray() }],
        });
        Assert.Contains(alias.Email, fake.Identities);
        var body = fake.LastEmailCreate!["bodyStructure"]!;
        Assert.Equal("multipart/mixed", body["type"]!.GetValue<string>());
        var related = body["subParts"]![0]!;
        Assert.Equal("multipart/related", related["type"]!.GetValue<string>());
        Assert.NotNull(related["subParts"]![1]!["cid"]);
        Assert.DoesNotContain("data:", fake.LastEmailCreate!["bodyValues"]!["html"]!["value"]!.GetValue<string>());
        Assert.Equal(2, fake.Uploads);
        Assert.NotNull(fake.LastSubmission?["identityId"]);
        Assert.True(fake.LastOnSuccess?["#send"]?["mailboxIds/sent"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Queue_splits_duplicates_skips_sends_keeps_failures_and_is_stored_encrypted()
    {
        var (fake, reg, fm) = await FastmailAsync();
        using var _ = reg;
        var (alias, _) = await reg.CreateAsync(fm, new CreateRequest { Description = "Shop" });

        var q = new QueuedEmail { FromEmail = alias.Email, AliasProvider = FastmailProvider.ProviderKey, To = ["a@example.org", "b@example.org"], Cc = ["c@example.org"], Subject = "QueueSubjectXYZ", Html = "<p>queued body</p>", Copies = 2 };
        Outbox.Put(q);
        Assert.Equal(3, Outbox.SplitPerRecipient(q.Id));
        Assert.Equal(3, Outbox.Items.Count);
        Assert.All(Outbox.Items, i => { Assert.Single(i.To); Assert.Empty(i.Cc); });
        var dup = Outbox.Duplicate(Outbox.Items[0].Id);
        dup.Enabled = false;                                        // skipped, stays queued
        Outbox.Put(new QueuedEmail { FromEmail = "gone@fastmail.com", AliasProvider = FastmailProvider.ProviderKey, To = ["d@example.org"], Subject = "will fail" });

        var stored = File.ReadAllText(Path.Combine(SecureStore.DataDir, Outbox.FileName));
        Assert.DoesNotContain("QueueSubjectXYZ", stored);
        Assert.DoesNotContain("queued body", stored);
        Outbox.Load();
        Assert.Equal(5, Outbox.Items.Count);
        Assert.False(Outbox.Items[1].Enabled);

        var before = fake.Submissions;
        var (sent, failed) = await Outbox.SendAllAsync(reg, Outbox.Items.Where(i => i.Enabled).ToList(), TimeSpan.Zero, stopOnError: false);
        Assert.Equal(6, sent);                                      // 3 split items x 2 copies
        Assert.Equal(1, failed);
        Assert.Equal(6, fake.Submissions - before);
        Assert.Equal(2, Outbox.Items.Count);
        Assert.Single(Outbox.Items, i => i.LastError != null);
        Assert.Contains(Outbox.Items, i => !i.Enabled);
    }

    [Fact]
    public async Task Fastmail_token_without_the_Masked_Email_scope_gives_a_clear_error()
    {
        using var reg = new ProviderRegistry(new AppSettings { FastmailToken = "t" }, new FakeJmap { MaskedEmailScope = false });
        await reg.RefreshAsync();
        Assert.True(reg.Errors.TryGetValue("Fastmail", out var e));
        Assert.Contains("Masked Email", e);
    }

    [Fact]
    public async Task SimpleLogin_lists_pages_creates_toggles_reverses_and_deletes()
    {
        var fake = new FakeRest();
        string? auth = null;
        int toggles = 0;
        JsonNode? custom = null;
        fake.Routes["GET /api/v2/aliases"] = r =>
        {
            auth = r.Headers.TryGetValues("Authentication", out var v) ? v.First() : null;
            var first = r.RequestUri!.Query.Contains("page_id=0", StringComparison.Ordinal);
            var items = first ? Enumerable.Range(1, 20).Select(i => $$"""{"id":{{i}},"email":"a{{i}}@sl.co","enabled":true,"note":"n","nb_forward":1,"nb_reply":0,"nb_block":0,"creation_timestamp":1700000000,"mailboxes":[{"id":1,"email":"me@fm.com"}]}""")
                              : ["""{"id":21,"email":"a21@sl.co","enabled":false,"note":null}"""];
            return (HttpStatusCode.OK, "{\"aliases\":[" + string.Join(",", items) + "]}");
        };
        fake.Routes["POST /api/alias/random/new"] = _ => (HttpStatusCode.Created, """{"id":99,"email":"random.word@sl.co","enabled":true,"note":"shop"}""");
        fake.Routes["GET /api/v5/alias/options"] = _ => (HttpStatusCode.OK, """{"can_create":true,"suffixes":[{"suffix":".x@sl.co","signed_suffix":".x@sl.co.SIG","is_premium":false}]}""");
        fake.Routes["GET /api/v2/mailboxes"] = _ => (HttpStatusCode.OK, """{"mailboxes":[{"id":7,"email":"me@fm.com","default":true}]}""");
        fake.Routes["POST /api/v3/alias/custom/new"] = r => { custom = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result); return (HttpStatusCode.Created, """{"id":100,"email":"shop.x@sl.co","enabled":true}"""); };
        fake.Routes["POST /api/aliases/99/toggle"] = _ => { toggles++; return (HttpStatusCode.OK, """{"enabled":false}"""); };
        fake.Routes["POST /api/aliases/99/contacts"] = _ => (HttpStatusCode.Created, """{"id":1,"contact":"sam@example.org","reverse_alias":"\"sam\" <ra+abc@sl.co>","reverse_alias_address":"ra+abc@sl.co","existed":false}""");
        fake.Routes["DELETE /api/aliases/99"] = _ => (HttpStatusCode.OK, """{"deleted":true}""");

        using var p = new SimpleLoginProvider("https://sl.test", "slkey", fake);
        var list = await p.ListAsync();
        Assert.Equal(21, list.Count);
        Assert.Equal("slkey", auth);
        Assert.Equal("me@fm.com", list[0].ForwardsTo);
        Assert.False(list[20].Enabled);

        var a = await p.CreateAsync(new CreateRequest { Description = "shop" });
        var c = await p.CreateAsync(new CreateRequest { Prefix = "Shop", Description = "x" });
        Assert.Equal("shop.x@sl.co", c.Email);
        Assert.Equal(".x@sl.co.SIG", custom?["signed_suffix"]?.GetValue<string>());
        Assert.Equal(7, custom!["mailbox_ids"]![0]!.GetValue<int>());
        Assert.Equal("shop", custom["alias_prefix"]!.GetValue<string>());

        await p.SetEnabledAsync(a, false);
        await p.SetEnabledAsync(a, false);   // already off: no second toggle
        Assert.Equal(1, toggles);
        Assert.False(a.Enabled);
        Assert.Equal("ra+abc@sl.co", await p.ReverseAddressAsync(a, "sam@example.org"));
        await p.DeleteAsync(a);
        Assert.Equal("deleted", a.State);
    }

    [Fact]
    public async Task Addy_lists_pages_creates_custom_reverses_deactivates_and_deletes()
    {
        var fake = new FakeRest();
        string? auth = null;
        JsonNode? created = null;
        var log = new List<string>();
        fake.Routes["GET /api/v1/aliases"] = r =>
        {
            auth = r.Headers.Authorization?.ToString();
            var q = r.RequestUri!.Query;
            var page2 = q.Contains("page[number]=2", StringComparison.Ordinal) || q.Contains("page%5Bnumber%5D=2", StringComparison.Ordinal);
            return (HttpStatusCode.OK, page2
                ? """{"data":[{"id":"b","email":"b@me.addy.io","active":false,"description":null}],"meta":{"current_page":2,"last_page":2}}"""
                : """{"data":[{"id":"a","email":"a@me.addy.io","active":true,"description":"x","emails_forwarded":3,"recipients":[{"email":"me@fm.com"}]}],"meta":{"current_page":1,"last_page":2}}""");
        };
        fake.Routes["GET /api/v1/account-details"] = _ => (HttpStatusCode.OK, """{"data":{"username":"me","default_alias_domain":"me.addy.io"}}""");
        fake.Routes["POST /api/v1/aliases"] = r => { created = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result); return (HttpStatusCode.Created, """{"data":{"id":"c","email":"shop@me.addy.io","active":true,"description":"Shop"}}"""); };
        fake.Routes["DELETE /api/v1/active-aliases/c"] = _ => { log.Add("off"); return (HttpStatusCode.NoContent, ""); };
        fake.Routes["DELETE /api/v1/aliases/c"] = _ => { log.Add("del"); return (HttpStatusCode.NoContent, ""); };

        using var p = new AddyProvider("https://addy.test", "addykey", fake);
        var list = await p.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("Bearer addykey", auth);
        Assert.Equal("me@fm.com", list[0].ForwardsTo);
        Assert.False(list[1].Enabled);

        var a = await p.CreateAsync(new CreateRequest { Prefix = "Shop", Description = "Shop" });
        Assert.Equal("me.addy.io", created?["domain"]?.GetValue<string>());
        Assert.Equal("custom", created?["format"]?.GetValue<string>());
        Assert.Equal("shop", created?["local_part"]?.GetValue<string>());
        Assert.Equal("shop+sam=example.org@me.addy.io", await p.ReverseAddressAsync(a, "sam@example.org"));
        await p.SetEnabledAsync(a, false);
        await p.DeleteAsync(a);
        Assert.Equal(["off", "del"], log);
        Assert.False(a.Enabled);
    }

    [Fact]
    public async Task Firefox_Relay_lists_creates_disables_and_deletes_and_cannot_send()
    {
        var fake = new FakeRest();
        string? auth = null;
        JsonNode? patch = null;
        bool deleted = false;
        fake.Routes["GET /api/v1/relayaddresses/"] = r =>
        {
            auth = r.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, """[{"id":5,"address":"abc123","full_address":"abc123@mozmail.com","enabled":true,"description":"forum","generated_for":"forum.example","num_forwarded":2,"num_blocked":0,"created_at":"2026-01-01T00:00:00Z"}]""");
        };
        fake.Routes["POST /api/v1/relayaddresses/"] = _ => (HttpStatusCode.Created, """{"id":6,"address":"zzz","full_address":"zzz@mozmail.com","enabled":true,"description":"new"}""");
        fake.Routes["PATCH /api/v1/relayaddresses/6/"] = r => { patch = JsonNode.Parse(r.Content!.ReadAsStringAsync().Result); return (HttpStatusCode.OK, "{}"); };
        fake.Routes["DELETE /api/v1/relayaddresses/6/"] = _ => { deleted = true; return (HttpStatusCode.NoContent, ""); };

        using var p = new FirefoxRelayProvider("https://relay.test", "relaykey", fake);
        var list = await p.ListAsync();
        var only = Assert.Single(list);
        Assert.Equal("Token relaykey", auth);
        Assert.Equal("abc123@mozmail.com", only.Email);
        Assert.Equal("forum.example", only.Site);

        var a = await p.CreateAsync(new CreateRequest { Description = "new", Site = "x.com" });
        await p.SetEnabledAsync(a, false);
        Assert.False(patch?["enabled"]?.GetValue<bool>());
        await p.DeleteAsync(a);
        Assert.True(deleted);
        Assert.Equal(SendMode.None, p.SendMode);
        await Assert.ThrowsAnyAsync<Exception>(() => p.ReverseAddressAsync(a, "x@y.z"));
    }

    [Fact]
    public async Task One_failing_service_does_not_hide_the_others()
    {
        var fake = new FakeRest();
        fake.Routes["GET /api/v1/relayaddresses/"] = _ => (HttpStatusCode.Unauthorized, "{\"detail\":\"Invalid token.\"}");
        fake.Routes["GET /api/v1/aliases"] = _ => (HttpStatusCode.OK, """{"data":[{"id":"1","email":"a@addy.io","active":true}],"meta":{"last_page":1}}""");
        using var reg = new ProviderRegistry(TestEnv.With(("addy", "key", "k"), ("relay", "key", "r")), fake);
        await reg.RefreshAsync();
        Assert.Single(reg.Aliases);
        Assert.True(reg.Errors.ContainsKey("Firefox Relay"));
    }

    [Fact]
    public async Task Redirects_are_refused_without_leaking_the_key_and_plain_http_is_refused()
    {
        var fake = new FakeRest();
        var hosts = new List<string>();
        fake.Routes["GET /api/v2/aliases"] = r => { hosts.Add(r.RequestUri!.Host); return (HttpStatusCode.Found, ""); };
        fake.OnSend = r => hosts.Add(r.RequestUri!.Host);
        fake.RedirectTo = "https://evil.test/steal";
        using var p = new SimpleLoginProvider("https://sl.test", "slkey", fake);
        var redirect = await Assert.ThrowsAnyAsync<Exception>(() => p.ListAsync());
        Assert.Contains("redirect", redirect.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.test", hosts);

        using var plain = new SimpleLoginProvider("http://sl.test", "slkey", fake);
        var http = await Assert.ThrowsAnyAsync<Exception>(() => plain.ListAsync());
        Assert.Contains("non-HTTPS", http.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jmap_refuses_a_plain_http_session_or_API_URL_before_sending_the_token()
    {
        using var j = new JmapClient("https://fake.test/jmap/session", "t", new FakeJmap { ApiUrl = "http://fake.test/jmap/api/" });
        Assert.Contains("apiUrl", (await Assert.ThrowsAnyAsync<Exception>(() => j.ConnectAsync())).Message, StringComparison.Ordinal);
        using var j2 = new JmapClient("http://fake.test/jmap/session", "t", new FakeJmap());
        Assert.Contains("https", (await Assert.ThrowsAnyAsync<Exception>(() => j2.ConnectAsync())).Message, StringComparison.Ordinal);
    }

    /// <summary>A slow answer showed "The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing" in the
    /// "Something went wrong" box: HttpClient reports its time limit as a cancellation, which the screens let through.</summary>
    [Fact]
    public async Task A_slow_server_is_a_readable_timeout_and_a_real_cancel_is_still_a_cancel()
    {
        using var slow = new JmapClient("https://fake.test/jmap/session", "t", new SlowServer(), TimeSpan.FromMilliseconds(200));
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => slow.ConnectAsync());
        Assert.StartsWith("Fastmail didn't answer within", timeout.Message, StringComparison.Ordinal);

        using var patient = new JmapClient("https://fake.test/jmap/session", "t", new SlowServer(), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patient.ConnectAsync(cts.Token));
    }

    private sealed class SlowServer : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
