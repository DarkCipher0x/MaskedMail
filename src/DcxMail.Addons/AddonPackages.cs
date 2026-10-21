using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DcxMail.Services;

namespace DcxMail.Addons;

/// <summary>What the approval screen shows for a package before anything is installed.</summary>
public sealed record PackageInfo(string Path, AddonManifest Manifest, string Sha256, long Bytes, int FileCount, string Signature,
    InstalledAddon? Existing, List<string> NewPermissions, string? Refusal);

/// <summary>Package inspection, installation, update, verification and removal (same rules as ServerManagement).</summary>
public static class AddonPackages
{
    public const int MaxFiles = 500;
    public const long MaxUnpacked = 50L << 20;

    public sealed record PermissionInfo(string Id, string Text, string Risk);

    /// <summary>What an add-on may ask for. Nothing here ever exposes an API key, the master password or 2FA secrets.</summary>
    public static readonly PermissionInfo[] Catalog =
    {
        new("aliases.read", "List your masked addresses: address, service, state, note, website and folder — never API keys", "low"),
        new("aliases.write", "Create masked addresses, change their note or enable/disable them (each change shows you the exact action first)", "medium"),
        new("mail.read", "Read message lists and the messages in your Fastmail folders (Inbox, Sent and each masked address's folder)", "HIGH"),
        new("queue.write", "Add emails to your outgoing queue (nothing is sent until you send the queue)", "medium"),
        new("mail.send", "Send email from your addresses: each email shows you the sender, recipients and subject first; you allow or deny it", "HIGH"),
        new("routing.run", "File Inbox mail into masked-address folders now (the same as “sweep now”)", "low"),
        new("filesystem.read", "Read a file YOU pick in a dialog, each time it asks", "medium"),
    };

    public static PermissionInfo? Permission(string id) => Catalog.FirstOrDefault(p => p.Id == id);

    /// <summary>Program files live under the local cache (Medium integrity: the Low-integrity add-on can't change them).</summary>
    public static string AddonsRoot => Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } scratch
        ? Path.GetFullPath(scratch).TrimEnd('\\') + "-addons"
        : Path.Combine(AppInfo.CacheDir, "addons");

    /// <summary>The add-on's only writable folder (Low integrity: under LocalLow).</summary>
    public static string DataFolder(string id)
    {
        var localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        var scope = Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } ? "MaskedMail-selftest" : "MaskedMail";
        return Path.Combine(localLow, "DarkCipher", scope, "addons", id);
    }

    public static List<InstalledAddon> All => AppSettings.Current.Addons;
    public static InstalledAddon? Find(string id) => All.FirstOrDefault(a => a.Id == id);
    public static event Action? Changed;

    private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9.-]{2,63}$");
    private static readonly Regex VersionRx = new(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$");

    /// <summary>Reads and validates a package without installing anything.</summary>
    public static PackageInfo Inspect(string path)
    {
        var empty = new AddonManifest();
        string sha;
        long size = new FileInfo(path).Length;
        if (size > MaxUnpacked) return new(path, empty, "", size, 0, "", null, [], "The package is larger than 50 MB.");
        using (var fs = File.OpenRead(path)) sha = Convert.ToHexString(SHA256.HashData(fs));
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            if (entries.Count == 0 || entries.Count > MaxFiles) return new(path, empty, sha, size, entries.Count, "", null, [], $"The package must hold 1–{MaxFiles} files.");
            if (entries.Sum(e => e.Length) > MaxUnpacked) return new(path, empty, sha, size, entries.Count, "", null, [], "The package unpacks to more than 50 MB.");
            foreach (var e in entries) if (SafeRelative(e.FullName) == null) return new(path, empty, sha, size, entries.Count, "", null, [], $"Unsafe path in the package: {e.FullName}");
            var mf = zip.GetEntry("addon.json") ?? throw new InvalidDataException("addon.json is missing at the root of the package.");
            if (mf.Length > 64 * 1024) throw new InvalidDataException("addon.json is too large.");
            AddonManifest m;
            using (var s = mf.Open()) m = JsonSerializer.Deserialize<AddonManifest>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 }) ?? throw new InvalidDataException("addon.json is empty.");
            var problem = Validate(m, entries.Select(e => SafeRelative(e.FullName)!).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (problem != null) return new(path, m, sha, size, entries.Count, "", null, [], problem);
            if (m.Theme.Length > 0)
            {
                var te = entries.First(e => string.Equals(SafeRelative(e.FullName), SafeRelative(m.Theme), StringComparison.OrdinalIgnoreCase));
                if (te.Length > ThemePack.MaxBytes) return new(path, m, sha, size, entries.Count, "", null, [], "The theme file is larger than 64 KB.");
                using var tr = new StreamReader(te.Open(), System.Text.Encoding.UTF8);
                try { ThemePack.Parse(tr.ReadToEnd()); }
                catch (InvalidDataException ex) { return new(path, m, sha, size, entries.Count, "", null, [], "The theme is not valid: " + ex.Message); }
            }
            var existing = Find(m.Id);
            var newPerms = existing == null ? m.Permissions.ToList() : m.Permissions.Except(existing.Manifest.Permissions).ToList();
            string? refusal = null;
            if (existing != null)
            {
                var vNew = Version.Parse(m.Version); var vOld = Version.Parse(existing.Manifest.Version);
                if (vNew < vOld) refusal = $"This is version {m.Version}, older than the installed {existing.Manifest.Version}. Downgrades are refused — remove the add-on first if you really want the older one.";
                else if (vNew == vOld) refusal = $"Version {m.Version} is already installed.";
            }
            // The entry program's Authenticode state, checked on a temporary copy (shown on the approval screen).
            string signature = m.Entry.Length == 0 ? "theme only (no program)" : "script (no signature check)";
            if (m.Entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "dcxmail-addon-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    zip.GetEntry(m.Entry.Replace('\\', '/'))!.ExtractToFile(tmp);
                    signature = Authenticode.Describe(tmp);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            return new(path, m, sha, size, entries.Count, signature, existing, newPerms, refusal);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return new(path, empty, sha, size, 0, "", null, [], "Not a valid add-on package: " + ex.Message);
        }
    }

    private static string? Validate(AddonManifest m, HashSet<string> files)
    {
        // Every add-on is built for API 2: program add-ons talk to the app only over the encrypted bridge; data-only
        // ones are sealed at rest. The old plain-text API 1 is refused.
        if (m.Api == 1) return "This add-on is made for API 1, the old unencrypted standard. Every add-on must be built for API 2 (program add-ons talk to the app only over the encrypted bridge; themes are sealed at rest); see docs/ADDONS.md.";
        if (m.Api != 2) return $"The add-on needs API {m.Api}; this app provides API 2.";
        if (!IdRx.IsMatch(m.Id ?? "")) return "Invalid id (3–64 lowercase letters, digits, dots, hyphens).";
        if (!VersionRx.IsMatch(m.Version ?? "")) return "Invalid version (major.minor.patch).";
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 60) return "A name of 1–60 characters is required.";
        if (string.IsNullOrWhiteSpace(m.Publisher) || m.Publisher.Length > 80) return "A publisher of 1–80 characters is required.";
        if ((m.Description ?? "").Length > 1000) return "The description is longer than 1000 characters.";
        m.Entry ??= ""; m.Theme ??= ""; m.Permissions ??= []; m.Commands ??= [];
        if (m.Theme.Length > 0 && (SafeRelative(m.Theme) is not { } theme || !files.Contains(theme) || !theme.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return "The theme must be a .json file in the package.";
        if (m.Entry.Length == 0)
        {
            if (m.Theme.Length == 0) return "An add-on needs an entry program or a theme.";
            if (m.Permissions.Count > 0 || m.Commands.Count > 0) return "A theme-only add-on (no entry) can't have permissions or commands.";
            return null;
        }
        if (m.Theme.Length > 0) return "An add-on is either a program or a theme, not both.";
        if (SafeRelative(m.Entry) is not { } entry || !files.Contains(entry)) return "The entry program is not in the package.";
        if (!entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return "The entry must be a .exe or .ps1 file.";
        if (m.TimeoutSeconds is < 5 or > 600) return "timeoutSeconds must be 5–600.";
        if (m.Permissions.Any(p => Permission(p) == null)) return "Unknown permission: " + m.Permissions.First(p => Permission(p) == null);
        m.Permissions = m.Permissions.Distinct().ToList();
        if (m.Commands.Count is 0 or > 30) return "An add-on needs 1–30 commands.";
        foreach (var c in m.Commands)
        {
            if (!Regex.IsMatch(c.Id ?? "", "^[a-z0-9][a-z0-9.-]{0,40}$") || string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 60) return $"Invalid command “{c.Id}”.";
            if (c.Context is not ("global" or "alias" or "message")) return $"Command “{c.Id}” has an unknown context “{c.Context}” (global, alias or message).";
        }
        if (m.Commands.Select(c => c.Id).Distinct().Count() != m.Commands.Count) return "Command ids must be unique.";
        return null;
    }

    /// <summary>A relative path inside the package, or null when it could escape the install folder.</summary>
    public static string? SafeRelative(string p)
    {
        var s = p.Replace('\\', '/');
        if (s.Length == 0 || s.Length > 200 || s.StartsWith('/') || s.Contains(':') || s.Split('/').Any(x => x is "" or "." or "..")) return null;
        if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || s.Split('/').Any(x => x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return s.Replace('/', '\\');
    }

    /// <summary>Installs or updates. <paramref name="grant"/> is what the user approved; on an update the previous grants
    /// are kept and only <paramref name="grant"/> ∩ NEW permissions are added.</summary>
    public static InstalledAddon Install(PackageInfo info, IEnumerable<string> grant)
    {
        if (info.Refusal != null) throw new InvalidOperationException(info.Refusal);
        var m = info.Manifest;
        // The package must not have changed since it was inspected and approved.
        string sha; using (var fs = File.OpenRead(info.Path)) sha = Convert.ToHexString(SHA256.HashData(fs));
        if (sha != info.Sha256) throw new InvalidOperationException("The package file changed after it was checked. Install it again.");
        var folder = Path.Combine(AddonsRoot, m.Id, m.Version);
        var staging = folder + ".installing";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(info.Path);
            foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                var rel = SafeRelative(e.FullName) ?? throw new InvalidDataException("unsafe path " + e.FullName);
                var target = Path.GetFullPath(Path.Combine(staging, rel));
                if (!target.StartsWith(Path.GetFullPath(staging) + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("unsafe path " + e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                byte[] plain;
                using (var es = e.Open()) using (var ms = new MemoryStream()) { es.CopyTo(ms); plain = ms.ToArray(); }
                hashes[rel] = Convert.ToHexString(SHA256.HashData(plain));
                // Data-only add-ons are read only by the app: encrypted at rest like the rest of the store. A program
                // add-on's files must stay runnable by Windows (they are hash-checked before every run instead).
                File.WriteAllBytes(target, m.Entry.Length == 0 ? SecureStore.Encrypt(plain, SealContext(m.Id, m.Version, rel)) : plain);
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(staging, folder);
        }
        catch { try { Directory.Delete(staging, true); } catch { } throw; }

        var existing = Find(m.Id);
        var approved = grant.Intersect(m.Permissions).ToHashSet();
        var a = existing ?? new InstalledAddon();
        var oldFolder = existing?.Folder;
        var granted = existing == null ? approved : existing.Granted.Intersect(m.Permissions).Concat(approved.Intersect(info.NewPermissions)).ToHashSet();
        a.Manifest = m; a.Granted = granted.ToList(); a.Folder = folder; a.Files = hashes; a.PackageSha256 = sha;
        a.Signature = info.Signature; a.Updated = DateTime.Now; a.DisabledReason = null; a.Enabled = true; a.Sealed = m.Entry.Length == 0;
        if (existing == null) All.Add(a);
        AppSettings.Save();
        Directory.CreateDirectory(DataFolder(m.Id));
        if (oldFolder != null && !oldFolder.Equals(folder, StringComparison.OrdinalIgnoreCase)) { try { if (Directory.Exists(oldFolder)) Directory.Delete(oldFolder, true); } catch { } }
        AuditLog.Write(existing == null ? "add-on installed" : "add-on updated", $"{m.Id} {m.Version} · granted: {string.Join(",", a.Granted)} · {info.Signature} · sha256 {sha[..16]}…");
        Changed?.Invoke();
        return a;
    }

    private static string SealContext(string id, string version, string rel) => $"addon|{id}|{version}|{rel.Replace('/', '\\').ToLowerInvariant()}";

    /// <summary>An installed file's content, decrypted when sealed and checked against the hash recorded at install.
    /// Throws <see cref="InvalidDataException"/> when it was changed.</summary>
    public static byte[] ReadInstalled(InstalledAddon a, string rel)
    {
        rel = SafeRelative(rel) ?? throw new InvalidDataException("unsafe path " + rel);
        var key = a.Files.Keys.FirstOrDefault(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException(rel + " is not part of the add-on");
        var bytes = File.ReadAllBytes(Path.Combine(a.Folder, key));
        if (a.Sealed)
            try { bytes = SecureStore.Decrypt(bytes, SealContext(a.Id, a.Manifest.Version, key)); }
            catch (CryptographicException) { throw new InvalidDataException(key + " was modified"); }
        if (Convert.ToHexString(SHA256.HashData(bytes)) != a.Files[key]) throw new InvalidDataException(key + " was modified");
        return bytes;
    }

    public static string ReadInstalledText(InstalledAddon a, string rel) => System.Text.Encoding.UTF8.GetString(ReadInstalled(a, rel)).TrimStart('﻿');

    /// <summary>Every installed file must still match its recorded hash, and nothing may have been added.</summary>
    public static string? VerifyFiles(InstalledAddon a)
    {
        if (!Directory.Exists(a.Folder)) return "its files are missing";
        var root = Path.GetFullPath(a.Folder);
        var present = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => f[(root.Length + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, hash) in a.Files)
        {
            var p = Path.Combine(root, rel);
            if (!File.Exists(p)) return $"{rel} is missing";
            if (a.Sealed) { try { ReadInstalled(a, rel); } catch (InvalidDataException ex) { return ex.Message; } continue; }
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) != hash) return $"{rel} was modified";
        }
        var extra = present.Except(a.Files.Keys, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return extra != null ? $"an unexpected file appeared: {extra}" : null;
    }

    public static void SetEnabled(InstalledAddon a, bool on, string? reason = null)
    {
        a.Enabled = on; a.DisabledReason = on ? null : reason;
        if (!on && AppSettings.Current.ThemeAddon == a.Id) AppSettings.Current.ThemeAddon = null;
        if (on && a.Manifest.Theme.Length > 0) AppSettings.Current.ThemeAddon = a.Id;   // an enabled theme is the theme in use
        AppSettings.Save();
        AuditLog.Write(on ? "add-on enabled" : "add-on disabled", a.Id + (reason != null ? " · " + reason : ""));
        ThemeAddons.Reapply();
        Changed?.Invoke();
    }

    public static void SetPermission(InstalledAddon a, string perm, bool on)
    {
        if (!a.Manifest.Permissions.Contains(perm)) return;
        if (on && !a.Granted.Contains(perm)) a.Granted.Add(perm);
        if (!on) a.Granted.Remove(perm);
        AppSettings.Save();
        AuditLog.Write(on ? "add-on permission granted" : "add-on permission revoked", $"{a.Id}: {perm}");
        Changed?.Invoke();
    }

    public static void Remove(InstalledAddon a, bool deleteData = true)
    {
        try { var dir = Path.Combine(AddonsRoot, a.Id); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        if (deleteData) { try { if (Directory.Exists(DataFolder(a.Id))) Directory.Delete(DataFolder(a.Id), true); } catch { } }
        All.Remove(a);
        if (AppSettings.Current.ThemeAddon == a.Id) AppSettings.Current.ThemeAddon = null;
        AppSettings.Save();
        AuditLog.Write("add-on removed", $"{a.Id} {a.Manifest.Version}");
        ThemeAddons.Reapply();
        Changed?.Invoke();
    }
}
