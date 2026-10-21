using System.IO;
using System.Text;
using System.Text.Json;

namespace DcxMail.Services;

public sealed record ErrorEntry(DateTimeOffset At, string Where, string Type, string Message, string Stack);

/// <summary>
/// Unexpected errors from anywhere in the app (UI thread, background threads, unobserved tasks), so nothing fails
/// silently and a problem can be diagnosed afterwards. Like the security log, every line is sealed on its own with the
/// data key (AES-256-GCM). Stack traces carry no local paths (builds map source paths to "/_/"). Message text is the
/// exception's own; the app never puts keys, passwords or mail content in exception messages. Newest 1000 kept.
/// </summary>
public static class ErrorLog
{
    private const int MaxEntries = 1000;
    private static readonly object Gate = new();
    public static string Path => System.IO.Path.Combine(SecureStore.DataDir, "errors.log");

    public static void Write(string where, Exception ex)
    {
        if (!SecureStore.IsUnlocked) return;
        try
        {
            var e = new ErrorEntry(DateTimeOffset.Now, where, ex.GetType().FullName ?? ex.GetType().Name, Trim(ex.Message, 2000), Trim(ex.ToString(), 8000));
            var line = Convert.ToBase64String(SecureStore.Encrypt(JsonSerializer.SerializeToUtf8Bytes(e), "errors"));
            lock (Gate)
            {
                File.AppendAllText(Path, line + "\n", Encoding.ASCII);
                if (new FileInfo(Path).Length > 4_000_000)
                {
                    var lines = File.ReadAllLines(Path);
                    if (lines.Length > MaxEntries) File.WriteAllLines(Path, lines[^MaxEntries..], Encoding.ASCII);
                }
            }
        }
        catch (Exception ioOrCrypto) when (ioOrCrypto is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // The error log must never itself break the app.
        }
    }

    public static List<ErrorEntry> Read()
    {
        var list = new List<ErrorEntry>();
        if (!File.Exists(Path) || !SecureStore.IsUnlocked) return list;
        string[] lines;
        lock (Gate) lines = File.ReadAllLines(Path);
        foreach (var l in lines.Where(l => l.Length > 0))
        {
            try { if (JsonSerializer.Deserialize<ErrorEntry>(SecureStore.Decrypt(Convert.FromBase64String(l), "errors")) is { } e) list.Add(e); }
            catch (Exception ex) when (ex is FormatException or JsonException or System.Security.Cryptography.CryptographicException) { /* a damaged line is skipped */ }
        }
        return list;
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
