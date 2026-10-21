using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>For inboxes whose service has no "read" flag or message delete (TempMail.lol, DropMail): the messages last
/// fetched, which ones were opened, and which were deleted here. Kept in memory only, for this run of the app.</summary>
internal sealed class InboxState
{
    private readonly Dictionary<string, List<MessageDetail>> _mail = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = [], _hidden = [];

    /// <summary>Stores what the service returned (newest first) and gives the page asked for.</summary>
    public (List<MessageSummary> Items, int Total) Page(Alias alias, List<MessageDetail> fresh, int position, int size = 50)
    {
        var visible = fresh.Where(m => !_hidden.Contains(K(alias, m.Id))).OrderByDescending(m => m.Sent).ToList();
        lock (_mail) _mail[alias.Email] = visible;
        var page = visible.Skip(position).Take(size).Select(m => new MessageSummary
        {
            Id = m.Id,
            Subject = m.Subject,
            From = m.From,
            To = m.To,
            Received = m.Sent,
            Preview = Preview(m.Text),
            Unread = !_seen.Contains(K(alias, m.Id)),
            HasAttachment = m.Attachments.Count > 0,
        }).ToList();
        return (page, visible.Count);
    }

    public MessageDetail Get(Alias alias, string id)
    {
        lock (_mail)
            return _mail.TryGetValue(alias.Email, out var list) && list.FirstOrDefault(m => m.Id == id) is { } m ? m
                : throw new ProviderException("That message is no longer in the inbox.");
    }

    public void SetSeen(Alias alias, string id, bool seen) { if (seen) _seen.Add(K(alias, id)); else _seen.Remove(K(alias, id)); }
    public void Hide(Alias alias, string id) => _hidden.Add(K(alias, id));

    private static string K(Alias a, string id) => a.Email + "\n" + id;

    public static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 140 ? flat[..140] + "…" : flat;
    }

    /// <summary>"Name &lt;a@b&gt;" or "a@b" → an address.</summary>
    public static Addr Parse(string s)
    {
        s = s.Trim();
        var lt = s.LastIndexOf('<');
        return lt >= 0 && s.EndsWith('>') ? new Addr(s[..lt].Trim().Trim('"'), s[(lt + 1)..^1].Trim()) : new Addr("", s);
    }
}
