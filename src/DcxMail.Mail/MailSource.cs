namespace DcxMail.Mail;

/// <summary>Somewhere a list of messages is read from: a Fastmail folder, or a disposable inbox on another service.
/// The main window and the throwaway inbox show any of them the same way.</summary>
public interface IMailSource
{
    /// <summary>Shown above the list ("Inbox/automation/shop@…", "Mail.tm inbox").</summary>
    string Title { get; }
    /// <summary>Messages can be marked unread again.</summary>
    bool CanMarkUnread { get; }
    /// <summary>"Move to Trash" (recoverable) or "Delete" (gone).</summary>
    string DeleteLabel { get; }
    /// <summary>A credit the service asks for where its mail is shown, or null.</summary>
    string? Attribution { get; }
    Task<(List<MessageSummary> Items, int Total)> ListAsync(int position, CancellationToken ct = default);
    Task<MessageDetail> GetAsync(string id, CancellationToken ct = default);
    Task SetSeenAsync(string id, bool seen, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(Attachment attachment, CancellationToken ct = default);
}

/// <summary>A Fastmail folder (an address's routing folder, Inbox, Sent…).</summary>
public sealed class FastmailFolder(MailService mail, string folderId, string title) : IMailSource
{
    public string FolderId => folderId;
    public string Title => title;
    public bool CanMarkUnread => true;
    public string DeleteLabel => "Move to Trash";
    public string? Attribution => null;

    public Task<(List<MessageSummary> Items, int Total)> ListAsync(int position, CancellationToken ct = default) => mail.ListAsync(folderId, position, ct: ct);
    public Task<MessageDetail> GetAsync(string id, CancellationToken ct = default) => mail.GetAsync(id, ct);
    public Task SetSeenAsync(string id, bool seen, CancellationToken ct = default) => mail.SetSeenAsync(id, seen, ct);
    public Task DeleteAsync(string id, CancellationToken ct = default) => mail.MoveToTrashAsync(id, ct);
    public Task<byte[]> DownloadAsync(Attachment a, CancellationToken ct = default) => mail.Jmap.DownloadAsync(a.BlobId, a.Type, a.Name, ct);
}
