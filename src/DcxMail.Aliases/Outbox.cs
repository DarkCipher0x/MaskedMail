using System.Text.Json.Serialization;
using DcxMail.Providers;
using DcxMail.Services;

namespace DcxMail.Mail;

/// <summary>One email waiting in the queue. Everything needed to send it later, including inline images (as data: URIs
/// in the HTML) and attachments, so it survives a restart. Stored encrypted with the rest (outbox.dcx).</summary>
public sealed class QueuedEmail
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    /// <summary>The address it is sent "from": a masked address or a Fastmail identity.</summary>
    public string FromEmail { get; set; } = "";
    /// <summary>The service of the masked address it is sent from (null = an ordinary Fastmail identity).</summary>
    public string? AliasProvider { get; set; }
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    /// <summary>Addresses already in reverse-alias form (taken from a received message) that must not be converted again.</summary>
    public List<string> KeepAsIs { get; set; } = [];
    public string Subject { get; set; } = "";
    public string Html { get; set; } = "";
    public List<OutgoingFile> Attachments { get; set; } = [];
    public string? InReplyTo { get; set; }
    public List<string> References { get; set; } = [];
    /// <summary>How many identical copies to send.</summary>
    public int Copies { get; set; } = 1;
    /// <summary>Unticked items stay in the queue but are skipped by "Send all".</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Copies already sent (a failure part-way leaves the rest queued).</summary>
    public int SentCopies { get; set; }
    public string? LastError { get; set; }

    [JsonIgnore] public string ToText => string.Join(", ", To) + (Cc.Count > 0 ? "  cc " + string.Join(", ", Cc) : "");
    [JsonIgnore] public string CopiesText => Copies > 1 ? $"×{Copies}" + (SentCopies > 0 ? $" ({SentCopies} sent)" : "") : "";
    [JsonIgnore] public string AttachText => Attachments.Count > 0 ? $"📎 {Attachments.Count}" : "";
    [JsonIgnore] public string StatusText => LastError != null ? "⚠ " + LastError : Enabled ? "ready" : "skipped";

    public QueuedEmail Clone() => new()
    {
        FromEmail = FromEmail, AliasProvider = AliasProvider, To = [.. To], Cc = [.. Cc], KeepAsIs = [.. KeepAsIs],
        Subject = Subject, Html = Html, Attachments = [.. Attachments], InReplyTo = InReplyTo, References = [.. References],
        Copies = Copies, Enabled = Enabled,
    };
}

/// <summary>
/// The outgoing queue: compose several emails (or several copies of one), then send them together, in order, with a
/// pause between each. Each item can be edited, duplicated, split into one copy per recipient, skipped or reordered.
/// </summary>
public static class Outbox
{
    public const string FileName = "outbox.dcx";
    public static List<QueuedEmail> Items { get; private set; } = [];
    public static event Action? Changed;

    public static int ReadyCount => Items.Count(i => i.Enabled);

    public static void Load()
    {
        Items = SecureStore.Load<List<QueuedEmail>>(FileName) ?? [];
        Changed?.Invoke();
    }

    public static void Save()
    {
        SecureStore.Save(FileName, Items);
        Changed?.Invoke();
    }

    /// <summary>Adds an item, or replaces the one with the same id (an item edited in the composer).</summary>
    public static void Put(QueuedEmail q)
    {
        var i = Items.FindIndex(x => x.Id == q.Id);
        if (i >= 0) Items[i] = q; else Items.Add(q);
        Save();
    }

    public static void Remove(string id) { Items.RemoveAll(x => x.Id == id); Save(); }

    public static QueuedEmail Duplicate(string id)
    {
        var src = Items.First(x => x.Id == id);
        var copy = src.Clone();
        Items.Insert(Items.IndexOf(src) + 1, copy);
        Save();
        return copy;
    }

    public static void Move(string id, int delta)
    {
        var i = Items.FindIndex(x => x.Id == id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        (Items[i], Items[j]) = (Items[j], Items[i]);
        Save();
    }

    /// <summary>Replaces one item addressed to several people with one copy per person (To and Cc), so nobody sees
    /// the other recipients. Returns how many items it became.</summary>
    public static int SplitPerRecipient(string id)
    {
        var src = Items.First(x => x.Id == id);
        var people = src.To.Concat(src.Cc).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (people.Count < 2) return 1;
        var at = Items.IndexOf(src);
        Items.RemoveAt(at);
        foreach (var p in people)
        {
            var c = src.Clone();
            c.To = [p]; c.Cc = [];
            Items.Insert(at++, c);
        }
        Save();
        return people.Count;
    }

    /// <summary>Turns a queued item into the message that goes out: resolves the sender (masked address, identity or
    /// reverse alias) exactly as the composer's Send does.</summary>
    public static async Task<OutgoingMessage> BuildAsync(QueuedEmail q, ProviderRegistry reg, List<(string Id, string Name, string Email)> identities, CancellationToken ct = default)
    {
        var msg = new OutgoingMessage
        {
            FromName = AppSettings.Current.SenderName,
            Subject = q.Subject,
            Html = q.Html,
            Attachments = q.Attachments,
            InReplyTo = q.InReplyTo,
            References = q.References,
        };
        Alias? alias = null;
        IAliasProvider? provider = null;
        if (q.AliasProvider != null)
        {
            provider = reg.Get(q.AliasProvider) ?? throw new ProviderException($"{ProviderRegistry.NameOf(q.AliasProvider)} is no longer set up in Settings.");
            alias = reg.Aliases.FirstOrDefault(a => a.ProviderKey == q.AliasProvider && a.Email.Equals(q.FromEmail, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ProviderException($"{q.FromEmail} is no longer one of your masked addresses.");
            if (!alias.Enabled) throw new ProviderException($"{q.FromEmail} is disabled.");
        }
        var keep = new HashSet<string>(q.KeepAsIs, StringComparer.OrdinalIgnoreCase);
        if (provider?.SendMode == SendMode.ReverseAlias)
        {
            msg.FromEmail = RealSender(alias!, identities, reg);
            foreach (var a in q.To) msg.To.Add(keep.Contains(a) ? a : await provider.ReverseAddressAsync(alias!, a, ct));
            foreach (var a in q.Cc) msg.Cc.Add(keep.Contains(a) ? a : await provider.ReverseAddressAsync(alias!, a, ct));
        }
        else if (provider?.SendMode == SendMode.None)
            throw new ProviderException($"{provider.Name} cannot send to new contacts.");
        else
        {
            msg.FromEmail = q.FromEmail;
            msg.FromIsFastmailMasked = provider?.SendMode == SendMode.Direct;
            msg.To = [.. q.To]; msg.Cc = [.. q.Cc];
        }
        return msg;
    }

    /// <summary>For reverse-alias services: the Fastmail address the alias forwards to (it must send), else the first identity.</summary>
    public static string RealSender(Alias a, List<(string Id, string Name, string Email)> identities, ProviderRegistry reg) =>
        a.ForwardsTo is { Length: > 0 } fwd && identities.Any(i => i.Email.Equals(fwd, StringComparison.OrdinalIgnoreCase)) ? fwd
        : identities.FirstOrDefault(i => !i.Email.StartsWith('*')).Email ?? reg.Jmap?.Username ?? "";

    /// <summary>Sends every remaining copy of one item. Throws on the first failure (copies already sent are counted).</summary>
    public static async Task SendItemAsync(QueuedEmail q, ProviderRegistry reg, List<(string Id, string Name, string Email)> identities, TimeSpan delay, CancellationToken ct = default)
    {
        var msg = await BuildAsync(q, reg, identities, ct);
        // A service that sends from its own addresses itself (Forward Email) doesn't need Fastmail for it.
        var viaService = q.AliasProvider != null && reg.Get(q.AliasProvider) is { SendMode: SendMode.Service } and ISendingProvider sp
            && reg.Aliases.FirstOrDefault(a => a.ProviderKey == q.AliasProvider && a.Email.Equals(q.FromEmail, StringComparison.OrdinalIgnoreCase)) is { } from
            ? (Func<Task>)(() => sp.SendAsync(from, msg, ct)) : null;
        var mail = viaService == null ? reg.Mail ?? throw new ProviderException("Sending needs a Fastmail API token (Settings → Fastmail).") : null;
        while (q.SentCopies < Math.Max(1, q.Copies))
        {
            ct.ThrowIfCancellationRequested();
            if (q.SentCopies > 0 && delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            if (viaService != null) await viaService(); else await mail!.SendAsync(msg, ct);
            q.SentCopies++;
        }
    }

    /// <summary>Sends the given items in order. Sent items leave the queue; failed ones stay with their error. With
    /// <paramref name="stopOnError"/> the run ends at the first failure. Returns (sent emails, failed items).</summary>
    public static async Task<(int Sent, int Failed)> SendAllAsync(ProviderRegistry reg, IEnumerable<QueuedEmail> items, TimeSpan delay, bool stopOnError,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var mail = reg.Mail ?? throw new ProviderException("Sending needs a Fastmail API token (Settings → Fastmail).");
        var identities = await mail.IdentitiesAsync(ct);
        var list = items.ToList();
        int sent = 0, failed = 0, n = 0;
        foreach (var q in list)
        {
            n++;
            if (n > 1 && delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            progress?.Report($"sending {n} of {list.Count}: {q.Subject}");
            var before = q.SentCopies;
            try
            {
                await SendItemAsync(q, reg, identities, delay, ct);
                sent += q.SentCopies - before;
                Items.Remove(q);
                AuditLog.Write("queued email sent", $"from {q.FromEmail} to {q.To.Count + q.Cc.Count} recipient(s){(q.Copies > 1 ? $", {q.Copies} copies" : "")}");
            }
            catch (OperationCanceledException) { sent += q.SentCopies - before; Save(); throw; }
            catch (Exception ex)
            {
                sent += q.SentCopies - before;
                q.LastError = ex.Message;
                failed++;
                if (stopOnError) { Save(); return (sent, failed); }
            }
            Save();
        }
        return (sent, failed);
    }
}
