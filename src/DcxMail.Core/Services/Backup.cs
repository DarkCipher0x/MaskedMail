using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DcxMail.Services;

/// <summary>
/// Encrypted backup (.dcxbackup): your settings, every API key and the folder map, sealed with a password of its own
/// (PBKDF2-SHA256 600k + AES-256-GCM). It does not depend on this PC or Windows account, so it restores anywhere.
/// </summary>
public static class Backup
{
    private const string Format = "DarkCipher MaskedMail backup";
    private const int Iterations = 600_000;

    public static void Export(string path, string password)
    {
        var payload = new JsonObject
        {
            ["created"] = DateTimeOffset.Now.ToString("o"),
            ["app"] = AppInfo.Version,
            ["settings"] = JsonSerializer.SerializeToNode(AppSettings.Current),
        };
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = SecureStore.Derive(password, salt, Iterations);
        var blob = SecureStore.Seal(kek, Encoding.UTF8.GetBytes(payload.ToJsonString()), Format);
        CryptographicOperations.ZeroMemory(kek);
        var file = new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["kdf"] = "PBKDF2-SHA256", ["iterations"] = Iterations,
            ["salt"] = Convert.ToBase64String(salt), ["data"] = Convert.ToBase64String(blob),
        };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Opens a backup; throws <see cref="CryptographicException"/> for a wrong password or a damaged file.</summary>
    public static AppSettings Open(string path, string password)
    {
        var file = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty file.");
        if (file["format"]?.GetValue<string>() != Format) throw new InvalidDataException("This is not a DarkCipher MaskedMail backup.");
        var kek = SecureStore.Derive(password, Convert.FromBase64String(file["salt"]!.GetValue<string>()), file["iterations"]!.GetValue<int>());
        try
        {
            var plain = SecureStore.Open(kek, Convert.FromBase64String(file["data"]!.GetValue<string>()), Format);
            var payload = JsonNode.Parse(plain)!;
            var s = payload["settings"].Deserialize<AppSettings>() ?? throw new InvalidDataException("The backup has no settings.");
            // JSON drops the dictionaries' comparer: addresses must match regardless of case.
            s.Normalize();
            return s;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }
}
