using System.IO;
using System.Reflection;

namespace DcxMail.Services;

/// <summary>Product facts every layer needs (name, version, cache folder), without depending on the WPF app.</summary>
public static class AppInfo
{
    public const string ProductName = "DarkCipher MaskedMail";

    /// <summary>The release version (Directory.Build.props: one version for every project).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Local, non-roaming cache: the viewer's browser profile and installed add-on files.</summary>
    public static string CacheDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarkCipher", "MaskedMail");
}
