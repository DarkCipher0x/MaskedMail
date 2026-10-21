using System.IO.Compression;
using System.Text;
using DcxMail.Addons;
using DcxMail.Fakes;
using DcxMail.Mail;
using DcxMail.Providers;
using DcxMail.Services;
using Xunit;

namespace DcxMail.Tests;

/// <summary>Add-ons: real packages, the real sandbox (Low integrity, Job, locked process) and the real encrypted bridge,
/// driven by Windows PowerShell with the SDK, the same way a user's add-on runs.</summary>
public sealed class AddonTests : IAsyncLifetime
{
    private const string ProbeScript = """
        $ErrorActionPreference = 'Stop'
        . "$PSScriptRoot\DcxAddon.ps1"
        $invoke = Connect-Dcx
        switch ($invoke.params.command) {
          'list'  { $a = @(Invoke-Dcx 'aliases.list'); $alias = $invoke.params.context.alias.email; Complete-Dcx ("aliases=" + $a.Count + " context=" + $alias) }
          'send'  { try { [void](Invoke-Dcx 'mail.send' @{ from = 'x@example.org'; to = @('a@example.org'); subject = 's'; text = 't' }); Complete-Dcx 'sent' } catch { Complete-Dcx ('refused: ' + $_.Exception.Message) } }
          'queue' { try { [void](Invoke-Dcx 'queue.add' @{ from = 'x@example.org'; to = @('a@example.org'); subject = 's'; text = 't' }); Complete-Dcx 'queued' } catch { Complete-Dcx ('refused: ' + $_.Exception.Message) } }
          'write' { try { Set-Content -Path (Join-Path $env:USERPROFILE 'dcxmail-lowil-probe.txt') -Value 'x'; Complete-Dcx 'WROTE' } catch { Complete-Dcx 'blocked-write' } }
          'plain' { [Console]::Out.WriteLine('{"jsonrpc":"2.0","method":"done","params":{"text":"plain"}}'); [Console]::Out.Flush(); Start-Sleep -Seconds 3 }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dcxmail-addon-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeJmap _fake = new();
    private ProviderRegistry _reg = null!;

    public async Task InitializeAsync()
    {
        TestEnv.Fresh();
        Directory.CreateDirectory(_dir);
        _reg = new ProviderRegistry(new AppSettings { FastmailToken = "t" }, _fake);
        await _reg.RefreshAsync();
        AddonHost.Registry = () => _reg;
        AddonHost.Approver = (_, _, _) => false;
    }

    public Task DisposeAsync()
    {
        AddonHost.Started = null;
        AddonHost.Registry = () => null;
        foreach (var a in AddonPackages.All.ToList()) AddonPackages.Remove(a);
        _reg.Dispose();
        TryDelete(_dir);
        TryDelete(AddonPackages.AddonsRoot);
        TryDelete(Path.GetDirectoryName(AddonPackages.DataFolder("x"))!);
        return Task.CompletedTask;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] Sdk(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "sdk", name));

    private string Pack(string name, params (string Path, byte[] Data)[] files)
    {
        var path = Path.Combine(_dir, name + ".dcxaddon");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, d) in files) { using var s = zip.CreateEntry(p).Open(); s.Write(d); }
        return path;
    }

    private static string Program(string version, string perms) => $$"""
        { "api": 2, "id": "test.probe", "name": "Probe", "version": "{{version}}", "publisher": "Unit test", "entry": "run.ps1", "timeoutSeconds": 30,
          "permissions": [{{perms}}],
          "commands": [ {"id":"list","title":"List","context":"alias"}, {"id":"send","title":"Send","context":"global"}, {"id":"queue","title":"Queue","context":"global"},
                        {"id":"write","title":"Write","context":"global"}, {"id":"plain","title":"Plain","context":"global"} ] }
        """;

    private static (string, byte[])[] ProgramFiles(string manifest) =>
        [("addon.json", U(manifest)), ("run.ps1", U(ProbeScript)), ("DcxAddon.ps1", Sdk("DcxAddon.ps1")), ("DcxBridge.dll", Sdk("DcxBridge.dll"))];

    private InstalledAddon InstallProbe(params string[] granted)
    {
        var info = AddonPackages.Inspect(Pack("probe1", ProgramFiles(Program("1.0.0", "\"aliases.read\", \"mail.send\", \"queue.write\""))));
        Assert.Null(info.Refusal);
        return AddonPackages.Install(info, granted);
    }

    [Fact]
    public void Bad_packages_are_refused()
    {
        var api1 = AddonPackages.Inspect(Pack("api1", ("addon.json", U("""{"api":1,"id":"test.old","name":"Old","version":"1.0.0","publisher":"x","entry":"run.ps1","commands":[{"id":"a","title":"A"}]}""")), ("run.ps1", U("x")))).Refusal;
        var slip = AddonPackages.Inspect(Pack("slip", ("addon.json", U(Program("1.0.0", ""))), ("../evil.ps1", U("x")))).Refusal;
        var perm = AddonPackages.Inspect(Pack("perm", ProgramFiles(Program("1.0.0", "\"vault.read\"")))).Refusal;
        var both = AddonPackages.Inspect(Pack("both", ("addon.json", U("""{"api":2,"id":"test.both","name":"Both","version":"1.0.0","publisher":"x","entry":"run.ps1","theme":"t.json","commands":[{"id":"a","title":"A"}]}""")), ("run.ps1", U("x")), ("t.json", U("""{"colors":{"Bg":{"dark":"#000000"}}}""")))).Refusal;
        var badTheme = AddonPackages.Inspect(Pack("badtheme", ("addon.json", U("""{"api":2,"id":"test.bad","name":"Bad","version":"1.0.0","publisher":"x","theme":"t.json"}""")), ("t.json", U("""{"colors":{"Bg":{"dark":"red"}},"xaml":"<x/>"}""")))).Refusal;
        Assert.Contains("API 1", api1, StringComparison.Ordinal);
        Assert.Contains("Unsafe path", slip, StringComparison.Ordinal);
        Assert.Contains("Unknown permission", perm, StringComparison.Ordinal);
        Assert.NotNull(both);
        Assert.Contains("theme", badTheme, StringComparison.Ordinal);
    }

    [Fact]
    public void A_theme_is_sealed_on_disk_applied_and_disabled_when_tampered_with()
    {
        var info = AddonPackages.Inspect(Pack("theme", ("addon.json", U("""{"api":2,"id":"test.theme","name":"Test theme","version":"1.0.0","publisher":"Unit test","theme":"theme.json"}""")),
                                                      ("theme.json", U("""{"name":"t","colors":{"Accent":{"dark":"#4C8DFF","light":"#1F4FB0"},"Bg":{"dark":"#070B16"}}}"""))));
        Assert.Null(info.Refusal);
        var a = AddonPackages.Install(info, []);
        var themeFile = Path.Combine(a.Folder, "theme.json");
        Assert.True(a.Sealed);
        Assert.DoesNotContain("4C8DFF", File.ReadAllText(themeFile));

        TestEnv.S.ThemeAddon = a.Id;
        Assert.Equal("#4C8DFF", ThemeAddons.Active()?.Get("Accent", true), StringComparer.OrdinalIgnoreCase);

        var bytes = File.ReadAllBytes(themeFile); bytes[^1] ^= 0x55; File.WriteAllBytes(themeFile, bytes);
        Assert.Null(ThemeAddons.Active());
        Assert.False(a.Enabled);
        Assert.Null(TestEnv.S.ThemeAddon);
    }

    [Fact]
    public async Task A_program_runs_at_Low_integrity_locked_away_and_cannot_write_to_the_profile()
    {
        var probe = InstallProbe("aliases.read");
        int rid = -1; bool openable = true;
        AddonHost.Started = p => { rid = p.IntegrityRid(); openable = p.OthersCanOpen(); };
        var r = await AddonHost.RunAsync(probe, "list", new AddonContext(_reg.Aliases[0]));
        AddonHost.Started = null;
        Assert.True(r.Ok, $"{r.Status} {r.Text} {string.Join(" | ", r.Log)}");
        Assert.Contains($"aliases={_reg.Aliases.Count}", r.Text, StringComparison.Ordinal);
        Assert.Contains(_reg.Aliases[0].Email, r.Text, StringComparison.Ordinal);
        Assert.Equal(0x1000, rid);                                  // Low integrity
        Assert.False(openable);

        var w = await AddonHost.RunAsync(probe, "write", new AddonContext());
        var probeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dcxmail-lowil-probe.txt");
        var wrote = File.Exists(probeFile);
        if (wrote) File.Delete(probeFile);
        Assert.False(wrote, "the add-on wrote to the user profile");
        Assert.Equal("blocked-write", w.Text);
    }

    [Fact]
    public async Task Ungranted_and_declined_calls_are_refused_and_granted_queue_add_works()
    {
        var probe = InstallProbe("aliases.read", "queue.write");    // mail.send requested but NOT granted
        var r = await AddonHost.RunAsync(probe, "send", new AddonContext());
        Assert.Contains("permission denied: mail.send", r.Text, StringComparison.Ordinal);

        AddonPackages.SetPermission(probe, "mail.send", true);
        bool asked = false;
        AddonHost.Approver = (_, _, _) => { asked = true; return false; };
        var before = _fake.Submissions;
        var d = await AddonHost.RunAsync(probe, "send", new AddonContext());
        Assert.True(asked);
        Assert.Contains("declined", d.Text, StringComparison.Ordinal);
        Assert.Equal(before, _fake.Submissions);

        var q = await AddonHost.RunAsync(probe, "queue", new AddonContext());
        Assert.Equal("queued", q.Text);
        Assert.Equal("a@example.org", Assert.Single(Outbox.Items).To[0]);
    }

    [Fact]
    public async Task Plain_text_lines_tampering_new_permissions_and_downgrades_are_all_handled()
    {
        var probe = InstallProbe("aliases.read", "queue.write");
        var plain = await AddonHost.RunAsync(probe, "plain", new AddonContext());
        Assert.False(plain.Ok);
        Assert.Equal("protocol error", plain.Status);

        // Update 1.1.0 asks for a new permission; installing without ticking it must not grant it.
        var upd = AddonPackages.Inspect(Pack("probe2", ProgramFiles(Program("1.1.0", "\"aliases.read\", \"mail.send\", \"queue.write\", \"mail.read\""))));
        Assert.Equal(["mail.read"], upd.NewPermissions);
        probe = AddonPackages.Install(upd, []);
        Assert.DoesNotContain("mail.read", probe.Granted);
        Assert.Contains("aliases.read", probe.Granted);

        var down = AddonPackages.Inspect(Pack("probe0", ProgramFiles(Program("1.0.5", "\"aliases.read\""))));
        Assert.Contains("Downgrades", down.Refusal, StringComparison.Ordinal);

        File.AppendAllText(Path.Combine(probe.Folder, "run.ps1"), "\n# changed");
        var t = await AddonHost.RunAsync(probe, "list", new AddonContext(_reg.Aliases[0]));
        Assert.Equal("blocked", t.Status);
        Assert.False(probe.Enabled);
    }
}
