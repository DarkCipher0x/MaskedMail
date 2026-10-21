using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DcxMail;

/// <summary>One WebView2 environment (profile in the local cache) shared by the reader and every composer.</summary>
public static class WebHost
{
    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> Environment =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(App.CacheDir, "WebView2"));

    public static async Task InitAsync(WebView2 view, bool scripts)
    {
        await view.EnsureCoreWebView2Async(await Environment);
        var s = view.CoreWebView2.Settings;
        s.IsScriptEnabled = scripts;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = scripts;
        s.AreDefaultScriptDialogsEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        var core = view.CoreWebView2;
        // Mail content may ask for nothing: camera, microphone, location, notifications, clipboard… are all denied.
        core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
        // Nothing is downloaded from inside a message or the editor (attachments are saved through the app).
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    /// <summary>The editor page with a fresh script nonce (only its own script may run) and the image policy: data:
    /// only, unless remote images are allowed in Settings — so quoting a message can't fire its tracking pixels.</summary>
    public static string EditorPage()
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
        var img = Services.AppSettings.Current.LoadRemoteImages ? "data: https:" : "data:";
        return Resource("editor.html").Replace("{{NONCE}}", nonce).Replace("{{IMG}}", img);
    }

    public static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name)
            ?? throw new FileNotFoundException("Missing embedded resource web/" + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Opens a link from a message in the default browser, after showing the real address.</summary>
    public static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
        if (u.Scheme == "mailto")
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow mw) mw.ComposeTo(Uri.UnescapeDataString(u.AbsoluteUri["mailto:".Length..].Split('?')[0]));
            return;
        }
        if (u.Scheme != "https" && u.Scheme != "http") return;
        if (!Dialogs.Confirm("Open link", "This link goes to:\n\n" + u.AbsoluteUri + "\n\nOpen it in your browser?", "Open")) return;
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
