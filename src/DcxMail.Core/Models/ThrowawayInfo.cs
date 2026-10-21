namespace DcxMail.Providers;

/// <summary>A throwaway address: a masked address with an expiry, kept in the encrypted settings. (In Core so the
/// settings model has no upward dependency; the throwaway engine is DcxMail.Aliases' Throwaways.)</summary>
public sealed class ThrowawayInfo
{
    public string Email { get; set; } = "";
    public string ProviderKey { get; set; } = "";
    public string AliasId { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Expires { get; set; }
    /// <summary>The last time expiry failed (the service was unreachable…); it is retried on the next check.</summary>
    public string? LastError { get; set; }

    public TimeSpan Left => Expires - DateTimeOffset.Now;
    public bool Expired => Left <= TimeSpan.Zero;
    public string LeftText => Describe(Left);

    /// <summary>"47m 05s", "3h 02m", "2d 4h", or "expired".</summary>
    public static string Describe(TimeSpan left) => left <= TimeSpan.Zero ? "expired"
        : left.TotalMinutes < 1 ? $"{left.Seconds}s"
        : left.TotalHours < 1 ? $"{(int)left.TotalMinutes}m {left.Seconds:00}s"
        : left.TotalDays < 1 ? $"{(int)left.TotalHours}h {left.Minutes:00}m"
        : $"{(int)left.TotalDays}d {left.Hours}h";
}
