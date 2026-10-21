using System.Text.Json.Serialization;

namespace DcxMail.Services;

/// <summary>All settings, including every API key, live in settings.dcx (encrypted, see <see cref="SecureStore"/>).</summary>
public sealed class AppSettings
{
    public const string FileName = "settings.dcx";

    // Fastmail (JMAP): one API token covers Masked Email, mail and sending.
    public string FastmailToken { get; set; } = "";
    public string FastmailSessionUrl { get; set; } = "https://api.fastmail.com/jmap/session";

    // Other masked-email, own-domain and disposable-inbox services: service key → field id → value (API keys
    // included; like everything here, encrypted in settings.dcx). Each service declares its fields itself.
    public Dictionary<string, Dictionary<string, string>> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Addresses the app has to remember itself: services that can't list them (DuckDuckGo) and disposable
    /// inboxes with their sign-in secrets.</summary>
    public List<Providers.LocalAlias> LocalAliases { get; set; } = [];

    // 0.1–0.2 kept three services in their own fields. Read once and moved into Services by Normalize(); never written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SimpleLoginKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SimpleLoginUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AddyKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AddyUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RelayKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RelayUrl { get; set; }

    // Routing: every alias gets a folder <parent>/<alias address>; matching Inbox mail is moved there.
    public string RouteParent { get; set; } = "Inbox/automation";
    public bool RoutingEnabled { get; set; } = true;
    public int SweepMinutes { get; set; } = 2;
    /// <summary>Alias address (lower case) → Fastmail mailbox id of its folder.</summary>
    public Dictionary<string, string> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Addresses the user turned routing off for.</summary>
    public HashSet<string> RouteOff { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Security (same options as DarkCipher ServerManagement)
    /// <summary>Lock after this many minutes without keyboard or mouse input; 0 = never.</summary>
    public int AutoLockMinutes { get; set; }
    public bool LockOnWindowsLock { get; set; } = true;
    /// <summary>Master password required at every start, auto-lock on, viewer cache wiped on exit.</summary>
    public bool SandboxMode { get; set; }
    /// <summary>Hide every MaskedMail window from screenshots, recording and streaming (sign-in and security dialogs always are).</summary>
    public bool HideFromCapture { get; set; }

    // Throwaway addresses: masked addresses with an expiry. Address (lower case) → its record.
    public Dictionary<string, Providers.ThrowawayInfo> Throwaways { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Service for new throwaways ("" = the first configured, Fastmail when set).</summary>
    public string ThrowawayProvider { get; set; } = "";
    public int ThrowawayMinutes { get; set; } = 60;
    /// <summary>On expiry: true = delete the address (mail to it bounces), false = only disable it.</summary>
    public bool ThrowawayDelete { get; set; } = true;
    /// <summary>On expiry: move its mail to Trash and remove its folder.</summary>
    public bool ThrowawayCleanMail { get; set; } = true;
    /// <summary>How often the throwaway inbox window checks for new mail.</summary>
    public int ThrowawayPollSeconds { get; set; } = 10;

    // Outgoing queue
    /// <summary>Pause between queued emails (and between copies), so a burst doesn't trip sending limits.</summary>
    public int QueueDelaySeconds { get; set; } = 3;
    public bool QueueStopOnError { get; set; }

    // Add-ons (same model as ServerManagement): manifests, grants, file hashes and run logs live here, encrypted.
    public List<Addons.InstalledAddon> Addons { get; set; } = [];
    /// <summary>The theme add-on in use, or null for the app's own colours.</summary>
    public string? ThemeAddon { get; set; }

    // Behaviour
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool LoadRemoteImages { get; set; }
    public string Theme { get; set; } = "System";
    public string DefaultFont { get; set; } = "Segoe UI";
    public int DefaultFontSize { get; set; } = 3;          // HTML size 1-7 (3 = normal)
    public string SenderName { get; set; } = "";

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        // A file that cannot be decrypted is an error, never "no settings": saving defaults would overwrite it.
        Current = SecureStore.Load<AppSettings>(FileName) ?? new AppSettings();
        Current.Normalize();
    }

    public static void Save() => SecureStore.Save(FileName, Current);

    /// <summary>Restore from a backup: replaces every setting and saves.</summary>
    public static void Replace(AppSettings restored)
    {
        Current = restored;
        Current.Normalize();
        Save();
    }

    /// <summary>After reading from JSON: puts back the case-insensitive comparers JSON drops, and moves the 0.1–0.2
    /// service fields into <see cref="Services"/>.</summary>
    public void Normalize()
    {
        Folders = new(Folders ?? [], StringComparer.OrdinalIgnoreCase);
        RouteOff = new(RouteOff ?? [], StringComparer.OrdinalIgnoreCase);
        Throwaways = new(Throwaways ?? [], StringComparer.OrdinalIgnoreCase);
        Services = new((Services ?? []).ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        LocalAliases ??= [];
        MoveLegacy("simplelogin", SimpleLoginKey, SimpleLoginUrl);
        MoveLegacy("addy", AddyKey, AddyUrl);
        MoveLegacy("relay", RelayKey, RelayUrl);
        SimpleLoginKey = SimpleLoginUrl = AddyKey = AddyUrl = RelayKey = RelayUrl = null;
    }

    private void MoveLegacy(string service, string? key, string? url)
    {
        if (string.IsNullOrWhiteSpace(key) || Services.ContainsKey(service)) return;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["key"] = key.Trim() };
        if (!string.IsNullOrWhiteSpace(url)) fields["url"] = url.Trim();
        Services[service] = fields;
    }

    /// <summary>One service's saved fields (empty when it isn't set up).</summary>
    public IReadOnlyDictionary<string, string> ServiceFields(string service) =>
        Services.TryGetValue(service, out var f) ? f : new Dictionary<string, string>();
}
