using DcxMail.Services;

namespace DcxMail.Addons;

// Add-on records kept in the encrypted settings. They live in Core so the settings model has no upward dependency;
// the add-on engine itself is DcxMail.Addons.

public sealed class AddonCommand
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>global | alias (a masked address is selected) | message (a message is open)</summary>
    public string Context { get; set; } = "global";
}

public sealed class AddonManifest
{
    public int Api { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string Entry { get; set; } = "";
    /// <summary>A theme file in the package. A theme-only add-on has no entry, commands or permissions: nothing runs.</summary>
    public string Theme { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
    public List<string> Permissions { get; set; } = [];
    public List<AddonCommand> Commands { get; set; } = [];
}

/// <summary>An installed add-on, as recorded in the encrypted settings (same model as DarkCipher ServerManagement).</summary>
public sealed class InstalledAddon
{
    public AddonManifest Manifest { get; set; } = new();
    /// <summary>Permissions the user approved (always a subset of the manifest's).</summary>
    public List<string> Granted { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>Install folder (…\addons\&lt;id&gt;\&lt;version&gt;).</summary>
    public string Folder { get; set; } = "";
    /// <summary>Relative path → SHA-256 of every installed file; verified before every run and every theme use.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string PackageSha256 { get; set; } = "";
    /// <summary>"Signed by …" / "NOT signed" for the entry program, at install time.</summary>
    public string Signature { get; set; } = "";
    public DateTime Installed { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public string? DisabledReason { get; set; }
    /// <summary>The installed files are sealed with the store key (data-only add-ons): read them only through
    /// AddonPackages.ReadInstalled.</summary>
    public bool Sealed { get; set; }
    public List<string> RunLog { get; set; } = [];

    public string Id => Manifest.Id;
    public string Title => $"{Manifest.Name} {Manifest.Version}";
    public string Kind => Manifest.Entry.Length > 0 ? "program" : "theme";
    public string StateText => Enabled ? (Manifest.Entry.Length == 0 && AppSettings.Current.ThemeAddon == Id ? "in use" : "enabled") : "disabled" + (DisabledReason != null ? " — " + DisabledReason : "");
}
