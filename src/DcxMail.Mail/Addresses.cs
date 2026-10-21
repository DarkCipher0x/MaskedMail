using System.Net.Mail;

namespace DcxMail.Mail;

/// <summary>Recipient lists as typed by the user ("a@example.com; Sam &lt;sam@example.org&gt;, …").</summary>
public static class Addresses
{
    /// <summary>The valid addresses, in order; anything that isn't one goes to <paramref name="bad"/>.</summary>
    public static List<string> Parse(string text, out List<string> bad)
    {
        var ok = new List<string>();
        bad = [];
        foreach (var part in text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { ok.Add(new MailAddress(part).Address); }
            catch (FormatException) { bad.Add(part); }
        }
        return ok;
    }
}
