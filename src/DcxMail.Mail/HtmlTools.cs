using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DcxMail.Mail;

public sealed record InlineImage(string Cid, string Type, string Name, byte[] Data);

/// <summary>HTML helpers for composing and reading mail.</summary>
public static partial class HtmlTools
{
    [GeneratedRegex(@"(<img\b[^>]*?\bsrc\s*=\s*)([""'])data:(image/[a-z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+)\2", RegexOptions.IgnoreCase)]
    private static partial Regex DataImg();

    /// <summary>Turns every data: image in the HTML into a cid: reference and returns the image bytes to attach inline.</summary>
    public static (string Html, List<InlineImage> Images) ExtractInlineImages(string html)
    {
        var images = new List<InlineImage>();
        var outHtml = DataImg().Replace(html, m =>
        {
            byte[] data;
            try { data = Convert.FromBase64String(Regex.Replace(m.Groups[4].Value, @"\s", "")); }
            catch (FormatException) { return m.Value; }
            var type = m.Groups[3].Value.ToLowerInvariant();
            var ext = type.Split('/')[1].Replace("jpeg", "jpg").Split('+')[0];
            var cid = $"img{images.Count + 1}.{Guid.NewGuid():N}@darkcipher";
            images.Add(new InlineImage(cid, type, $"image{images.Count + 1}.{ext}", data));
            return $"{m.Groups[1].Value}{m.Groups[2].Value}cid:{cid}{m.Groups[2].Value}";
        });
        return (outHtml, images);
    }

    /// <summary>A readable plain-text version of an HTML body (for the text/plain alternative and quoting).</summary>
    public static string ToPlainText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head)\b.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|h[1-6]|li|tr|blockquote)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li\b[^>]*>", "• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<a\b[^>]*href=([""'])(http[^""']+)\1[^>]*>(.*?)</a>", m =>
            m.Groups[3].Value.Contains(m.Groups[2].Value) ? m.Groups[3].Value : $"{m.Groups[3].Value} ({m.Groups[2].Value})", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s).Replace(" ", " ");
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    public static string Encode(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Plain text as HTML (for text-only mail and quoting).</summary>
    public static string TextToHtml(string text)
    {
        var enc = Encode(text).Replace("\r\n", "\n");
        enc = Regex.Replace(enc, @"(https?://[^\s<]+)", "<a href=\"$1\">$1</a>");
        return "<div style=\"white-space:pre-wrap\">" + enc + "</div>";
    }

    /// <summary>Removes scripts, event handlers, forms and frames from received HTML (scripts are also disabled in the
    /// viewer; this is the second layer).</summary>
    public static string Sanitize(string html)
    {
        // Whole elements with their content first, then any stray opening, closing or self-closing tag of the same kinds.
        // svg/math are dropped whole: they have their own script and event-handler surfaces.
        var s = Regex.Replace(html, @"<(script|iframe|frameset|object|applet|noscript|svg|math|template)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"</?(script|iframe|frame|frameset|object|embed|applet|noscript|form|input|button|textarea|select|meta|base|link|svg|math|template|portal)\b[^>]*>", "", RegexOptions.IgnoreCase);
        // Event handlers, whatever separates them from the tag name or the previous attribute (space, / or a quote).
        s = Regex.Replace(s, @"([\s/""'])on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([\s/""'])(srcdoc|formaction)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "$1", RegexOptions.IgnoreCase);
        // Script URLs in any link-like attribute, quoted or not: the value is entity-decoded and stripped of whitespace and
        // control characters first, as a browser would, so "&#x6A;avascript:" or " java\tscript:" are caught too.
        s = Regex.Replace(s, @"([\s/""'](?:href|src|action|xlink:href|background|data|poster|lowsrc|dynsrc)\s*=\s*)(""[^""]*""|'[^']*'|[^\s>]+)", m =>
        {
            var v = WebUtility.HtmlDecode(m.Groups[2].Value.Trim('"', '\''));
            v = new string(v.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToLowerInvariant();
            return v.StartsWith("javascript:") || v.StartsWith("vbscript:") || v.StartsWith("data:text/html") || v.StartsWith("data:image/svg")
                ? m.Groups[1].Value + "\"#\"" : m.Value;
        }, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"expression\s*\(|behavior\s*:|-moz-binding", "x", RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>Replaces cid: references with data: URIs from the message's inline parts.</summary>
    public static string ResolveCids(string html, IReadOnlyDictionary<string, (string Type, byte[] Data)> parts) =>
        Regex.Replace(html, @"cid:([^""'\s>)]+)", m =>
        {
            var key = Uri.UnescapeDataString(m.Groups[1].Value).Trim('<', '>');
            return parts.TryGetValue(key, out var p) ? $"data:{p.Type};base64,{Convert.ToBase64String(p.Data)}" : m.Value;
        }, RegexOptions.IgnoreCase);

    /// <summary>The page the reader shows: themed, with a Content-Security-Policy that blocks scripts and (unless
    /// allowed) every remote resource, so tracking pixels cannot tell the sender you opened it.</summary>
    public static string ReaderPage(string bodyHtml, bool remoteImages, string bg, string fg, string accent, string muted)
    {
        var img = remoteImages ? "data: https: http:" : "data:";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append($"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src {img}; style-src 'unsafe-inline'{(remoteImages ? " https:" : "")}; font-src data:{(remoteImages ? " https:" : "")}\">");
        sb.Append("<style>html{background:").Append(bg).Append(";}body{margin:0;padding:18px 22px;font-family:'Segoe UI',sans-serif;font-size:14px;color:")
          .Append(fg).Append(";}a{color:").Append(accent).Append(";}blockquote{border-left:3px solid ").Append(muted)
          .Append(";margin:8px 0;padding-left:10px;color:").Append(muted).Append(";}img{max-width:100%;height:auto;}")
          .Append(".mail{background:#fff;color:#111;border-radius:3px;padding:16px;overflow-x:auto;}</style></head><body>");
        sb.Append("<div class=\"mail\">").Append(bodyHtml).Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>The quoted original under a reply or forward.</summary>
    public static string Quote(MessageDetail m, bool forward)
    {
        var from = Encode(string.Join(", ", m.From.Select(a => a.ToString())));
        var head = forward
            ? $"<p>---------- Forwarded message ----------<br>From: {from}<br>Date: {m.Sent.LocalDateTime:f}<br>Subject: {Encode(m.Subject)}<br>To: {Encode(string.Join(", ", m.To.Select(a => a.ToString())))}</p>"
            : $"<p>On {m.Sent.LocalDateTime:f}, {from} wrote:</p>";
        var body = m.Html.Length > 0 ? Sanitize(StripDocument(m.Html)) : TextToHtml(m.Text);
        return $"<p><br></p><p><br></p>{head}<blockquote style=\"border-left:3px solid #ccc;margin:0;padding-left:10px\">{body}</blockquote>";
    }

    private static string StripDocument(string html)
    {
        var m = Regex.Match(html, @"<body\b[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : html;
    }
}
