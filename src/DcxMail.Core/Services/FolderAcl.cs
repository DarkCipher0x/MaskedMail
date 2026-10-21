using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DcxMail.Services;

/// <summary>
/// Locks the app's folders to the signed-in user (and SYSTEM): a protected access list with no inherited entries, so
/// other accounts on the PC can't even list the encrypted files. The encryption doesn't depend on this; it's a second
/// layer. Add-ons (same user, Low integrity) can still read their own program files but can't write here.
/// </summary>
public static class FolderAcl
{
    public static void Protect(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var info = new DirectoryInfo(dir);
            var me = WindowsIdentity.GetCurrent().User!;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (IsProtected(dir)) return;
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.SetOwner(me);
            info.SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException) { /* best effort: the files are encrypted anyway */ }
    }

    /// <summary>True when the folder's list is protected and grants only the current user and SYSTEM.</summary>
    public static bool IsProtected(string dir)
    {
        var sec = new DirectoryInfo(dir).GetAccessControl();
        if (!sec.AreAccessRulesProtected) return false;
        var me = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var sids = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet();
        return sids.SetEquals([me, system]);
    }
}
