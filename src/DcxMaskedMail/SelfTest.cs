using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DcxMail.Fakes;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;

namespace DcxMail;

/// <summary>
/// <c>DcxMaskedMail.exe --selftest &lt;report&gt;</c>: the checks that need the real app process: every window drawn
/// off-screen (also the Store screenshots), the capture guard, Settings search, the editor's script policy, the
/// clipboard retry and the process's DLL policy. Uses a scratch data folder and in-process fake servers (no network,
/// no real account, no window on screen; dialogs refuse while it runs). The library logic (store, security, HTML,
/// providers, throwaways, queue, add-ons) is covered by the unit tests: <c>dotnet test DarkCipher.MaskedMail.sln</c>.
/// </summary>
public static class SelfTest
{
    public static bool Running { get; private set; }
    /// <summary>True while windows are drawn off-screen for the renders (the sign-in window may be built then).</summary>
    public static bool RenderingWindows { get; private set; }

    private static readonly List<string> Lines = [];
    private static int _ok, _fail;

    public static async Task<int> RunAsync(string reportPath)
    {
        Running = true;
        if (Environment.GetEnvironmentVariable("DCX_DATA_DIR") is not { Length: > 0 })
            Environment.SetEnvironmentVariable("DCX_DATA_DIR", Path.Combine(Path.GetTempPath(), "dcxmail-selftest-" + Guid.NewGuid().ToString("N")[..8]));
        Lines.Add($"DarkCipher MaskedMail {App.Version} self-test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        Check("store: opens in the scratch folder, settings saved encrypted, folder locked to this user", () =>
        {
            if (!SecureStore.Initialize()) return Fail("store did not open");
            AppSettings.Load();
            AppSettings.Current.FastmailToken = "fmu1-SELFTEST-TOKEN";
            AppSettings.Save();
            var raw = File.ReadAllText(Path.Combine(SecureStore.DataDir, AppSettings.FileName));
            AppSettings.Load();
            return !raw.Contains("SELFTEST") && AppSettings.Current.FastmailToken == "fmu1-SELFTEST-TOKEN" && FolderAcl.IsProtected(SecureStore.DataDir) || Fail("store");
        });
        Check("process: DLLs from remote or Low-integrity-labelled files are refused", () =>
            (ProcessHardening.ImageLoadPolicy() & 0b11) == 0b11 || Fail("image load policy " + ProcessHardening.ImageLoadPolicy()));
        Check("editor: embedded, only its own script may run (fresh nonce per load), no remote images unless allowed", () =>
        {
            var a = WebHost.EditorPage(); var b = WebHost.EditorPage();
            var nonceA = a[(a.IndexOf("nonce-", StringComparison.Ordinal) + 6)..].Split('\'')[0];
            var csp = a[..a.IndexOf("</head>", StringComparison.Ordinal)];
            return WebHost.Resource("editor.html").Contains("contenteditable") && !a.Contains("{{")
                   && csp.Contains("script-src 'nonce-") && !System.Text.RegularExpressions.Regex.IsMatch(csp, "script-src[^;]*unsafe-inline")
                   && a.Contains($"<script nonce=\"{nonceA}\">") && !b.Contains(nonceA) && nonceA.Length >= 20
                   && (AppSettings.Current.LoadRemoteImages || csp.Contains("img-src data:;")) || Fail("editor CSP");
        });
        Check("clipboard: a busy clipboard (CLIPBRD_E_CANT_OPEN) is retried, and a clipboard that stays busy fails quietly", () =>
        {
            var real = ClipboardHelper.Setter;
            try
            {
                int calls = 0; string? got = null;
                ClipboardHelper.Setter = t => { if (++calls < 4) throw new System.Runtime.InteropServices.COMException("OpenClipboard Failed", unchecked((int)0x800401D0)); got = t; };
                var retried = ClipboardHelper.TrySetText("x@example.org") && got == "x@example.org" && calls == 4;
                ClipboardHelper.Setter = _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard Failed", unchecked((int)0x800401D0));
                var gaveUp = !ClipboardHelper.TrySetText("y@example.org");     // must not throw
                return retried && gaveUp && ClipboardHelper.Say(false, "the address").Contains("another program") || Fail("clipboard retry");
            }
            finally { ClipboardHelper.Setter = real; }
        });

        await CheckAsync("renders: every window drawn off-screen (shots/*.png); capture guard; Settings search", () => RenderAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "shots")));

        Lines.Add("");
        Lines.Add($"{_ok} ok, {_fail} failed");
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); File.WriteAllLines(reportPath, Lines); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(SecureStore.DataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Running = false;
        return _fail == 0 ? 0 : 1;
    }

    // ---------- renders (off-screen, never activated, not in the taskbar) ----------

    private static async Task<bool> RenderAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        ThemeManager.Apply(ThemeMode.Dark);
        // An exception in a UI handler fails the report instead of ending the process.
        System.Windows.Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            _fail++;
            Lines.Add("FAIL  unhandled UI exception — " + e.Exception.GetType().Name + ": " + e.Exception.Message);
        };
        var fake = new FakeJmap();
        fake.Masked.Clear();
        string[] sites = ["https://shop.example.com", "https://forum.example.org", "https://news.example.net"];
        string[] notes = ["Example Shop", "Forum sign-up", "Newsletter"];
        for (int i = 0; i < 3; i++)
            fake.Masked.Add(JsonNode.Parse($$"""{"id":"m{{i}}","email":"demo{{i}}.x7k2@fastmail.com","state":"{{(i == 2 ? "disabled" : "enabled")}}","description":"{{notes[i]}}","forDomain":"{{sites[i]}}","createdAt":"2026-10-0{{i + 1}}T10:00:00Z","createdBy":"DarkCipher MaskedMail"}""")!.AsObject());
        fake.Boxes.Add(new FakeBox("auto", "automation", "inbox", null));
        fake.Boxes.Add(new FakeBox("f0", "demo0.x7k2@fastmail.com", "auto", null));
        string[] subjects = ["Your order has shipped", "Welcome to Example Shop", "Receipt #1042"];
        for (int i = 0; i < 3; i++)
            fake.Emails.Add(new FakeEmail("r" + i, "f0", $$"""{"subject":"{{subjects[i]}}","from":[{"name":"Example Shop","email":"orders@shop.example.com"}],"to":[{"email":"demo0.x7k2@fastmail.com"}],"receivedAt":"2026-10-1{{i}}T09:3{{i}}:00Z","preview":"Hi Alex, thanks for your order. Here is what happens next…","keywords":{{(i == 0 ? "{}" : "{\"$seen\":true}")}},"textBody":[{"partId":"1","type":"text/plain"}],"htmlBody":[{"partId":"2","type":"text/html"}],"bodyValues":{"1":{"value":"Hi Alex, thanks for your order."},"2":{"value":"{{DemoOrderHtml}}" } } }"""));
        AppSettings.Current.Folders.Clear();
        AppSettings.Current.Folders["demo0.x7k2@fastmail.com"] = "f0";
        AppSettings.Current.RouteOff.Clear();
        AppSettings.Current.CloseToTray = false;

        var reg = new ProviderRegistry(new AppSettings { FastmailToken = "t" }, fake);
        var main = OffScreen(new MainWindow(), 1600, 960);   // also the Store screenshots: at least 1366 x 768
        await main.InitReaderForTestAsync();
        await main.ReloadAccountsAsync(reg);
        main.AliasList.SelectedItem = reg.Aliases.First(a => a.Routed);
        await Task.Delay(400);
        main.MessageList.SelectedIndex = 0;
        await Task.Delay(3000);   // the message viewer (WebView2) starts and draws the message
        foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
        {
            ThemeManager.Apply(mode);
            await Task.Delay(150);
            await SaveAsync(main, Path.Combine(dir, $"main-{mode.ToString().ToLowerInvariant()}.png"));
        }
        ThemeManager.Apply(ThemeMode.Dark);

        var settings = OffScreen(new Views.SettingsWindow(reg, "Security & privacy"), 1440, 960);
        await Task.Delay(150);
        await SaveAsync(settings, Path.Combine(dir, "settings.png"));
        // Settings must make security easy to find: its own category, and search reaches every security option.
        var secCards = settings.VisibleCardsForTest;
        if (!secCards.Contains("Encryption & master password") || !secCards.Contains("Two-factor sign-in") || !secCards.Contains("App lock")) return Fail("security category: " + string.Join(",", secCards));
        foreach (var (q, want) in new[] { ("2FA", "Two-factor sign-in"), ("master password", "Encryption & master password"), ("backup", "Backups & security log"), ("sandbox", "App lock"), ("token", "Fastmail account"), ("folder", "Routing"), ("cloudflare", "Cloudflare Email Routing"), ("proton", "SimpleLogin"), ("duck.com", "DuckDuckGo"), ("disposable", "Mail.tm"), ("guerrilla", "Services not included, and why") })
        {
            settings.SearchForTest(q);
            if (!settings.VisibleCardsForTest.Contains(want)) return Fail($"search “{q}” missed {want}");
        }
        settings.SearchForTest("");
        settings.RevealCategory("Fastmail");
        await Task.Delay(100);
        await SaveAsync(settings, Path.Combine(dir, "settings-fastmail.png"));
        foreach (var (category, file) in new[] { ("Masked-email services", "settings-masked.png"), ("Your own domains", "settings-domains.png"), ("Disposable inboxes", "settings-disposable.png") })
        {
            settings.RevealCategory(category);
            await Task.Delay(100);
            await SaveAsync(settings, Path.Combine(dir, file));
        }
        settings.Close();

        // Throwaway: a live inbox with a verification mail in it (demo data only), and its Settings page.
        var (tw, _) = await Throwaways.CreateAsync(reg, TimeSpan.FromMinutes(47));
        var twFolder = AppSettings.Current.Folders[tw.Email];
        fake.Emails.Add(new FakeEmail("tw1", twFolder, """
            {"subject":"Confirm your Example Shop account","from":[{"name":"Example Shop","email":"hello@shop.example.com"}],"to":[{"email":"@@TW@@"}],
             "receivedAt":"2026-10-20T09:41:00Z","sentAt":"2026-10-20T09:41:00Z","preview":"Your verification code is 482913","keywords":{},
             "textBody":[{"partId":"1","type":"text/plain"}],"htmlBody":[{"partId":"2","type":"text/html"}],
             "bodyValues":{"1":{"value":"Hi Alex,\n\nYour verification code is 482913.\nOr confirm here: https://shop.example.com/account/verify?token=demo\n\nThanks,\nExample Shop"},
                           "2":{"value":"<p>Your verification code is <b>482913</b>.</p><p><a href=\"https://shop.example.com/account/verify?token=demo\">Verify my email</a></p>"}}}
            """.Replace("@@TW@@", tw.Email)));
        var twWin = OffScreen(new Views.ThrowawayWindow(reg, tw.Email), 760, 640);
        await Task.Delay(900);
        await SaveAsync(twWin, Path.Combine(dir, "throwaway.png"));
        twWin.Close();
        var twSettings = OffScreen(new Views.SettingsWindow(reg, "Throwaway addresses"), 1040, 900);
        await Task.Delay(150);
        await SaveAsync(twSettings, Path.Combine(dir, "settings-throwaway.png"));
        twSettings.Close();
        // The main window with the throwaway selected (countdown chip, card bar, its inbox in the middle): a Store screenshot.
        ThemeManager.Apply(ThemeMode.Dark);
        await Task.Delay(150);
        main.AliasList.SelectedItem = reg.Aliases.First(x => x.Email == tw.Email);
        await Task.Delay(500);
        main.MessageList.SelectedIndex = 0;
        await Task.Delay(1500);
        await SaveAsync(main, Path.Combine(dir, "main-throwaway.png"));
        AppSettings.Current.Throwaways.Clear();
        RenderingWindows = true;
        try
        {
            var unlock = OffScreen(new Views.UnlockWindow(needPassword: true, needCode: true), 440, 520);
            await SaveAsync(unlock, Path.Combine(dir, "sign-in.png"));
            // The sign-in screen must never appear in screenshots or streams; ordinary windows only when asked.
            if (!CaptureGuard.IsExcluded(unlock)) return Fail("the sign-in window is visible to screen capture");
            if (CaptureGuard.IsExcluded(main) != AppSettings.Current.HideFromCapture) return Fail("main window capture setting not applied");
            unlock.Close();
        }
        finally { RenderingWindows = false; }
        Outbox.Items.Clear();
        Outbox.Items.Add(new QueuedEmail { FromEmail = "demo0.x7k2@fastmail.com", AliasProvider = "fastmail", To = ["alex@example.org"], Subject = "Order question", Copies = 1 });
        Outbox.Items.Add(new QueuedEmail { FromEmail = "demo0.x7k2@fastmail.com", AliasProvider = "fastmail", To = ["sam@example.org"], Subject = "Order question", Copies = 3 });
        Outbox.Items.Add(new QueuedEmail { FromEmail = "demo1.x7k2@fastmail.com", AliasProvider = "fastmail", To = ["anna@example.net"], Cc = ["alex@example.org"], Subject = "Forum invite", Enabled = false });
        var outbox = OffScreen(new Views.OutboxWindow(reg), 1000, 520);
        await Task.Delay(150);
        await SaveAsync(outbox, Path.Combine(dir, "queue.png")); outbox.Close();
        Outbox.Items.Clear();
        var create = OffScreen(new Views.NewAliasWindow(reg), 560, 660);
        await SaveAsync(create, Path.Combine(dir, "new-alias.png")); create.Close();
        var compose = OffScreen(new Views.ComposeWindow(reg, Views.ComposeWindow.Mode.New, reg.Aliases[0], null), 900, 640);
        await Task.Delay(2000);   // the editor (WebView2) loads
        await SaveAsync(compose, Path.Combine(dir, "compose.png")); compose.Close();
        main.Close();
        return Directory.GetFiles(dir, "*.png").Length == 14 || Fail("missing renders: " + Directory.GetFiles(dir, "*.png").Length);
    }

    private static T OffScreen<T>(T w, double width, double height) where T : System.Windows.Window
    {
        w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        w.Owner = null;
        w.Left = -30000; w.Top = -30000;
        w.SizeToContent = System.Windows.SizeToContent.Manual;
        w.Width = width; w.Height = height;
        w.ShowActivated = false; w.ShowInTaskbar = false;
        w.Show();
        return w;
    }

    private static async Task SaveAsync(System.Windows.Window w, string path)
    {
        var root = (System.Windows.FrameworkElement)w.Content;
        root.UpdateLayout();
        // The root is drawn at its margin offset, so the picture includes the margin on both sides (or the right edge is cut).
        var m = root.Margin;
        double width = root.ActualWidth + m.Left + m.Right, height = root.ActualHeight + m.Top + m.Bottom;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(w.Background, null, new System.Windows.Rect(0, 0, width, height));
        bmp.Render(bg);
        bmp.Render(root);
        // WebView2 draws in a window of its own, which RenderTargetBitmap can't see: paste in its own capture instead.
        foreach (var view in Descendants<Microsoft.Web.WebView2.Wpf.WebView2>(root))
        {
            if (view.CoreWebView2 is null || view.ActualWidth < 1 || view.ActualHeight < 1) continue;
            using var shot = new MemoryStream();
            await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, shot);
            shot.Position = 0;
            var img = System.Windows.Media.Imaging.BitmapFrame.Create(shot, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var at = view.TransformToAncestor(root).Transform(new System.Windows.Point(m.Left, m.Top));
            var layer = new System.Windows.Media.DrawingVisual();
            using (var dc = layer.RenderOpen()) dc.DrawImage(img, new System.Windows.Rect(at.X, at.Y, view.ActualWidth, view.ActualHeight));
            bmp.Render(layer);
        }
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    /// <summary>The demo order e-mail shown in the reader renders (made-up shop and customer; JSON-escaped).</summary>
    private const string DemoOrderHtml =
        "<div style=\\\"font-family:Segoe UI,Arial,sans-serif;max-width:560px;color:#1f2937\\\">" +
        "<h2 style=\\\"color:#0f766e;margin:0 0 12px\\\">Your order is on its way</h2>" +
        "<p>Hi Alex,</p><p>Good news: order <b>#1042</b> left our warehouse today and should arrive on <b>Tuesday 14 October</b>.</p>" +
        "<table style=\\\"border-collapse:collapse;width:100%;margin:12px 0\\\">" +
        "<tr><td style=\\\"padding:6px 0;border-bottom:1px solid #e5e7eb\\\">Desk lamp, matte black</td><td style=\\\"text-align:right;border-bottom:1px solid #e5e7eb\\\">£34.00</td></tr>" +
        "<tr><td style=\\\"padding:6px 0;border-bottom:1px solid #e5e7eb\\\">LED bulb, warm white (2)</td><td style=\\\"text-align:right;border-bottom:1px solid #e5e7eb\\\">£9.00</td></tr>" +
        "<tr><td style=\\\"padding:6px 0\\\"><b>Total</b></td><td style=\\\"text-align:right\\\"><b>£43.00</b></td></tr></table>" +
        "<p>You can track the parcel from your account at any time.</p><p>Thanks for shopping with us,<br>Example Shop</p></div>";

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    // ---------- plumbing ----------

    private static string? _why;
    private static bool Fail(string why) { _why = why; return false; }

    private static void Check(string name, Func<bool> test)
    {
        _why = null;
        try { Record(name, test(), null); }
        catch (Exception ex) { Record(name, false, ex); }
    }

    private static async Task CheckAsync(string name, Func<Task<bool>> test)
    {
        _why = null;
        try { Record(name, await test(), null); }
        catch (Exception ex) { Record(name, false, ex); }
    }

    private static void Record(string name, bool ok, Exception? ex)
    {
        if (ok) { _ok++; Lines.Add("ok    " + name); }
        else { _fail++; Lines.Add("FAIL  " + name + (_why != null ? "  — " + _why : "") + (ex != null ? "  — " + ex.GetType().Name + ": " + ex.Message : "")); }
    }
}
