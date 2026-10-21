using DcxMail.Fakes;
using DcxMail.Providers;
using DcxMail.Services;
using Xunit;

namespace DcxMail.Tests;

/// <summary>Throwaway addresses: create, folder, expiry (delete or disable), mail clean-up, retries, codes and links.</summary>
public sealed class ThrowawayTests
{
    public ThrowawayTests() => TestEnv.Fresh();

    private static AppSettings S => TestEnv.S;

    private static async Task<(FakeJmap, ProviderRegistry)> RegAsync()
    {
        var fake = new FakeJmap();
        var reg = new ProviderRegistry(new AppSettings { FastmailToken = "t" }, fake);
        await reg.RefreshAsync();
        return (fake, reg);
    }

    private static string StateOf(FakeJmap fake, string email) =>
        fake.Masked.Single(m => m["email"]!.GetValue<string>() == email)["state"]!.GetValue<string>();

    [Fact]
    public async Task One_call_makes_a_random_address_with_its_own_folder_and_an_encrypted_expiry()
    {
        var (fake, reg) = await RegAsync();
        using var _ = reg;
        S.RoutingEnabled = false;                                  // a throwaway gets its folder even with routing off
        var (alias, info) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(10));
        Assert.True(S.Folders.TryGetValue(alias.Email, out var folder));
        Assert.Contains(fake.Boxes, b => b.Id == folder && b.Name == alias.Email);
        Assert.Contains("Throwaway", alias.Description, StringComparison.Ordinal);
        Assert.True(info.Left > TimeSpan.FromMinutes(9));

        Assert.DoesNotContain(alias.Email, File.ReadAllText(Path.Combine(SecureStore.DataDir, AppSettings.FileName)));
        AppSettings.Load();
        var stored = Throwaways.Find(alias.Email);
        Assert.NotNull(stored);
        Assert.True(Math.Abs((stored.Expires - info.Expires).TotalSeconds) < 1);
    }

    [Fact]
    public async Task Expiry_deletes_the_address_trashes_its_mail_and_removes_its_folder_while_extend_and_keep_work()
    {
        var (fake, reg) = await RegAsync();
        using var _ = reg;
        S.ThrowawayDelete = true; S.ThrowawayCleanMail = true;
        var (a1, t1) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(5));
        var (a2, _) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(5));
        var (a3, _) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(5));
        var folder1 = S.Folders[a1.Email];
        fake.Emails.Add(new FakeEmail("t1", "inbox", $$"""{"to":[{"email":"{{a1.Email}}"}],"subject":"Your code","receivedAt":"2026-10-20T10:00:00Z"}"""));
        fake.Emails.Add(new FakeEmail("t2", folder1, $$"""{"to":[{"email":"{{a1.Email}}"}],"subject":"Welcome","receivedAt":"2026-10-20T10:01:00Z"}"""));
        await reg.Mail!.SweepAsync(new Dictionary<string, string> { [a1.Email] = folder1 });
        Assert.Equal(2, fake.Emails.Count(e => e.Box == folder1));

        Assert.Empty(await Throwaways.EnforceAsync(reg));          // not yet expired
        Throwaways.Extend(a2.Email, TimeSpan.FromHours(1));
        Assert.True(Throwaways.Find(a2.Email)!.Left >= TimeSpan.FromMinutes(64));
        Throwaways.Keep(a3.Email);
        Assert.Null(Throwaways.Find(a3.Email));

        t1.Expires = DateTimeOffset.Now.AddSeconds(-1);
        Assert.Equal([a1.Email], await Throwaways.EnforceAsync(reg));
        Assert.Equal("deleted", StateOf(fake, a1.Email));
        Assert.Equal("enabled", StateOf(fake, a3.Email));          // kept, so it stays
        Assert.All(fake.Emails.Where(e => e.Id is "t1" or "t2"), e => Assert.Equal("trash", e.Box));
        Assert.DoesNotContain(fake.Boxes, b => b.Id == folder1);
        Assert.False(S.Folders.ContainsKey(a1.Email));
        Assert.Null(Throwaways.Find(a1.Email));
        Assert.DoesNotContain(reg.Aliases, x => x.Email == a1.Email);
    }

    [Fact]
    public async Task A_folder_the_user_renamed_is_never_emptied_or_removed()
    {
        var (fake, reg) = await RegAsync();
        using var _ = reg;
        S.ThrowawayDelete = true; S.ThrowawayCleanMail = true;
        var (a, t) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(5));
        var folder = S.Folders[a.Email];
        var i = fake.Boxes.FindIndex(b => b.Id == folder);
        fake.Boxes[i] = fake.Boxes[i] with { Name = "Receipts I want to keep" };
        fake.Emails.Add(new FakeEmail("k1", folder, """{"subject":"keep me"}"""));

        t.Expires = DateTimeOffset.Now.AddSeconds(-1);
        Assert.Equal([a.Email], await Throwaways.EnforceAsync(reg));
        Assert.Contains(fake.Boxes, b => b.Id == folder);
        Assert.Equal(folder, fake.Emails.Single(e => e.Id == "k1").Box);
        Assert.False(S.Folders.ContainsKey(a.Email));               // no longer routed there
    }

    [Fact]
    public async Task Disable_mode_keeps_the_address_and_its_mail_and_a_failure_is_kept_for_retry()
    {
        var (fake, reg) = await RegAsync();
        using var _ = reg;
        S.ThrowawayDelete = false; S.ThrowawayCleanMail = false;
        var (a, t) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(5));
        t.Expires = DateTimeOffset.Now.AddSeconds(-1);
        await Throwaways.EnforceAsync(reg);
        Assert.Equal("disabled", StateOf(fake, a.Email));
        Assert.True(S.Folders.ContainsKey(a.Email));
        Assert.Null(Throwaways.Find(a.Email));

        // A throwaway whose service is no longer set up can't be ended: it stays, with the reason, and is retried.
        S.Throwaways["gone@example.org"] = new ThrowawayInfo { Email = "gone@example.org", ProviderKey = "relay", Expires = DateTimeOffset.Now.AddMinutes(-1) };
        Assert.Empty(await Throwaways.EnforceAsync(reg));
        Assert.Contains("no longer set up", Throwaways.Find("gone@example.org")?.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_codes_are_picked_out_but_years_prices_and_footers_are_not()
    {
        Assert.Equal(["482913"], Throwaways.FindCodes("Your Example Shop verification code", "Hi Alex,\nYour verification code is 482913. It expires in 10 minutes.\n© 2026 Example Shop"));
        Assert.Contains("731 554", Throwaways.FindCodes("Sign in to Forum", "Use this one-time code to sign in:\n\n  731 554\n\nPrice £19.99"));
        Assert.Equal(["G-583921"], Throwaways.FindCodes("Welcome", "Your login code for Example is G-583921."));
        Assert.Empty(Throwaways.FindCodes("Newsletter", "Our 2026 summer sale starts now. Save 40% on 1200 items."));
    }

    [Fact]
    public void Confirmation_links_are_picked_out_but_unsubscribe_images_and_blogs_are_not()
    {
        var links = Throwaways.FindLinks(
            "<a href=\"https://shop.example.com/account/verify?token=abc&amp;u=1\">Verify</a> <a href=\"https://shop.example.com/unsubscribe\">x</a> <img src=\"https://cdn.example.com/logo.png\">",
            "Or paste https://shop.example.com/confirm-email/xyz into your browser. Privacy: https://shop.example.com/privacy");
        Assert.Equal(2, links.Count);
        Assert.Equal("https://shop.example.com/account/verify?token=abc&u=1", links[0]);
        Assert.Contains("https://shop.example.com/confirm-email/xyz", links);
        Assert.Empty(Throwaways.FindLinks("<a href=\"https://example.com/blog\">blog</a>", ""));
    }

    [Theory]
    [InlineData("45m", 45)]
    [InlineData("6h", 6 * 60)]
    [InlineData("3d", 3 * 24 * 60)]
    [InlineData("90", 90)]
    public void Custom_lifetimes_parse(string text, int minutes) => Assert.Equal(TimeSpan.FromMinutes(minutes), Throwaways.ParseLifetime(text));

    [Theory]
    [InlineData("0")]
    [InlineData("400d")]
    [InlineData("soon")]
    public void Silly_lifetimes_are_refused(string text) => Assert.Null(Throwaways.ParseLifetime(text));
}
