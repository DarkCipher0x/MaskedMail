using System.Security.Cryptography;

namespace DcxMail.Providers;

/// <summary>Address names for services that need the app to pick one: random and unguessable (cryptographic RNG), so
/// nobody can work out your other addresses from one of them.</summary>
public static class RandomName
{
    private const string Letters = "abcdefghijkmnpqrstuvwxyz";          // no l/o: easy to read out
    private const string Chars = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>A letter then 11 more letters or digits, e.g. "k7qmw2xhe9ra" (about 60 bits).</summary>
    public static string New(int length = 12) =>
        Letters[RandomNumberGenerator.GetInt32(Letters.Length)] + RandomNumberGenerator.GetString(Chars, length - 1);

    /// <summary>A strong password for a disposable inbox account (only the app ever uses it).</summary>
    public static string Password() => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789-_", 32);

    /// <summary>Cleans a wanted name to what mail servers accept: a–z, 0–9, dot, dash, underscore; at most 64.</summary>
    public static string Clean(string wanted)
    {
        var s = new string(wanted.Trim().ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_').ToArray()).Trim('.', '-', '_');
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>The wanted name if there is one, else a random one.</summary>
    public static string Or(string wanted) => Clean(wanted) is { Length: > 0 } c ? c : New();
}
