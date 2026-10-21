using System.Text;
using DcxMail.Services;
using Xunit;

namespace DcxMail.Tests;

/// <summary>The encrypted store, the master password and 2FA, backups, the security log and the sign-in throttle.</summary>
public sealed class StoreSecurityTests
{
    private const string Token = "fmu1-UNITTEST-TOKEN";

    public StoreSecurityTests() => TestEnv.Fresh();

    private static string Ring() => File.ReadAllText(Path.Combine(SecureStore.DataDir, "keyring.json"));

    private static void SaveToken()
    {
        TestEnv.S.FastmailToken = Token;
        TestEnv.S.Folders["A@x.com"] = "mb1";
        AppSettings.Save();
    }

    [Fact]
    public void Encrypt_round_trips_and_hides_the_plain_text()
    {
        var blob = SecureStore.Encrypt("secret"u8.ToArray(), "a.dcx");
        Assert.Equal("secret", Encoding.UTF8.GetString(SecureStore.Decrypt(blob, "a.dcx")));
        Assert.False(blob.AsSpan().IndexOf("secret"u8) >= 0);
    }

    [Fact]
    public void A_file_is_bound_to_its_name() =>
        Assert.ThrowsAny<Exception>(() => SecureStore.Decrypt(SecureStore.Encrypt([1, 2, 3], "a.dcx"), "b.dcx"));

    [Fact]
    public void Tampering_is_detected()
    {
        var blob = SecureStore.Encrypt([1, 2, 3, 4], "x.dcx");
        blob[^1] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => SecureStore.Decrypt(blob, "x.dcx"));
    }

    [Fact]
    public void Settings_are_saved_encrypted_and_keep_case_insensitive_folders()
    {
        SaveToken();
        var raw = File.ReadAllText(Path.Combine(SecureStore.DataDir, AppSettings.FileName));
        AppSettings.Load();
        Assert.DoesNotContain("UNITTEST", raw);
        Assert.Equal(Token, TestEnv.S.FastmailToken);
        Assert.True(TestEnv.S.Folders.ContainsKey("a@X.com"));
    }

    [Fact]
    public void Legacy_key_dpapi_moves_into_the_key_ring()
    {
        SaveToken();
        SecureStore.WriteLegacyLayoutForTests();
        SecureStore.ResetForTests();
        Assert.True(SecureStore.Initialize());
        AppSettings.Load();
        Assert.Equal(Token, TestEnv.S.FastmailToken);
        Assert.True(File.Exists(Path.Combine(SecureStore.DataDir, "keyring.json")));
        Assert.False(File.Exists(Path.Combine(SecureStore.DataDir, "key.dpapi")));
    }

    [Fact]
    public void Master_password_on_start_keeps_no_Windows_copy_and_refuses_wrong_passwords()
    {
        const string pw = "correct horse battery staple";
        SaveToken();
        SecureStore.SetPassword(pw, requireOnStart: true);
        Assert.True(SecureStore.PasswordRequiredOnStart);
        Assert.DoesNotContain("\"Dpapi\"", Ring());

        SecureStore.ResetForTests();
        Assert.False(SecureStore.Initialize());
        Assert.ThrowsAny<Exception>(AppSettings.Load);
        Assert.False(SecureStore.Unlock("wrong password here"));
        Assert.True(SecureStore.Unlock(pw));
        AppSettings.Load();
        Assert.Equal(Token, TestEnv.S.FastmailToken);
        Assert.True(SecureStore.VerifyPassword(pw));
        Assert.False(SecureStore.VerifyPassword("nope nope nope"));

        SecureStore.RemovePassword();
        SecureStore.ResetForTests();
        Assert.True(SecureStore.Initialize());
        Assert.False(SecureStore.HasPassword);
    }

    [Fact]
    public void Totp_matches_the_RFC_6238_vectors()
    {
        var secret = "12345678901234567890"u8.ToArray();
        Assert.Equal("287082", Totp.Code(secret, 59 / 30));
        Assert.Equal("081804", Totp.Code(secret, 1111111109 / 30));
        Assert.Equal("279037", Totp.Code(secret, 2000000000 / 30));
        Assert.Equal("MZXW6YTBOI", Totp.Base32("foobar"u8.ToArray()));
        Assert.StartsWith("otpauth://totp/DarkCipher%20MaskedMail", Totp.Uri(secret, "a@b"), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_factor_codes_are_single_use_with_a_30_second_window_and_sealed()
    {
        var secret = Totp.NewSecret();
        var codes = Totp.RecoveryCodes();
        SecureStore.SetTotp(secret, codes);
        var now = DateTimeOffset.UtcNow;
        var step = now.ToUnixTimeSeconds() / 30;
        Assert.DoesNotContain(Totp.Base32(secret), Ring());
        Assert.DoesNotContain(codes[0], Ring());

        var code = Totp.Code(secret, step);
        Assert.True(SecureStore.VerifySecondFactorAt(code, now));
        Assert.False(SecureStore.VerifySecondFactorAt(code, now));                          // no replay
        Assert.True(SecureStore.VerifySecondFactorAt(Totp.Code(secret, step + 1), now));    // +30 s
        Assert.False(SecureStore.VerifySecondFactorAt(Totp.Code(secret, step - 5), now));   // stale
        Assert.True(SecureStore.VerifySecondFactor(codes[3].ToLowerInvariant().Replace("-", "", StringComparison.Ordinal)));
        Assert.False(SecureStore.VerifySecondFactor(codes[3]));                             // recovery codes are single-use
        Assert.Equal(9, SecureStore.RecoveryCodesLeft);

        SecureStore.RemoveTotp();
        Assert.False(SecureStore.HasTotp);
    }

    [Fact]
    public void Backups_have_their_own_password_and_hold_nothing_in_plain_text()
    {
        SaveToken();
        var file = Path.Combine(SecureStore.DataDir, "t.dcxbackup");
        Backup.Export(file, "backup password 123");
        Assert.DoesNotContain("UNITTEST", File.ReadAllText(file));
        var restored = Backup.Open(file, "backup password 123");
        Assert.Equal(Token, restored.FastmailToken);
        Assert.True(restored.Folders.ContainsKey("a@x.com"));
        Assert.ThrowsAny<Exception>(() => Backup.Open(file, "backup password 124"));
    }

    [Theory]
    [InlineData("short1!")]
    [InlineData("aaaaaaaaaaaaaa")]
    [InlineData("password1234")]
    [InlineData("Password2024!")]
    [InlineData("qwertyqwerty")]
    [InlineData("abcdefghijkl")]
    public void Weak_passwords_are_refused(string pw) => Assert.NotNull(SecureStore.PasswordProblem(pw));

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("Tr0ub4dor&3xyz")]
    [InlineData("mY-cat-ate-14-socks")]
    public void Strong_passwords_are_accepted(string pw) => Assert.Null(SecureStore.PasswordProblem(pw));

    [Fact]
    public void Wrong_attempts_are_counted_across_restarts_and_reset_on_success()
    {
        for (int i = 0; i < 5; i++) SecureStore.RecordFailure();
        SecureStore.ResetForTests();                 // like closing and reopening the app
        SecureStore.Initialize();
        Assert.Equal(5, SecureStore.FailedAttempts);
        Assert.True(SecureStore.RetryWait() > TimeSpan.Zero);
        SecureStore.RecordSuccess();
        Assert.Equal(0, SecureStore.FailedAttempts);
        Assert.Equal(TimeSpan.Zero, SecureStore.RetryWait());
    }

    [Fact]
    public void The_data_folder_is_locked_to_this_user() => Assert.True(FolderAcl.IsProtected(SecureStore.DataDir));

    [Fact]
    public void Security_log_lines_are_sealed_and_appended_junk_shows()
    {
        AuditLog.Write("unit event", "detail-SECRETWORD");
        Assert.DoesNotContain("SECRETWORD", File.ReadAllText(AuditLog.Path));
        Assert.Contains(AuditLog.Read(), e => e.Action == "unit event" && e.Detail == "detail-SECRETWORD");
        File.AppendAllText(AuditLog.Path, Convert.ToBase64String(new byte[48]) + "\n");
        Assert.Contains(AuditLog.Read(), e => e.Action == AuditLog.Broken);
    }

    [Fact]
    public void Security_log_detects_a_line_removed_from_the_middle()
    {
        AuditLog.Write("one"); AuditLog.Write("two"); AuditLog.Write("three");
        var clean = AuditLog.Read();
        Assert.Equal(3, clean.Count);
        Assert.DoesNotContain(clean, e => e.Action == AuditLog.Broken);
        var lines = File.ReadAllLines(AuditLog.Path).Where(l => l.Length > 0).ToList();
        File.WriteAllLines(AuditLog.Path, [lines[0], lines[2]]);
        Assert.Contains(AuditLog.Read(), e => e.Action == AuditLog.Broken);
    }

    [Fact]
    public void Error_log_lines_are_sealed()
    {
        ErrorLog.Write("unit", new InvalidOperationException("boom-SECRETWORD"));
        Assert.DoesNotContain("SECRETWORD", File.ReadAllText(ErrorLog.Path));
        Assert.Contains(ErrorLog.Read(), e => e.Where == "unit" && e.Message == "boom-SECRETWORD");
    }

    [Fact]
    public void App_lock_needs_a_factor_and_sandbox_needs_a_required_password()
    {
        Assert.False(SecurityPolicy.CanLock);
        TestEnv.S.SandboxMode = true;
        Assert.True(SecurityPolicy.ReconcileSandbox());      // no required password: sandbox switched off
        Assert.False(TestEnv.S.SandboxMode);
    }
}
