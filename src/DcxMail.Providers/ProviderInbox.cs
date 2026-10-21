using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>A disposable inbox held by its service, shown like any folder (see <see cref="IMailSource"/>).</summary>
public sealed class ProviderInbox(IInboxProvider service, IAliasProvider provider, Alias alias) : IMailSource
{
    public string Title => $"{provider.Name} inbox · {alias.Email}";
    public bool CanMarkUnread => true;
    public string DeleteLabel => "Delete";
    public string? Attribution => provider.Info.Attribution;

    public Task<(List<MessageSummary> Items, int Total)> ListAsync(int position, CancellationToken ct = default) => service.MessagesAsync(alias, position, ct);
    public Task<MessageDetail> GetAsync(string id, CancellationToken ct = default) => service.MessageAsync(alias, id, ct);
    public Task SetSeenAsync(string id, bool seen, CancellationToken ct = default) => service.SetSeenAsync(alias, id, seen, ct);
    public Task DeleteAsync(string id, CancellationToken ct = default) => service.DeleteMessageAsync(alias, id, ct);
    public Task<byte[]> DownloadAsync(Attachment attachment, CancellationToken ct = default) => service.DownloadAsync(alias, attachment, ct);
}
