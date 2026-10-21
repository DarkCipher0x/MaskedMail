using System.IO;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;

namespace DcxMail.Addons;

/// <summary>Where a command was run from: a selected masked address, an open message, or nowhere in particular.</summary>
public sealed record AddonContext(Alias? Alias = null, MessageDetail? Message = null);

public sealed record AddonRunResult(bool Ok, string Text, bool Show, List<string> Log, string Status);

/// <summary>
/// Runs one add-on command, exactly like DarkCipher ServerManagement (ADR-012 / ADR-035): verifies the installed files,
/// starts the program at Low integrity in a Job with a locked-down process (<see cref="SandboxProcess"/>), runs the
/// encrypted handshake (<see cref="AddonBridge"/>: ECDH P-256 → HKDF → AES-256-GCM per direction, counter nonces), sends
/// "invoke", answers each method call after checking the granted permission (changes and sends are shown to you first),
/// and collects "done". Crashes, hangs, floods, plain-text lines and protocol abuse end the run — never the app.
/// </summary>
public static class AddonHost
{
    public const int MaxMessages = 2000;
    public const long MaxBytes = 1L << 20;
    public const int MaxLine = 256 * 1024;

    /// <summary>The app's registry of services (set by the main window).</summary>
    public static Func<ProviderRegistry?> Registry { get; set; } = () => null;
    /// <summary>Approval of one change or send (tests replace it): add-on, title, exact details → allowed?</summary>
    /// <remarks>
    /// The UI seams below are plugged in by the app at start-up (App.ConnectAddonUi). Their defaults are the safe ones,
    /// so the engine on its own (tests, or a missing hook) denies every approval, offers no file and shows nothing.
    /// </remarks>
    internal static Func<InstalledAddon, string, string, bool> Approver = (_, _, _) => false;
    /// <summary>The file prompt: returns a path the user picked, or null.</summary>
    internal static Func<InstalledAddon, string, string?> FilePrompt = (_, _) => null;
    /// <summary>A line for the status bar (ui.notify).</summary>
    internal static Action<string> Notify = _ => { };
    /// <summary>Runs a bridge call where app state may be touched (the app's UI thread).</summary>
    internal static Func<Func<Task<JsonNode?>>, Task<JsonNode?>> Dispatch = call => call();
    /// <summary>Self-test: sees each sandboxed process right after it starts (to check that others can't open it).</summary>
    internal static Action<SandboxProcess>? Started;

    private static int _running;
    public static bool Busy => _running > 0;

    public static async Task<AddonRunResult> RunAsync(InstalledAddon a, string commandId, AddonContext ctx, CancellationToken ct = default)
    {
        var log = new List<string>();
        AddonRunResult Fail(string why, string status = "refused") { Record(a, commandId, status + ": " + why); return new(false, why, true, log, status); }
        if (!a.Enabled) return Fail("The add-on is disabled" + (a.DisabledReason != null ? $" ({a.DisabledReason})." : "."));
        var cmd = a.Manifest.Commands.FirstOrDefault(c => c.Id == commandId);
        if (cmd == null) return Fail("Unknown command.");
        if (a.Manifest.Api < 2) return Fail("This add-on uses the old, unencrypted bridge (API 1). Install a version built for API 2 (encrypted).", "blocked");
        if (AddonPackages.VerifyFiles(a) is { } tamper)
        {
            AddonPackages.SetEnabled(a, false, "files changed after install: " + tamper);
            return Fail($"The add-on's files no longer match what you approved ({tamper}). It was disabled; reinstall it from a trusted package.", "blocked");
        }

        var entry = Path.Combine(a.Folder, a.Manifest.Entry);
        var data = AddonPackages.DataFolder(a.Id);
        Directory.CreateDirectory(data);
        var cmdLine = entry.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            ? $"\"{Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")}\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{entry}\""
            : $"\"{entry}\"";

        SandboxProcess p;
        try { p = SandboxProcess.Start(cmdLine, a.Folder, ChildEnvironment(a, data)); }
        catch (Exception ex) { return Fail("The sandboxed process could not be created: " + ex.Message + ". The add-on did not run (there is no unsandboxed fallback).", "failed"); }

        Started?.Invoke(p);
        Interlocked.Increment(ref _running);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Manifest.TimeoutSeconds, 5, 600));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string? doneText = null; bool show = false; string status = "ok";
        var stderr = new StringBuilder();
        try
        {
            _ = Task.Run(async () =>
            {
                var buf = new byte[4096];
                try { int n; while ((n = await p.StdErr.ReadAsync(buf, CancellationToken.None)) > 0 && stderr.Length < 8000) stderr.Append(Encoding.UTF8.GetString(buf, 0, n)); } catch { }
            }, CancellationToken.None);   // drains until the pipe closes, whatever the run's token says
            var writer = new StreamWriter(p.StdIn, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var reader = new LineReader(p.StdOut);
            // Encrypted bridge (API 2): plain-text hello both ways (ephemeral ECDH keys), then AES-256-GCM frames only.
            using var bridge = new AddonBridge();
            await writer.WriteLineAsync(bridge.Hello());
            string? hello = null;
            try { do hello = await reader.ReadLineAsync(cts.Token); while (hello is { } hl && hl.Trim().Length == 0); }
            catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "cancelled" : "timed out"; }
            catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message); }
            if (status == "ok")
            {
                if (hello == null) status = "ended before the encrypted handshake";
                else try { bridge.Accept(hello); }
                    catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message); }
            }
            if (status == "ok")
                await writer.WriteLineAsync(bridge.Seal(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "invoke",
                    ["params"] = new JsonObject { ["api"] = 2, ["command"] = commandId, ["context"] = Context(ctx) },
                }.ToJsonString()));
            int messages = 0; long bytes = 0;
            while (status == "ok")
            {
                string? line;
                try { line = await reader.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "cancelled" : "timed out"; break; }
                catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message); break; }
                if (line == null) break;
                if (line.Trim().Length == 0) continue;
                bytes += line.Length; messages++;
                if (messages > MaxMessages || bytes > MaxBytes) { status = "flood"; log.Add($"✕ more than {MaxMessages} messages / {MaxBytes >> 20} MB — stopped"); break; }
                string text;
                try { text = bridge.Open(line); }
                catch (InvalidDataException ex) { status = "protocol error"; log.Add("✕ " + ex.Message + " — the run was stopped"); break; }
                JsonObject? msg;
                try { msg = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { msg = null; }
                if (msg == null) { status = "protocol error"; log.Add("✕ not a JSON object: " + Trim(text, 120)); break; }
                var method = msg["method"]?.GetValue<string>() ?? "";
                if (method == "log") { log.Add(Trim(msg["params"]?["text"]?.ToString() ?? "", 2000)); continue; }
                if (method == "done") { doneText = Trim(msg["params"]?["text"]?.ToString() ?? "", 100_000); show = msg["params"]?["show"]?.GetValue<bool>() == true; break; }
                if (msg["id"] is not JsonValue idv) { status = "protocol error"; log.Add("✕ a call without an id"); break; }
                JsonObject reply;
                try
                {
                    var result = await Dispatch(() => Call(a, method, msg["params"] as JsonObject ?? new JsonObject(), ctx, log, cts.Token));
                    reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idv.DeepClone(), ["result"] = result };
                }
                catch (AddonError e) { reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idv.DeepClone(), ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message } }; if (e.Code is -32001 or -32003) log.Add($"⛔ {method}: {e.Message}"); }
                catch (Exception e) { reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idv.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "failed: " + Trim(e.Message, 300) } }; }
                try { await writer.WriteLineAsync(bridge.Seal(reply.ToJsonString())); } catch (IOException) { break; }
            }
            if (status == "ok") await p.WaitForExitAsync(TimeSpan.FromSeconds(3), cts.Token);
        }
        finally
        {
            int code = p.HasExited ? p.ExitCode : -1;
            if (!p.HasExited) p.Kill();
            p.Dispose();
            Interlocked.Decrement(ref _running);
            if (doneText == null && status == "ok") status = code == 0 ? "ended without a result" : $"crashed (exit code {code})";
            if (stderr.Length > 0 && status != "ok") log.Add("stderr: " + Trim(stderr.ToString(), 2000));
            Record(a, commandId, status + (doneText != null ? " · " + Trim(doneText.Replace('\n', ' '), 120) : ""));
            AuditLog.Write("add-on command run", $"{a.Id} {commandId} · {status}");
        }
        bool ok = doneText != null && status == "ok";
        return new(ok, doneText ?? $"The add-on {status}.", show || !ok, log, status);
    }

    /// <summary>What the add-on learns about where it was run from: never message content (that needs mail.read).</summary>
    private static JsonObject Context(AddonContext ctx) => new()
    {
        ["alias"] = ctx.Alias is { } a ? new JsonObject { ["email"] = a.Email, ["service"] = a.ProviderName, ["enabled"] = a.Enabled, ["note"] = a.Description, ["site"] = a.Site } : null,
        ["message"] = ctx.Message is { } m ? new JsonObject { ["id"] = m.Id, ["subject"] = m.Subject, ["from"] = m.From.FirstOrDefault()?.Email } : null,
    };

    private static void Record(InstalledAddon a, string cmd, string what)
    {
        a.RunLog.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {cmd}  {what}");
        while (a.RunLog.Count > 50) a.RunLog.RemoveAt(0);
        try { AppSettings.Save(); } catch { }
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    /// <summary>The child's whole environment: a whitelist of ours (no secrets that happen to be in it) plus DCX_*.</summary>
    private static Dictionary<string, string> ChildEnvironment(InstalledAddon a, string data)
    {
        string[] keep = { "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "PATH", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
                          "USERNAME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData",
                          "CommonProgramFiles", "CommonProgramFiles(x86)", "PSModulePath", "LOCALAPPDATA", "APPDATA" };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keep) if (Environment.GetEnvironmentVariable(k) is { } v) env[k] = v;
        env["TEMP"] = env["TMP"] = data;
        env["DCX_ADDON_ID"] = a.Id;
        env["DCX_ADDON_VERSION"] = a.Manifest.Version;
        env["DCX_ADDON_DATA"] = data;
        env["DCX_ADDON_API"] = "2";
        env["DCX_APP"] = "MaskedMail";
        return env;
    }

    // ---- methods (UI thread) ----------------------------------------------------------------------------------------

    public sealed class AddonError(int code, string msg) : Exception(msg) { public int Code { get; } = code; }

    /// <summary>Every bridge method: name, permission ("" = none), description (app.capabilities lists them).</summary>
    public static readonly (string Name, string Permission, string Description)[] Methods =
    {
        ("app.info", "", "App version, this add-on's id and its granted permissions"),
        ("app.capabilities", "", "Every method an add-on can call, its permission and whether it is granted"),
        ("ui.notify", "", "Show a line in the status bar: {\"text\"}"),
        ("aliases.list", "aliases.read", "Your masked addresses: email, service, enabled, note, site, routed — never API keys"),
        ("aliases.create", "aliases.write", "Create an address (you approve it): {\"service\": a service key from aliases.list (fastmail, simplelogin, addy, relay, duckduckgo, forwardemail, cloudflare, improvmx, migadu, mailcow, mailtm, tempmaillol, dropmail), \"note\", \"site\", \"prefix\", \"domain\", \"forwardTo\"}"),
        ("aliases.setEnabled", "aliases.write", "Enable or disable an address (you approve it): {\"email\",\"enabled\"}"),
        ("aliases.update", "aliases.write", "Change an address's note / site (you approve it): {\"email\",\"note\",\"site\"}"),
        ("throwaway.create", "aliases.write", "Create a throwaway address that ends itself (you approve it): {\"minutes\":1–525600, \"note\", \"service\" (optional)} → {email, expires}"),
        ("throwaway.list", "aliases.read", "Your throwaway addresses and when each expires"),
        ("mail.list", "mail.read", "Messages in a folder: {\"folder\":\"inbox|sent|<masked address>\",\"limit\"≤100,\"search\"}"),
        ("mail.get", "mail.read", "One message: {\"id\"} → subject, from, to, cc, date, text, html, attachment names"),
        ("queue.add", "queue.write", "Add an email to the outgoing queue (not sent): {\"from\",\"to\":[],\"cc\":[],\"subject\",\"html\"|\"text\",\"copies\"}"),
        ("mail.send", "mail.send", "Send an email now (you approve each one): same fields as queue.add"),
        ("routing.sweep", "routing.run", "File Inbox mail into masked-address folders now; returns how many moved"),
        ("files.pick", "filesystem.read", "Ask the user to pick a file; returns its text (≤ 2 MB)"),
    };

    private static void Need(InstalledAddon a, string perm)
    {
        if (!a.Granted.Contains(perm)) throw new AddonError(-32001, $"permission denied: {perm}" + (a.Manifest.Permissions.Contains(perm) ? " (not granted by the user)" : " (not requested in addon.json)"));
    }

    private static void Approve(InstalledAddon a, string title, string detail)
    {
        var ok = Approver(a, title, detail);
        AuditLog.Write(ok ? "add-on action allowed" : "add-on action DENIED", $"{a.Id}: {title}");
        if (!ok) throw new AddonError(-32003, "the user declined");
    }

    private static string Str(JsonObject p, string name, int max, bool required = false)
    {
        var v = p[name];
        if (v == null) { if (required) throw new AddonError(-32602, $"\"{name}\" is required"); return ""; }
        if (v is not JsonValue jv || !jv.TryGetValue<string>(out var s)) throw new AddonError(-32602, $"\"{name}\" must be a string");
        if (s.Length > max) throw new AddonError(-32602, $"\"{name}\" is longer than {max:N0} characters");
        return new string(s.Where(c => !char.IsControl(c) || c is '\n' or '\t' or '\r').ToArray());
    }

    private static List<string> Addresses(JsonObject p, string name, bool required)
    {
        var list = new List<string>();
        if (p[name] is JsonArray arr)
            foreach (var n in arr.Take(100))
            {
                if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) throw new AddonError(-32602, $"\"{name}\" must be a list of addresses");
                try { list.Add(new MailAddress(s.Trim()).Address); } catch (FormatException) { throw new AddonError(-32602, $"not an email address: {Trim(s, 80)}"); }
            }
        else if (p[name] != null) throw new AddonError(-32602, $"\"{name}\" must be a list of addresses");
        if (required && list.Count == 0) throw new AddonError(-32602, $"\"{name}\" needs at least one address");
        return list;
    }

    private static JsonNode? J(object? o) => o == null ? null : JsonSerializer.SerializeToNode(o);

    private static ProviderRegistry Reg() => Registry() ?? throw new AddonError(-32000, "no services are set up yet");
    private static MailService Mail() => Reg().Mail ?? throw new AddonError(-32000, "this needs a Fastmail API token (Settings → Fastmail)");

    private static Alias FindAlias(string email) =>
        Reg().Aliases.FirstOrDefault(x => x.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new AddonError(-32602, "no such masked address");

    private static async Task<JsonNode?> Call(InstalledAddon a, string method, JsonObject p, AddonContext ctx, List<string> log, CancellationToken ct)
    {
        switch (method)
        {
            case "app.info":
                return J(new { app = AppInfo.ProductName, version = AppInfo.Version, addon = a.Id, granted = a.Granted, api = 2, encrypted = true });
            case "app.capabilities":
                return J(new { methods = Methods.Select(m => new { name = m.Name, permission = m.Permission, description = m.Description, granted = m.Permission.Length == 0 || a.Granted.Contains(m.Permission) }) });
            case "ui.notify":
                Notify($"◇ {a.Manifest.Name}: {Str(p, "text", 300)}");
                return J(new { ok = true });

            case "aliases.list":
                Need(a, "aliases.read");
                return J(Reg().Aliases.Select(x => new { email = x.Email, service = x.ProviderName, serviceKey = x.ProviderKey, enabled = x.Enabled, state = x.State, note = x.Description, site = x.Site, routed = x.Routed, created = x.Created }));
            case "aliases.create":
            {
                Need(a, "aliases.write");
                var key = Str(p, "service", 20);
                var provider = key.Length == 0 ? Reg().Providers.FirstOrDefault(x => x.Info.Has(Features.Create)) : Reg().Get(key);
                if (provider == null) throw new AddonError(-32602, key.Length == 0 ? "no masked-email service is set up" : $"the service “{key}” is not set up");
                if (!provider.Info.Has(Features.Create)) throw new AddonError(-32602, $"{provider.Name} can't create addresses");
                var req = new CreateRequest
                {
                    Description = Str(p, "note", 200), Site = Str(p, "site", 200), Prefix = Str(p, "prefix", 64),
                    Domain = Str(p, "domain", 253), ForwardTo = Str(p, "forwardTo", 320),
                };
                Approve(a, "create a masked address", $"Service: {provider.Name}\nNote: {req.Description}\nWebsite: {req.Site}\nStarts with: {req.Prefix}" +
                    (req.Domain.Length > 0 ? $"\nDomain: {req.Domain}" : "") + (req.ForwardTo.Length > 0 ? $"\nMail goes to: {req.ForwardTo}" : "") +
                    (provider.Info.Caution != null ? "\n\n⚠ " + provider.Info.Caution : ""));
                var (alias, folderError) = await Reg().CreateAsync(provider, req, ct);
                AuditLog.Write("masked address created", $"{alias.ProviderName}: {alias.Email} (by add-on {a.Id})");
                return J(new { email = alias.Email, service = alias.ProviderName, routed = alias.Routed, folderError });
            }
            case "aliases.setEnabled":
            {
                Need(a, "aliases.write");
                var alias = FindAlias(Str(p, "email", 320, required: true));
                var on = p["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var b) ? b : throw new AddonError(-32602, "\"enabled\" must be true or false");
                if (Reg().Get(alias.ProviderKey) is not { } svc || !svc.Info.Has(Features.Disable)) throw new AddonError(-32602, $"{alias.ProviderName} can't turn addresses on or off");
                Approve(a, on ? "enable a masked address" : "disable a masked address", $"{alias.Email} ({alias.ProviderName}) → {(on ? "enabled" : "disabled")}");
                await Reg().Get(alias.ProviderKey)!.SetEnabledAsync(alias, on, ct);
                return J(new { email = alias.Email, enabled = alias.Enabled });
            }
            case "throwaway.create":
            {
                Need(a, "aliases.write");
                var minutes = p["minutes"] is JsonValue mv && mv.TryGetValue<int>(out var mm) ? mm : AppSettings.Current.ThrowawayMinutes;
                if (minutes is < 1 or > 525_600) throw new AddonError(-32602, "\"minutes\" must be 1–525600");
                var note = Str(p, "note", 100);
                var key = Str(p, "service", 20);
                var provider = (key.Length == 0 ? Throwaways.DefaultProvider(Reg()) : Reg().Get(key))
                    ?? throw new AddonError(-32000, key.Length == 0 ? "no masked-email service is set up" : $"the service “{key}” is not set up");
                if (!provider.Info.Has(Features.Create)) throw new AddonError(-32602, $"{provider.Name} can't create addresses");
                var lasts = Throwaways.Cap(provider, TimeSpan.FromMinutes(minutes));
                Approve(a, "create a throwaway address", $"Service: {provider.Name}\nLasts: {Throwaways.Describe(lasts)}\nNote: {note}" +
                    (provider.Info.Caution != null ? "\n\n⚠ " + provider.Info.Caution : ""));
                var (alias, info) = await Throwaways.CreateAsync(Reg(), TimeSpan.FromMinutes(minutes), provider, note, ct);
                AuditLog.Write("throwaway created by add-on", $"{a.Id}: {alias.Email}");
                return J(new { email = alias.Email, service = alias.ProviderName, expires = info.Expires });
            }
            case "throwaway.list":
                Need(a, "aliases.read");
                return J(AppSettings.Current.Throwaways.Values.Select(t => new { email = t.Email, service = ProviderRegistry.NameOf(t.ProviderKey), created = t.Created, expires = t.Expires, expired = t.Expired }));
            case "aliases.update":
            {
                Need(a, "aliases.write");
                var alias = FindAlias(Str(p, "email", 320, required: true));
                var note = p["note"] != null ? Str(p, "note", 200) : alias.Description;
                var site = p["site"] != null ? Str(p, "site", 200) : alias.Site;
                Approve(a, "change a masked address", $"{alias.Email}\nNote: {alias.Description} → {note}\nWebsite: {alias.Site} → {site}");
                await Reg().Get(alias.ProviderKey)!.UpdateAsync(alias, note, site, ct);
                return J(new { email = alias.Email, note = alias.Description, site = alias.Site });
            }

            case "mail.list":
            {
                Need(a, "mail.read");
                var mail = Mail();
                var folder = Str(p, "folder", 320);
                string boxId = folder.ToLowerInvariant() switch
                {
                    "" or "inbox" => await mail.RoleIdAsync("inbox", ct),
                    "sent" => await mail.RoleIdAsync("sent", ct),
                    _ => AppSettings.Current.Folders.TryGetValue(folder.Trim(), out var f) ? f : throw new AddonError(-32602, "that masked address has no folder"),
                };
                var limit = p["limit"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? Math.Clamp(l, 1, 100) : 25;
                var (items, total) = await mail.ListAsync(boxId, 0, limit, Str(p, "search", 200), ct);
                return J(new { total, messages = items.Select(m => new { id = m.Id, subject = m.Subject, from = m.From.Select(x => x.ToString()), to = m.To.Select(x => x.ToString()), received = m.Received, preview = m.Preview, unread = m.Unread, hasAttachment = m.HasAttachment }) });
            }
            case "mail.get":
            {
                Need(a, "mail.read");
                var m = await Mail().GetAsync(Str(p, "id", 200, required: true), ct);
                return J(new
                {
                    id = m.Id, subject = m.Subject, from = m.From.Select(x => x.ToString()), to = m.To.Select(x => x.ToString()), cc = m.Cc.Select(x => x.ToString()), date = m.Sent,
                    text = m.Text.Length > 0 ? m.Text : HtmlTools.ToPlainText(m.Html), html = HtmlTools.Sanitize(m.Html), attachments = m.Attachments.Select(x => new { name = x.Name, type = x.Type, size = x.Size }),
                });
            }
            case "queue.add":
            {
                Need(a, "queue.write");
                var q = Compose(p);
                Outbox.Put(q);
                log.Add($"queued: {q.Subject} → {string.Join(", ", q.To)}");
                AuditLog.Write("add-on queued an email", $"{a.Id}: from {q.FromEmail} to {q.To.Count + q.Cc.Count} recipient(s)");
                return J(new { id = q.Id, queued = Outbox.Items.Count });
            }
            case "mail.send":
            {
                Need(a, "mail.send");
                var q = Compose(p);
                Approve(a, "send an email", $"From: {q.FromEmail}\nTo: {string.Join(", ", q.To)}" + (q.Cc.Count > 0 ? $"\nCc: {string.Join(", ", q.Cc)}" : "") +
                                            $"\nSubject: {q.Subject}" + (q.Copies > 1 ? $"\nCopies: {q.Copies}" : "") + $"\n\n{Trim(HtmlTools.ToPlainText(q.Html), 600)}");
                var identities = await Mail().IdentitiesAsync(ct);
                await Outbox.SendItemAsync(q, Reg(), identities, TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.QueueDelaySeconds, 0, 600)), ct);
                AuditLog.Write("add-on sent an email", $"{a.Id}: from {q.FromEmail} to {q.To.Count + q.Cc.Count} recipient(s)");
                return J(new { sent = q.SentCopies });
            }
            case "routing.sweep":
                Need(a, "routing.run");
                return J(new { moved = await Reg().SweepAsync(ct) });
            case "files.pick":
            {
                Need(a, "filesystem.read");
                var path = FilePrompt(a, Str(p, "title", 120));
                if (path == null) return J(new { cancelled = true });
                var fi = new FileInfo(path);
                if (fi.Length > 2 * 1024 * 1024) throw new AddonError(-32602, "files above 2 MB are not passed to add-ons");
                AuditLog.Write("file given to add-on", $"{a.Id}: {fi.Name}");
                return J(new { name = fi.Name, text = await File.ReadAllTextAsync(path, ct) });
            }
            default:
                throw new AddonError(-32601, "unknown method " + Trim(method, 60) + " (app.capabilities lists them all)");
        }

        QueuedEmail Compose(JsonObject p)
        {
            var from = Str(p, "from", 320, required: true).Trim();
            var alias = Reg().Aliases.FirstOrDefault(x => x.Email.Equals(from, StringComparison.OrdinalIgnoreCase));
            var html = Str(p, "html", 2_000_000);
            if (html.Length == 0) html = HtmlTools.TextToHtml(Str(p, "text", 1_000_000));
            var copies = p["copies"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : 1;
            if (copies is < 1 or > 50) throw new AddonError(-32602, "\"copies\" must be 1–50");
            return new QueuedEmail
            {
                FromEmail = alias?.Email ?? from, AliasProvider = alias?.ProviderKey,
                To = Addresses(p, "to", required: true), Cc = Addresses(p, "cc", required: false),
                Subject = Str(p, "subject", 500), Html = html, Copies = copies,
            };
        }
    }

    /// <summary>UTF-8 line reader with a per-line cap (a missing newline can't make us buffer forever).</summary>
    private sealed class LineReader(Stream s)
    {
        private readonly byte[] _buf = new byte[8192];
        private int _len, _pos;
        private readonly List<byte> _line = [];

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                while (_pos < _len)
                {
                    var b = _buf[_pos++];
                    if (b == (byte)'\n') { var str = Encoding.UTF8.GetString(_line.ToArray()).TrimEnd('\r'); _line.Clear(); return str; }
                    _line.Add(b);
                    if (_line.Count > MaxLine) throw new InvalidDataException($"a message longer than {MaxLine / 1024} KB");
                }
                // Pipe reads on a synchronous handle don't observe the token, so race them against it.
                var read = s.ReadAsync(_buf, 0, _buf.Length, CancellationToken.None);
                var done = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, ct));
                if (done != read) throw new OperationCanceledException(ct);
                _len = await read; _pos = 0;
                if (_len == 0) return _line.Count > 0 ? Encoding.UTF8.GetString(_line.ToArray()) : null;
            }
        }
    }
}
