using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DcxMail.Services;

/// <summary>One security-log entry. <see cref="Prev"/> is a hash of the previous line as stored (the chain).</summary>
public sealed record AuditEntry(DateTimeOffset At, string Action, string Detail, string Prev = "");

/// <summary>
/// Security and account events (sign-in, lock, password and 2FA changes, aliases created or deleted, backups, add-ons).
/// Each line is sealed on its own with the data key (AES-256-GCM), so the file reveals nothing and is appended to
/// without rewriting. Each entry also carries a SHA-256 of the line before it, inside its encryption, so deleting,
/// reordering or re-inserting lines shows up when the log is read (only the oldest retained line can't be checked).
/// Never contains API keys, passwords or message content. Kept to the newest 5000 entries.
/// </summary>
public static class AuditLog
{
    private const int MaxEntries = 5000;
    public const string Broken = "⚠ log altered";
    private static readonly object Gate = new();
    private static string? _lastLine;
    public static string Path => System.IO.Path.Combine(SecureStore.DataDir, "audit.log");

    /// <summary>Forgets the remembered last line (the tests delete the log between cases).</summary>
    internal static void ResetForTests() { lock (Gate) _lastLine = null; }

    public static string Hash(string line) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(line)))[..22];

    public static void Write(string action, string detail = "")
    {
        if (!SecureStore.IsUnlocked) return;
        try
        {
            lock (Gate)
            {
                _lastLine ??= File.Exists(Path) ? File.ReadLines(Path).LastOrDefault(l => l.Length > 0) ?? "" : "";
                var json = JsonSerializer.SerializeToUtf8Bytes(new AuditEntry(DateTimeOffset.Now, action, detail, _lastLine.Length > 0 ? Hash(_lastLine) : ""));
                var line = Convert.ToBase64String(SecureStore.Encrypt(json, "audit"));
                File.AppendAllText(Path, line + "\n", Encoding.ASCII);
                _lastLine = line;
                if (new FileInfo(Path).Length > 3_000_000) Trim();
            }
        }
        catch { /* the log must never break the action it records */ }
    }

    /// <summary>All entries, oldest first, with a "⚠ log altered" entry wherever the chain or a line doesn't check out.</summary>
    public static List<AuditEntry> Read()
    {
        var list = new List<AuditEntry>();
        if (!File.Exists(Path) || !SecureStore.IsUnlocked) return list;
        string[] lines;
        lock (Gate) lines = File.ReadAllLines(Path).Where(l => l.Length > 0).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            AuditEntry? e = null;
            try { e = JsonSerializer.Deserialize<AuditEntry>(SecureStore.Decrypt(Convert.FromBase64String(lines[i]), "audit")); } catch { }
            if (e == null) { list.Add(new AuditEntry(DateTimeOffset.MinValue, Broken, "a line was damaged or is not a sealed log entry")); continue; }
            // The first retained line may follow trimmed ones; every later line must name the line before it.
            if (i > 0 && e.Prev != Hash(lines[i - 1]))
                list.Add(new AuditEntry(e.At, Broken, "entries before this one were removed, reordered or inserted"));
            list.Add(e);
        }
        return list;
    }

    private static void Trim()
    {
        var lines = File.ReadAllLines(Path);
        if (lines.Length <= MaxEntries) return;
        var tmp = Path + ".tmp";
        File.WriteAllLines(tmp, lines[^MaxEntries..], Encoding.ASCII);
        File.Move(tmp, Path, overwrite: true);
    }
}
