using System.Security.Cryptography;
using System.Text;

namespace DcxMail.Services;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s) — works with Microsoft/Google Authenticator, Authy, 1Password, Bitwarden…</summary>
public static class Totp
{
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
        // RFC 6238's default and the only algorithm every authenticator app accepts. HMAC-SHA1 is not affected by
        // SHA-1 collision attacks; the 160-bit random secret is what protects the code.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        int o = hash[^1] & 0x0F;
        int bin = (hash[o] & 0x7F) << 24 | hash[o + 1] << 16 | hash[o + 2] << 8 | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static string Base32(byte[] data)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = buffer << 8 | b; bits += 8;
            while (bits >= 5) { sb.Append(A[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(A[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static string Uri(byte[] secret, string account) =>
        $"otpauth://totp/{System.Uri.EscapeDataString("DarkCipher MaskedMail:" + account)}?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString("DarkCipher Systems")}&algorithm=SHA1&digits=6&period=30";

    /// <summary>Ten one-time recovery codes like 7KQ2-M9XD-4TPR.</summary>
    public static List<string> RecoveryCodes()
    {
        const string A = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        return Enumerable.Range(0, 10).Select(_ =>
        {
            var b = RandomNumberGenerator.GetBytes(12);
            var s = new string(b.Select(x => A[x % A.Length]).ToArray());
            return $"{s[..4]}-{s[4..8]}-{s[8..12]}";
        }).ToList();
    }
}
