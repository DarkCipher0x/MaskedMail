using System.Runtime.InteropServices;

namespace DcxMail.Services;

/// <summary>
/// Process-wide Windows mitigations, applied first thing at start-up:
/// <list type="bullet">
/// <item>Image load policy: no DLLs from remote (network) locations and none carrying a Low integrity label, so a
/// Low-integrity add-on (or anything else that can only write Low-labelled files) can't plant a DLL for the app.</item>
/// <item>Safe DLL search: the current directory is removed from the DLL search path.</item>
/// <item>Heap termination on corruption.</item>
/// </list>
/// Mitigations that would break .NET (dynamic code) or input methods (extension points) are deliberately not used.
/// </summary>
public static class ProcessHardening
{
    public static List<string> Applied { get; } = [];

    public static void Apply()
    {
        try { if (SetDllDirectory("")) Applied.Add("current directory removed from DLL search"); } catch { }
        try { if (HeapSetInformation(IntPtr.Zero, 1 /*HeapEnableTerminationOnCorruption*/, IntPtr.Zero, 0)) Applied.Add("heap termination on corruption"); } catch { }
        try
        {
            // PROCESS_MITIGATION_IMAGE_LOAD_POLICY: NoRemoteImages (bit 0) | NoLowMandatoryLabelImages (bit 1)
            uint flags = 0b11;
            if (SetProcessMitigationPolicy(10 /*ProcessImageLoadPolicy*/, ref flags, sizeof(uint))) Applied.Add("no remote or Low-labelled DLLs");
        }
        catch { }
    }

    /// <summary>The image load policy in force (self-test).</summary>
    public static uint ImageLoadPolicy()
    {
        uint flags = 0;
        try { GetProcessMitigationPolicy(GetCurrentProcess(), 10, ref flags, sizeof(uint)); } catch { }
        return flags;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool HeapSetInformation(IntPtr heap, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, ref uint buffer, int length);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
