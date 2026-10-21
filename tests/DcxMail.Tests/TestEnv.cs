using System.Runtime.CompilerServices;
using DcxMail.Mail;
using DcxMail.Services;
using Xunit;

// The store, the settings and the queue are process-wide: one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DcxMail.Tests;

/// <summary>A private data folder for the whole run, and a clean store for each test that wants one.</summary>
internal static class TestEnv
{
    /// <summary>Runs before anything reads <see cref="SecureStore.DataDir"/>: the real data folder is never touched.</summary>
    [ModuleInitializer]
    internal static void UseScratchFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcxmail-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DCX_DATA_DIR", dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
    }

    /// <summary>An empty, unlocked store (new key), default settings, an empty queue and log.</summary>
    internal static void Fresh()
    {
        SecureStore.ResetForTests();
        AuditLog.ResetForTests();
        if (Directory.Exists(SecureStore.DataDir))
            foreach (var f in Directory.GetFiles(SecureStore.DataDir)) File.Delete(f);
        Assert.True(SecureStore.Initialize());
        AppSettings.Load();
        Outbox.Items.Clear();
    }

    internal static AppSettings S => AppSettings.Current;

    /// <summary>Settings with some services set up: ("addy", "key", "k"), ("mailtm", "enabled", "true")…</summary>
    internal static AppSettings With(params (string Service, string Field, string Value)[] values)
    {
        var s = new AppSettings();
        foreach (var (service, field, value) in values)
        {
            if (!s.Services.TryGetValue(service, out var f)) s.Services[service] = f = new(StringComparer.OrdinalIgnoreCase);
            f[field] = value;
        }
        return s;
    }
}
