using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DcxMail.Mail;

public sealed record Mailbox(string Id, string Name, string? ParentId, string? Role, int Total, int Unread);

public sealed record Addr(string Name, string Email)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} <{Email}>";
    public string Short => string.IsNullOrWhiteSpace(Name) ? Email : Name;
}

public sealed class MessageSummary
{
    public string Id { get; init; } = "";
    public string Subject { get; init; } = "";
    public List<Addr> From { get; init; } = [];
    public List<Addr> To { get; init; } = [];
    public DateTimeOffset Received { get; init; }
    public string Preview { get; init; } = "";
    public bool Unread { get; set; }
    public bool HasAttachment { get; init; }
    public string FromText => From.Count > 0 ? From[0].Short : "(no sender)";
    public string DateText => Received.LocalDateTime.Date == DateTime.Today ? Received.LocalDateTime.ToString("HH:mm") : Received.LocalDateTime.ToString("dd MMM yy");
}

public sealed record Attachment(string BlobId, string Name, string Type, long Size, string? Cid, bool Inline);

public sealed class MessageDetail
{
    public string Id { get; init; } = "";
    public string Subject { get; init; } = "";
    public List<Addr> From { get; init; } = [];
    public List<Addr> To { get; init; } = [];
    public List<Addr> Cc { get; init; } = [];
    public List<Addr> ReplyTo { get; init; } = [];
    public DateTimeOffset Sent { get; init; }
    public string Html { get; init; } = "";
    public string Text { get; init; } = "";
    public string? MessageId { get; init; }
    public List<string> References { get; init; } = [];
    public List<Attachment> Attachments { get; init; } = [];
}

public sealed class OutgoingFile
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "application/octet-stream";
    public byte[] Data { get; init; } = [];
}

public sealed class OutgoingMessage
{
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "";
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public string Subject { get; set; } = "";
    public string Html { get; set; } = "";
    public List<OutgoingFile> Attachments { get; set; } = [];
    public string? InReplyTo { get; set; }
    public List<string> References { get; set; } = [];
    /// <summary>Set when the From address is a Fastmail masked email: an identity for it is created if none exists.</summary>
    public bool FromIsFastmailMasked { get; set; }
}

/// <summary>Fastmail mail over JMAP: folders, the routing sweep, reading and sending.</summary>
public sealed class MailService(JmapClient jmap)
{
    public JmapClient Jmap { get; } = jmap;
    private List<Mailbox>? _boxes;

    public async Task<List<Mailbox>> MailboxesAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_boxes != null && !refresh) return _boxes;
        var res = await Jmap.CallOneAsync("Mailbox/get", new JsonObject
        {
            ["ids"] = null,
            ["properties"] = new JsonArray("id", "name", "parentId", "role", "totalEmails", "unreadEmails"),
        }, ct);
        _boxes = res["list"]!.AsArray().Select(m => new Mailbox(
            m!["id"]!.GetValue<string>(), m["name"]!.GetValue<string>(), m["parentId"]?.GetValue<string>(),
            m["role"]?.GetValue<string>(), m["totalEmails"]?.GetValue<int>() ?? 0, m["unreadEmails"]?.GetValue<int>() ?? 0)).ToList();
        return _boxes;
    }

    public async Task<string> RoleIdAsync(string role, CancellationToken ct = default) =>
        (await MailboxesAsync(false, ct)).FirstOrDefault(b => b.Role == role)?.Id
        ?? throw new JmapException($"Your Fastmail account has no {role} folder.");

    /// <summary>Folder names Fastmail accepts: the alias address itself, with characters a mail server may treat as a
    /// hierarchy separator replaced.</summary>
    public static string FolderName(string address) => Regex.Replace(address.Trim().ToLowerInvariant(), @"[/\\]", "_");

    /// <summary>Finds or creates a folder path such as "Inbox/automation/shop@example.com" and returns its id. The first
    /// segment may name a role folder ("Inbox").</summary>
    public async Task<string> EnsurePathAsync(string path, CancellationToken ct = default)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("Empty folder path.");
        var boxes = await MailboxesAsync(false, ct);
        string? parent = null;
        for (int i = 0; i < parts.Length; i++)
        {
            var name = parts[i];
            var found = boxes.FirstOrDefault(b => b.ParentId == parent && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? (i == 0 ? boxes.FirstOrDefault(b => b.ParentId == null && string.Equals(b.Role, name, StringComparison.OrdinalIgnoreCase)) : null);
            if (found != null) { parent = found.Id; continue; }

            var create = new JsonObject { ["name"] = name, ["parentId"] = parent };
            var res = await Jmap.CallOneAsync("Mailbox/set", new JsonObject { ["create"] = new JsonObject { ["m"] = create } }, ct);
            JmapClient.CheckSet(res, $"Creating folder \"{name}\"");
            var id = res["created"]!["m"]!["id"]!.GetValue<string>();
            boxes.Add(new Mailbox(id, name, parent, null, 0, 0));
            parent = id;
        }
        return parent!;
    }

    public string PathOf(string mailboxId)
    {
        var boxes = _boxes ?? [];
        var parts = new List<string>();
        for (var b = boxes.FirstOrDefault(x => x.Id == mailboxId); b != null; b = boxes.FirstOrDefault(x => x.Id == b.ParentId))
            parts.Insert(0, b.Name);
        return string.Join("/", parts);
    }

    public async Task<(List<MessageSummary> Items, int Total)> ListAsync(string mailboxId, int position = 0, int limit = 60, string? search = null, CancellationToken ct = default)
    {
        var filter = new JsonObject { ["inMailbox"] = mailboxId };
        if (!string.IsNullOrWhiteSpace(search)) filter["text"] = search.Trim();
        var res = await Jmap.CallAsync([
            ("Email/query", new JsonObject
            {
                ["filter"] = filter,
                ["sort"] = new JsonArray(new JsonObject { ["property"] = "receivedAt", ["isAscending"] = false }),
                ["position"] = position, ["limit"] = limit, ["calculateTotal"] = true,
            }),
            ("Email/get", new JsonObject
            {
                ["#ids"] = JmapClient.Ref(0, "Email/query", "/ids"),
                ["properties"] = new JsonArray("id", "subject", "from", "to", "receivedAt", "preview", "keywords", "hasAttachment"),
            }),
        ], ct);
        var total = res[0]["total"]?.GetValue<int>() ?? 0;
        var items = res[1]["list"]!.AsArray().Select(e => new MessageSummary
        {
            Id = e!["id"]!.GetValue<string>(),
            Subject = e["subject"]?.GetValue<string>() ?? "(no subject)",
            From = Addrs(e["from"]),
            To = Addrs(e["to"]),
            Received = DateTimeOffset.Parse(e["receivedAt"]!.GetValue<string>()),
            Preview = e["preview"]?.GetValue<string>() ?? "",
            Unread = e["keywords"] is not JsonObject k || !k.ContainsKey("$seen"),
            HasAttachment = e["hasAttachment"]?.GetValue<bool>() ?? false,
        }).ToList();
        return (items, total);
    }

    public async Task<MessageDetail> GetAsync(string id, CancellationToken ct = default)
    {
        var res = await Jmap.CallOneAsync("Email/get", new JsonObject
        {
            ["ids"] = new JsonArray(id),
            ["properties"] = new JsonArray("id", "subject", "from", "to", "cc", "replyTo", "sentAt", "receivedAt", "messageId", "references",
                "htmlBody", "textBody", "attachments", "bodyValues"),
            ["fetchHTMLBodyValues"] = true, ["fetchTextBodyValues"] = true, ["maxBodyValueBytes"] = 4_000_000,
        }, ct);
        var e = res["list"]!.AsArray().FirstOrDefault() ?? throw new JmapException("That message no longer exists.");
        var values = e["bodyValues"] as JsonObject ?? new JsonObject();
        string Join(JsonNode? parts, string type) => string.Join("\n", (parts as JsonArray ?? [])
            .Where(p => p!["type"]?.GetValue<string>() == type && p["partId"] != null)
            .Select(p => values[p!["partId"]!.GetValue<string>()]?["value"]?.GetValue<string>() ?? ""));
        var atts = (e["attachments"] as JsonArray ?? []).Select(a => new Attachment(
            a!["blobId"]!.GetValue<string>(), a["name"]?.GetValue<string>() ?? "attachment", a["type"]?.GetValue<string>() ?? "application/octet-stream",
            a["size"]?.GetValue<long>() ?? 0, a["cid"]?.GetValue<string>()?.Trim('<', '>'), a["disposition"]?.GetValue<string>() == "inline")).ToList();
        return new MessageDetail
        {
            Id = id,
            Subject = e["subject"]?.GetValue<string>() ?? "(no subject)",
            From = Addrs(e["from"]), To = Addrs(e["to"]), Cc = Addrs(e["cc"]), ReplyTo = Addrs(e["replyTo"]),
            Sent = DateTimeOffset.Parse((e["sentAt"] ?? e["receivedAt"])!.GetValue<string>()),
            Html = Join(e["htmlBody"], "text/html"),
            Text = Join(e["textBody"], "text/plain"),
            MessageId = (e["messageId"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(),
            References = (e["references"] as JsonArray ?? []).Select(r => r!.GetValue<string>()).ToList(),
            Attachments = atts,
        };
    }

    public Task SetSeenAsync(string id, bool seen, CancellationToken ct = default) =>
        UpdateAsync(id, new JsonObject { ["keywords/$seen"] = seen ? true : null }, ct);

    public async Task MoveToTrashAsync(string id, CancellationToken ct = default)
    {
        var trash = await RoleIdAsync("trash", ct);
        await UpdateAsync(id, new JsonObject { ["mailboxIds"] = new JsonObject { [trash] = true } }, ct);
    }

    /// <summary>Throwaway clean-up: moves every message in a folder to Trash (recoverable there, never destroyed here),
    /// then removes the folder once it is empty. Returns how many messages were moved, or -1 when the folder was left
    /// alone: a system folder, or one that is no longer named <paramref name="expectedName"/> (renamed or reused by
    /// the user, so it is not ours to empty).</summary>
    public async Task<int> EmptyAndRemoveFolderAsync(string folderId, string expectedName, CancellationToken ct = default)
    {
        var trash = await RoleIdAsync("trash", ct);
        var box = (await MailboxesAsync(true, ct)).FirstOrDefault(b => b.Id == folderId);
        if (box == null) return 0;                                                     // already gone
        if (folderId == trash || box.Role != null || !string.Equals(box.Name, expectedName, StringComparison.OrdinalIgnoreCase)) return -1;
        int moved = 0;
        for (int round = 0; round < 20; round++)
        {
            var q = await Jmap.CallOneAsync("Email/query", new JsonObject { ["filter"] = new JsonObject { ["inMailbox"] = folderId }, ["limit"] = 250 }, ct);
            var ids = (q["ids"] as JsonArray ?? []).Select(i => i!.GetValue<string>()).ToList();
            if (ids.Count == 0) break;
            var update = new JsonObject();
            foreach (var id in ids) update[id] = new JsonObject { ["mailboxIds"] = new JsonObject { [trash] = true } };
            JmapClient.CheckSet(await Jmap.CallOneAsync("Email/set", new JsonObject { ["update"] = update }, ct), "Moving mail to Trash");
            moved += ids.Count;
        }
        var res = await Jmap.CallOneAsync("Mailbox/set", new JsonObject { ["destroy"] = new JsonArray(folderId), ["onDestroyRemoveEmails"] = false }, ct);
        JmapClient.CheckSet(res, "Removing the folder");
        _boxes?.RemoveAll(b => b.Id == folderId);
        return moved;
    }

    private async Task UpdateAsync(string id, JsonObject patch, CancellationToken ct)
    {
        var res = await Jmap.CallOneAsync("Email/set", new JsonObject { ["update"] = new JsonObject { [id] = patch } }, ct);
        JmapClient.CheckSet(res, "Updating the message");
    }

    /// <summary>
    /// The routing sweep: looks at the newest Inbox mail and moves every message addressed to a routed alias
    /// (To, Cc, Bcc or Delivered-To) into that alias's folder. Returns how many were moved. The folder list is only
    /// fetched when there is mail to move (a quiet sweep is one request), and a folder deleted in Fastmail is never a
    /// move target: it is reported in <see cref="LastMissingFolders"/> so the caller can forget it.
    /// </summary>
    public async Task<int> SweepAsync(IReadOnlyDictionary<string, string> folderByAddress, int lookAt = 250, CancellationToken ct = default)
    {
        LastMissingFolders = new HashSet<string>();
        if (folderByAddress.Count == 0) return 0;
        var inbox = await RoleIdAsync("inbox", ct);
        var res = await Jmap.CallAsync([
            ("Email/query", new JsonObject
            {
                ["filter"] = new JsonObject { ["inMailbox"] = inbox },
                ["sort"] = new JsonArray(new JsonObject { ["property"] = "receivedAt", ["isAscending"] = false }),
                ["limit"] = lookAt,
            }),
            ("Email/get", new JsonObject
            {
                ["#ids"] = JmapClient.Ref(0, "Email/query", "/ids"),
                ["properties"] = new JsonArray("id", "to", "cc", "bcc", "header:Delivered-To:asAddresses:all", "header:X-Delivered-To:asAddresses:all"),
            }),
        ], ct);
        var targets = new Dictionary<string, string>();          // message id → folder id
        foreach (var e in res[1]["list"]!.AsArray())
        {
            var target = MatchFolder(e!.AsObject(), folderByAddress);
            if (target != null && target != inbox) targets[e["id"]!.GetValue<string>()] = target;
        }
        if (targets.Count == 0) return 0;

        var live = (await MailboxesAsync(true, ct)).Select(b => b.Id).ToHashSet();
        LastMissingFolders = targets.Values.Where(f => !live.Contains(f)).ToHashSet();
        var update = new JsonObject();
        foreach (var (id, target) in targets.Where(t => live.Contains(t.Value)))
            update[id] = new JsonObject { ["mailboxIds/" + inbox] = null, ["mailboxIds/" + target] = true };
        if (update.Count == 0) return 0;
        var set = await Jmap.CallOneAsync("Email/set", new JsonObject { ["update"] = update }, ct);
        JmapClient.CheckSet(set, "Filing mail");
        return update.Count;
    }

    /// <summary>Folder ids the last sweep wanted to move mail into but which no longer exist in Fastmail.</summary>
    public IReadOnlySet<string> LastMissingFolders { get; private set; } = new HashSet<string>();

    /// <summary>The folder for the first recipient address that belongs to a routed alias, or null.</summary>
    public static string? MatchFolder(JsonObject email, IReadOnlyDictionary<string, string> folderByAddress)
    {
        foreach (var field in new[] { "to", "cc", "bcc" })
            foreach (var a in Addrs(email[field]))
                if (folderByAddress.TryGetValue(a.Email, out var f)) return f;
        foreach (var field in new[] { "header:Delivered-To:asAddresses:all", "header:X-Delivered-To:asAddresses:all" })
            if (email[field] is JsonArray headers)
                foreach (var h in headers)
                    foreach (var a in Addrs(h))
                        if (folderByAddress.TryGetValue(a.Email, out var f)) return f;
        return null;
    }

    public async Task<List<(string Id, string Name, string Email)>> IdentitiesAsync(CancellationToken ct = default)
    {
        var res = await Jmap.CallOneAsync("Identity/get", new JsonObject { ["ids"] = null }, ct);
        return res["list"]!.AsArray().Select(i => (i!["id"]!.GetValue<string>(), i["name"]?.GetValue<string>() ?? "", i["email"]!.GetValue<string>())).ToList();
    }

    /// <summary>The identity to send as: an exact match, then a wildcard identity for the domain (*@domain), and for a
    /// Fastmail masked address a new identity is created (Fastmail lets you send from addresses you own).</summary>
    public async Task<string> IdentityForAsync(string email, string name, bool createIfMissing, CancellationToken ct = default)
    {
        var ids = await IdentitiesAsync(ct);
        var exact = ids.FirstOrDefault(i => i.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        if (exact.Id != null) return exact.Id;
        var domain = email[(email.IndexOf('@') + 1)..];
        var wild = ids.FirstOrDefault(i => i.Email.Equals("*@" + domain, StringComparison.OrdinalIgnoreCase));
        if (wild.Id != null) return wild.Id;
        if (!createIfMissing)
            throw new JmapException($"There is no Fastmail sending identity for {email}. Add it in Fastmail (Settings → Sending identities) or send from another address.");
        var res = await Jmap.CallOneAsync("Identity/set", new JsonObject
        {
            ["create"] = new JsonObject { ["i"] = new JsonObject { ["name"] = name, ["email"] = email } },
        }, ct);
        JmapClient.CheckSet(res, $"Creating a sending identity for {email}");
        return res["created"]!["i"]!["id"]!.GetValue<string>();
    }

    /// <summary>Sends an HTML message: inline images (data: URIs in the HTML) become cid: parts, files become
    /// attachments, a plain-text alternative is generated, and the sent copy goes to Sent.</summary>
    public async Task SendAsync(OutgoingMessage m, CancellationToken ct = default)
    {
        if (!Jmap.HasSubmission) throw new JmapException("This Fastmail API token cannot send mail. Create a token with the \"Email submission\" scope.");
        var identity = await IdentityForAsync(m.FromEmail, m.FromName, m.FromIsFastmailMasked, ct);
        var drafts = await RoleIdAsync("drafts", ct);
        var sent = await RoleIdAsync("sent", ct);

        var (html, inline) = HtmlTools.ExtractInlineImages(m.Html);
        // A JSON node can have only one parent, so the alternative part goes into multipart/related only when there are
        // inline images; otherwise it is the body itself.
        JsonObject body = new()
        {
            ["type"] = "multipart/alternative",
            ["subParts"] = new JsonArray(
                new JsonObject { ["partId"] = "text", ["type"] = "text/plain" },
                new JsonObject { ["partId"] = "html", ["type"] = "text/html" }),
        };
        if (inline.Count > 0)
        {
            var related = new JsonArray { body };
            foreach (var img in inline)
            {
                var blob = await Jmap.UploadAsync(img.Data, img.Type, ct);
                related.Add(new JsonObject { ["blobId"] = blob, ["type"] = img.Type, ["cid"] = img.Cid, ["disposition"] = "inline", ["name"] = img.Name });
            }
            body = new JsonObject { ["type"] = "multipart/related", ["subParts"] = related };
        }
        if (m.Attachments.Count > 0)
        {
            var mixed = new JsonArray { body };
            foreach (var f in m.Attachments)
            {
                var blob = await Jmap.UploadAsync(f.Data, f.Type, ct);
                mixed.Add(new JsonObject { ["blobId"] = blob, ["type"] = f.Type, ["name"] = f.Name, ["disposition"] = "attachment" });
            }
            body = new JsonObject { ["type"] = "multipart/mixed", ["subParts"] = mixed };
        }

        JsonArray List(IEnumerable<string> addrs) => new(addrs.Select(a => (JsonNode)new JsonObject { ["email"] = a }).ToArray());
        var email = new JsonObject
        {
            ["mailboxIds"] = new JsonObject { [drafts] = true },
            ["keywords"] = new JsonObject { ["$draft"] = true, ["$seen"] = true },
            ["from"] = new JsonArray(new JsonObject { ["name"] = m.FromName, ["email"] = m.FromEmail }),
            ["to"] = List(m.To),
            ["subject"] = m.Subject,
            ["bodyStructure"] = body,
            ["bodyValues"] = new JsonObject
            {
                ["text"] = new JsonObject { ["value"] = HtmlTools.ToPlainText(html) },
                ["html"] = new JsonObject { ["value"] = html },
            },
        };
        if (m.Cc.Count > 0) email["cc"] = List(m.Cc);
        if (m.InReplyTo != null)
        {
            email["inReplyTo"] = new JsonArray(m.InReplyTo);
            email["references"] = new JsonArray(m.References.Append(m.InReplyTo).Distinct().Select(r => (JsonNode)r).ToArray());
        }

        var res = await Jmap.CallAsync([
            ("Email/set", new JsonObject { ["create"] = new JsonObject { ["draft"] = email } }),
            ("EmailSubmission/set", new JsonObject
            {
                ["create"] = new JsonObject { ["send"] = new JsonObject { ["identityId"] = identity, ["emailId"] = "#draft" } },
                ["onSuccessUpdateEmail"] = new JsonObject
                {
                    ["#send"] = new JsonObject { ["mailboxIds/" + drafts] = null, ["mailboxIds/" + sent] = true, ["keywords/$draft"] = null },
                },
            }),
        ], ct);
        JmapClient.CheckSet(res[0], "Saving the message");
        JmapClient.CheckSet(res[1], "Sending");
    }

    public static List<Addr> Addrs(JsonNode? node) => (node as JsonArray ?? [])
        .Where(a => a?["email"] != null)
        .Select(a => new Addr(a!["name"]?.GetValue<string>() ?? "", a["email"]!.GetValue<string>())).ToList();
}
