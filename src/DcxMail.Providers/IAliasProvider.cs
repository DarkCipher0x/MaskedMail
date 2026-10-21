using DcxMail.Mail;

namespace DcxMail.Providers;

/// <summary>A service the app manages addresses with. What it supports is in <see cref="Info"/>.Features; the optional
/// abilities below are separate interfaces so a service only implements what it really has.</summary>
public interface IAliasProvider
{
    ServiceInfo Info { get; }
    string Key { get; }
    string Name { get; }
    SendMode SendMode { get; }
    Task<List<Alias>> ListAsync(CancellationToken ct = default);
    Task<Alias> CreateAsync(CreateRequest req, CancellationToken ct = default);
    Task SetEnabledAsync(Alias alias, bool enabled, CancellationToken ct = default);
    Task UpdateAsync(Alias alias, string description, string site, CancellationToken ct = default);
    /// <summary>Deletes the address (Fastmail: state "deleted", mail to it bounces). For a remembered-only service it
    /// is removed from the app's list (the service keeps it).</summary>
    Task DeleteAsync(Alias alias, CancellationToken ct = default);
    /// <summary>For <see cref="SendMode.ReverseAlias"/>: the address to send to so <paramref name="recipient"/> receives
    /// the mail from <paramref name="alias"/>.</summary>
    Task<string> ReverseAddressAsync(Alias alias, string recipient, CancellationToken ct = default);
}

/// <summary>The domains or suffixes a new address can use (<see cref="Features.ChooseDomain"/>). First = default.</summary>
public interface IDomainChoice
{
    Task<List<string>> DomainsAsync(CancellationToken ct = default);
}

/// <summary>Where a new address's mail can go (<see cref="Features.ForwardTo"/>). First = default.</summary>
public interface IForwardChoice
{
    Task<List<string>> ForwardTargetsAsync(CancellationToken ct = default);
}

/// <summary>The service holds the mail itself and the app reads it there (<see cref="Features.Inbox"/>).</summary>
public interface IInboxProvider
{
    Task<(List<MessageSummary> Items, int Total)> MessagesAsync(Alias alias, int position, CancellationToken ct = default);
    Task<MessageDetail> MessageAsync(Alias alias, string id, CancellationToken ct = default);
    Task SetSeenAsync(Alias alias, string id, bool seen, CancellationToken ct = default);
    Task DeleteMessageAsync(Alias alias, string id, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(Alias alias, Attachment attachment, CancellationToken ct = default);
}

/// <summary>The service sends mail from its addresses itself (<see cref="SendMode.Service"/>).</summary>
public interface ISendingProvider
{
    Task SendAsync(Alias from, OutgoingMessage message, CancellationToken ct = default);
}

/// <summary>Recent deliveries for an address (<see cref="Features.Activity"/>).</summary>
public interface IActivityProvider
{
    Task<List<ActivityEntry>> ActivityAsync(Alias alias, CancellationToken ct = default);
}

/// <summary>One delivery event: when, what happened (forwarded, blocked, bounced…), who sent it and to where.</summary>
public sealed record ActivityEntry(DateTimeOffset At, string What, string From, string To, string Subject = "");
