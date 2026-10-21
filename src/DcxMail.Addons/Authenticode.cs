using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DcxMail.Addons;

/// <summary>The Authenticode state of an add-on's .exe for the approval screen (WinVerifyTrust, no UI, cached revocation
/// data only — inspecting a package never goes online).</summary>
public static class Authenticode
{
    public static string Describe(string path)
    {
        int rc = WinVerify(path);
        if (rc == unchecked((int)0x800B0100)) return "NOT signed";
        if (rc != 0) return $"Signature INVALID or untrusted (WinVerifyTrust 0x{rc:X8})";
        try
        {
            var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return $"Signed by {signer.GetNameInfo(X509NameType.SimpleName, false)} (signature valid)";
        }
        catch (Exception ex) { return "Signature present but the signer couldn't be read: " + ex.Message; }
    }

    private static int WinVerify(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* WTD_UI_NONE */, dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFile,
                fdwRevocationChecks = 1 /* WTD_REVOKE_WHOLECHAIN */,
                dwProvFlags = 0x00000080 /* CHAIN_EXCLUDE_ROOT */ | 0x00001000 /* WTD_CACHE_ONLY_URL_RETRIEVAL */,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile; public int dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
