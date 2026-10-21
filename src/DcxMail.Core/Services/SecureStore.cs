using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcxMail.Services;

/// <summary>
/// Everything the app writes to disk is encrypted with AES-256-GCM under one random 256-bit data key (same design as
/// DarkCipher ServerManagement). The data key itself is never stored in the clear. It is wrapped by:
///   • Windows DPAPI (current user) — unlocks automatically when you sign in to Windows, and/or
///   • a master password (PBKDF2-SHA256, 600k iterations) — when "require on start" is on, the DPAPI copy is deleted,
///     so the files are useless without the password even to an administrator or from a copy of the disk.
/// An authenticator (TOTP) second factor can gate opening and unlocking the app. Each file is bound to its own name
/// (associated data), so encrypted files cannot be swapped for one another.
/// </summary>
public static class SecureStore
{
    private const int Pbkdf2Iterations = 600_000;
    private static readonly byte[] Magic = "DCX1"u8.ToArray();
    private static readonly byte[] Entropy = "DarkCipher.MaskedMail.v1"u8.ToArray();
    private static byte[]? _key;
    private static Keyring _ring = new();
    private static readonly object Gate = new();

    /// <summary>%APPDATA%\DarkCipher\MaskedMail, or DCX_DATA_DIR (the self-test uses it so tests never touch real data).</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkCipher", "MaskedMail");

    private static string RingPath => Path.Combine(DataDir, "keyring.json");
    private static string LegacyKeyPath => Path.Combine(DataDir, "key.dpapi");   // 0.1.0–0.1.2

    public static bool IsUnlocked => _key != null;
    public static bool HasPassword => _ring.Password != null;
    /// <summary>True when only the master password can open the store (no DPAPI copy of the key).</summary>
    public static bool PasswordRequiredOnStart => _ring.Password != null && _ring.Dpapi == null;

    /// <summary>Loads (or creates) the key ring. Returns true if the store is open; false means the master password is needed.</summary>
    public static bool Initialize()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DataDir);
            FolderAcl.Protect(DataDir);
            if (!File.Exists(RingPath))
            {
                // Upgrade from 0.1.0–0.1.2: the DPAPI-wrapped key moves into the key ring unchanged, so files stay readable.
                if (File.Exists(LegacyKeyPath))
                {
                    var wrapped = File.ReadAllBytes(LegacyKeyPath);
                    _key = ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.CurrentUser);
                    _ring = new Keyring { Dpapi = Convert.ToBase64String(wrapped) };
                    SaveRing();
                    File.Delete(LegacyKeyPath);
                    return true;
                }
                _key = RandomNumberGenerator.GetBytes(32);
                _ring = new Keyring { Dpapi = Convert.ToBase64String(ProtectedData.Protect(_key, Entropy, DataProtectionScope.CurrentUser)) };
                SaveRing();
                return true;
            }
            _ring = JsonSerializer.Deserialize<Keyring>(File.ReadAllText(RingPath)) ?? new Keyring();
            if (_ring.Dpapi != null)
            {
                try { _key = ProtectedData.Unprotect(Convert.FromBase64String(_ring.Dpapi), Entropy, DataProtectionScope.CurrentUser); return true; }
                catch (CryptographicException) { if (_ring.Password == null) throw new InvalidOperationException("The data key belongs to another Windows account and no master password is set."); }
            }
            return false;
        }
    }

    public static bool Unlock(string password)
    {
        if (_ring.Password is not { } pw) return false;
        var kek = Derive(password, Convert.FromBase64String(pw.Salt), pw.Iterations);
        try { _key = Open(kek, Convert.FromBase64String(pw.Blob), "keyring"); return true; }
        catch (CryptographicException) { return false; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public static bool VerifyPassword(string password)
    {
        if (_ring.Password is not { } pw) return false;
        var kek = Derive(password, Convert.FromBase64String(pw.Salt), pw.Iterations);
        try { CryptographicOperations.ZeroMemory(Open(kek, Convert.FromBase64String(pw.Blob), "keyring")); return true; }
        catch (CryptographicException) { return false; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Sets or changes the master password. requireOnStart removes the DPAPI copy of the key.</summary>
    public static void SetPassword(string password, bool requireOnStart)
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        if (PasswordProblem(password) is { } problem) throw new ArgumentException(problem);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Derive(password, salt, Pbkdf2Iterations);
        try
        {
            _ring.Password = new PasswordWrap { Salt = Convert.ToBase64String(salt), Iterations = Pbkdf2Iterations, Blob = Convert.ToBase64String(Seal(kek, key, "keyring")) };
            _ring.Dpapi = requireOnStart ? null : Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
            SaveRing();
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static readonly string[] Common =
    {
        "password", "passw0rd", "qwerty", "letmein", "welcome", "iloveyou", "admin", "monkey", "dragon", "football",
        "baseball", "master", "sunshine", "princess", "trustno1", "123456", "abc123", "darkcipher", "maskedmail", "fastmail",
    };

    /// <summary>Why a master or backup password is too weak, or null: 12+ characters, not one repeated character or a
    /// common word padded with digits, and mixed (2+ kinds of character) unless it is a long passphrase (16+).</summary>
    public static string? PasswordProblem(string pw)
    {
        if (pw.Length < 12) return "Use at least 12 characters.";
        if (pw.Distinct().Count() < 4) return "Use more different characters.";
        var core = new string(pw.ToLowerInvariant().Where(char.IsLetter).ToArray());
        if (Common.Any(c => core == c || core == c + c || pw.ToLowerInvariant().Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '!', '.') == c))
            return "That's a common password with something added. Pick something nobody would guess.";
        int kinds = new[] { pw.Any(char.IsLower), pw.Any(char.IsUpper), pw.Any(char.IsDigit), pw.Any(c => !char.IsLetterOrDigit(c)) }.Count(x => x);
        if (pw.Length < 16 && kinds < 2) return "Mix letters with digits or symbols, or use a passphrase of 16+ characters.";
        return null;
    }

    // ---- sign-in throttling (kept across restarts) -------------------------------------------------------------
    // Each wrong password or code adds to a counter in the key ring; the next attempt must wait 2^(n-3) s (from the 4th
    // failure, at most 5 minutes). Restarting the app doesn't reset it; a correct sign-in does.

    public static int FailedAttempts => _ring.FailedAttempts;

    public static TimeSpan RetryWait()
    {
        if (_ring.FailedAttempts < 3 || _ring.LastFailure is not { } last) return TimeSpan.Zero;
        var wait = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, _ring.FailedAttempts - 3)));
        var left = last + wait - DateTimeOffset.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    public static void RecordFailure()
    {
        lock (Gate) { _ring.FailedAttempts++; _ring.LastFailure = DateTimeOffset.UtcNow; SaveRing(); }
    }

    public static void RecordSuccess()
    {
        if (_ring.FailedAttempts == 0) return;
        lock (Gate) { _ring.FailedAttempts = 0; _ring.LastFailure = null; SaveRing(); }
    }

    public static void RemovePassword()
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        _ring.Dpapi = Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        _ring.Password = null;
        SaveRing();
    }

    // ---- second factor (TOTP, RFC 6238) ------------------------------------------------------------------------
    // The authenticator secret is sealed with the data key and recovery codes are stored only as salted hashes.
    // It gates opening the app (and the lock screen); the file encryption itself rests on the key / master password.

    public static bool HasTotp => _ring.Totp != null;
    public static int RecoveryCodesLeft => _ring.Recovery?.Count ?? 0;

    public static void SetTotp(byte[] secret, IEnumerable<string> recoveryCodes)
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        _ring.Totp = Convert.ToBase64String(Seal(key, secret, "totp"));
        _ring.Recovery = recoveryCodes.Select(HashRecovery).ToList();
        _ring.TotpLastStep = 0;
        SaveRing();
    }

    public static void RemoveTotp()
    {
        _ring.Totp = null; _ring.Recovery = null; _ring.TotpLastStep = 0;
        SaveRing();
    }

    /// <summary>Checks a 6-digit code (±1 step, no replay) or a one-time recovery code (consumed).</summary>
    public static bool VerifySecondFactor(string input) => VerifySecondFactorAt(input, DateTimeOffset.UtcNow);

    internal static bool VerifySecondFactorAt(string input, DateTimeOffset now)
    {
        if (_ring.Totp is not { } sealedSecret || _key is not { } key) return false;
        var code = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (code.Length == 6 && code.All(char.IsDigit))
        {
            var secret = Open(key, Convert.FromBase64String(sealedSecret), "totp");
            try
            {
                long step = now.ToUnixTimeSeconds() / 30;
                for (long s = step - 1; s <= step + 1; s++)
                    if (s > _ring.TotpLastStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Totp.Code(secret, s)), Encoding.ASCII.GetBytes(code)))
                    {
                        _ring.TotpLastStep = s;
                        SaveRing();
                        return true;
                    }
                return false;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        var h = HashRecovery(code);
        if (_ring.Recovery?.Contains(h) == true) { _ring.Recovery.Remove(h); SaveRing(); return true; }
        return false;
    }

    private static string HashRecovery(string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("dcx-recovery|" + new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())));

    /// <summary>Forgets the data key from memory (exit from the lock screen; self-test).</summary>
    public static void Forget()
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    // ---- self-test hooks (never used by the app) ----------------------------------------------------------------

    /// <summary>Drops the in-memory key and ring so the next <see cref="Initialize"/> behaves like a fresh start.</summary>
    internal static void ResetForTests() { Forget(); _ring = new Keyring(); }

    /// <summary>Recreates the 0.1.0–0.1.2 layout (key.dpapi, no keyring.json) from the open key, to test the upgrade.</summary>
    internal static void WriteLegacyLayoutForTests()
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        File.WriteAllBytes(LegacyKeyPath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        File.Delete(RingPath);
    }

    // ---- files ------------------------------------------------------------------------------------------------

    public static T? Load<T>(string name) where T : class
    {
        var path = Path.Combine(DataDir, name);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(Decrypt(File.ReadAllBytes(path), name));
    }

    public static void Save<T>(string name, T value)
    {
        var bytes = Encrypt(JsonSerializer.SerializeToUtf8Bytes(value), name);
        lock (Gate) WriteAtomic(Path.Combine(DataDir, name), bytes);
    }

    public static byte[] Encrypt(byte[] plain, string context) => Seal(_key ?? throw new InvalidOperationException("Store is locked."), plain, context);
    public static byte[] Decrypt(byte[] blob, string context) => Open(_key ?? throw new InvalidOperationException("Store is locked."), blob, context);

    // ---- primitives -------------------------------------------------------------------------------------------

    /// <summary>Layout: "DCX1" | nonce (12) | tag (16) | ciphertext.</summary>
    public static byte[] Seal(byte[] key, byte[] plain, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ct = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, ct, tag, Encoding.UTF8.GetBytes(context));
        return [.. Magic, .. nonce, .. tag, .. ct];
    }

    public static byte[] Open(byte[] key, byte[] blob, string context)
    {
        if (blob.Length < 32 || !blob.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("Not a DarkCipher encrypted file.");
        var plain = new byte[blob.Length - 32];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(blob.AsSpan(4, 12), blob.AsSpan(32), blob.AsSpan(16, 16), plain, Encoding.UTF8.GetBytes(context));
        return plain;
    }

    public static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static void SaveRing() =>
        WriteAtomic(RingPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_ring, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })));

    private static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class Keyring
    {
        public int Version { get; set; } = 1;
        public string? Dpapi { get; set; }
        public PasswordWrap? Password { get; set; }
        public string? Totp { get; set; }
        public List<string>? Recovery { get; set; }
        public long TotpLastStep { get; set; }
        public int FailedAttempts { get; set; }
        public DateTimeOffset? LastFailure { get; set; }
    }

    private sealed class PasswordWrap
    {
        public string Salt { get; set; } = "";
        public int Iterations { get; set; }
        public string Blob { get; set; } = "";
    }
}
