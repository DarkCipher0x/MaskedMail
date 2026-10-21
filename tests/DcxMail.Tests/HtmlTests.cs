using System.Text.Json.Nodes;
using DcxMail.Mail;
using DcxMail.Providers;
using Xunit;

namespace DcxMail.Tests;

/// <summary>The HTML tools (sanitizer, inline images, plain text, reader page) and the small parsers.</summary>
public sealed class HtmlTests
{
    [Fact]
    public void Data_images_become_cid_parts()
    {
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var (html, imgs) = HtmlTools.ExtractInlineImages($"<p>hi</p><img alt=\"x\" src=\"data:image/png;base64,{png}\"><img src='data:image/jpeg;base64,{png}'>");
        Assert.Equal(2, imgs.Count);
        Assert.DoesNotContain("data:", html);
        Assert.Contains("cid:" + imgs[0].Cid, html);
        Assert.Equal("image/jpeg", imgs[1].Type);
        Assert.Equal(7, imgs[0].Data.Length);
    }

    [Fact]
    public void Plain_text_alternative_keeps_text_lists_and_links_and_drops_scripts()
    {
        var t = HtmlTools.ToPlainText("<div>Hello&nbsp;<b>there</b></div><ul><li>one</li><li>two</li></ul><a href=\"https://example.com/x\">site</a><script>bad()</script>");
        Assert.Contains("Hello there", t);
        Assert.Contains("• one", t);
        Assert.Contains("site (https://example.com/x)", t);
        Assert.DoesNotContain("bad()", t);
    }

    [Fact]
    public void Sanitize_removes_scripts_handlers_javascript_links_forms_and_frames()
    {
        var s = HtmlTools.Sanitize("<p onclick=\"x()\">a</p><script>alert(1)</script><img src=x onerror=alert(1)><a href=\"javascript:evil()\">l</a><form action=y><input></form><iframe src=z></iframe>");
        Assert.DoesNotContain("script", s, StringComparison.OrdinalIgnoreCase);
        foreach (var bad in new[] { "onerror", "onclick", "javascript:", "<form", "iframe" }) Assert.DoesNotContain(bad, s);
    }

    [Theory]
    [InlineData("<img/onerror=alert(1) src=x>")]
    [InlineData("<svg onload=alert(1)><circle/></svg>")]
    [InlineData("<a href=\"  javascript:alert(1)\">x</a>")]
    [InlineData("<a href=JaVaScRiPt:alert(1)>x</a>")]
    [InlineData("<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>")]
    [InlineData("<button formaction=javascript:alert(1)>x</button>")]
    [InlineData("<a href=\"&#x6A;avascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"java&#09;script:alert(1)\">x</a>")]
    [InlineData("<img src=x onerror\n=alert(1)>")]
    [InlineData("<div style=\"background:url(x);width:expression(alert(1))\">")]
    [InlineData("<math><a xlink:href=javascript:alert(1)>x</a></math>")]
    [InlineData("<object data=\"data:text/html,<script>alert(1)</script>\"></object>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>")]
    public void Sanitize_stops_known_bypasses(string vector)
    {
        var s = HtmlTools.Sanitize(vector).ToLowerInvariant();
        foreach (var bad in new[] { "onerror", "onload", "<svg", "srcdoc", "formaction", "javascript", "java\tscript", "&#x6a;avascript", "expression(", "data:text/html", "<script" })
            Assert.DoesNotContain(bad, s);
    }

    [Fact]
    public void Sanitize_leaves_ordinary_mail_alone()
    {
        var ok = HtmlTools.Sanitize("<p style=\"color:red\">Hi <a href=\"https://example.com/x?a=1&amp;b=2\">link</a> <img src=\"cid:a@b\"></p>");
        Assert.Contains("https://example.com/x?a=1&amp;b=2", ok);
        Assert.Contains("cid:a@b", ok);
        Assert.Contains("color:red", ok);
    }

    [Fact]
    public void Cid_references_are_resolved_for_the_reader() =>
        Assert.Contains("data:image/png;base64,AQ==",
            HtmlTools.ResolveCids("<img src=\"cid:abc@x\">", new Dictionary<string, (string, byte[])> { ["abc@x"] = ("image/png", [1]) }));

    [Fact]
    public void Reader_page_blocks_scripts_and_remote_content_by_default()
    {
        var p = HtmlTools.ReaderPage("<p>x</p>", false, "#000", "#fff", "#0ff", "#888");
        Assert.Contains("default-src 'none'", p);
        Assert.Contains("img-src data:;", p);
        Assert.DoesNotContain("script-src", p);
    }

    [Fact]
    public void Addresses_parse_names_and_separators_and_report_bad_ones()
    {
        var ok = Addresses.Parse("a@b.com; Sam <sam@x.org>, nope", out var bad);
        Assert.Equal(["a@b.com", "sam@x.org"], ok);
        Assert.Equal(["nope"], bad);
    }

    [Fact]
    public void Folder_names_are_lower_case_without_path_separators()
    {
        Assert.Equal("shop.abc@fastmail.com", MailService.FolderName(" Shop.ABC@Fastmail.com "));
        Assert.Equal("a_b@c", MailService.FolderName("a/b@c"));
    }

    [Fact]
    public void Routing_matches_To_Cc_Bcc_and_Delivered_To()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mask@fm.com"] = "F1" };
        Assert.Equal("F1", MailService.MatchFolder(JsonNode.Parse("""{"to":[{"name":"","email":"MASK@fm.com"}]}""")!.AsObject(), map));
        Assert.Equal("F1", MailService.MatchFolder(JsonNode.Parse("""{"to":[{"email":"list@x.com"}],"header:Delivered-To:asAddresses:all":[[{"email":"mask@fm.com"}]]}""")!.AsObject(), map));
        Assert.Null(MailService.MatchFolder(JsonNode.Parse("""{"to":[{"email":"other@x.com"}]}""")!.AsObject(), map));
    }

    [Fact]
    public void Addy_send_from_uses_the_documented_format()
    {
        Assert.Equal("shop+hello=store.com@johndoe.addy.io", AddyProvider.SendFromAddress("shop@johndoe.addy.io", "hello@store.com"));
        Assert.Equal("first+hello+whatever=example.com@johndoe.anonaddy.com", AddyProvider.SendFromAddress("first@johndoe.anonaddy.com", "hello+whatever@example.com"));
    }

    [Fact]
    public void Fastmail_prefix_and_origin_follow_the_API_rules()
    {
        Assert.Equal("my_shop_co_uk", FastmailProvider.CleanPrefix("My-Shop.Co UK!"));
        Assert.Equal(64, FastmailProvider.CleanPrefix(new string('a', 80)).Length);
        Assert.Equal("https://www.example.com", FastmailProvider.Origin("www.example.com/login?x=1"));
        Assert.Equal("", FastmailProvider.Origin(""));
    }
}
