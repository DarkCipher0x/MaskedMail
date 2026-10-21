using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DcxMail.Addons;

/// <summary>
/// Starts a program at LOW integrity inside a Job object, with only stdin/stdout/stderr pipes inherited and a whitelisted
/// environment (ADR-012). Low integrity: no writes to Medium-labelled places (the user profile, the app, other add-ons);
/// Job: killed with the job / on Kill, memory capped, few processes, no breakaway. There is no unsandboxed fallback.
/// Hardening (ADR-035): an explicit handle list (PROC_THREAD_ATTRIBUTE_HANDLE_LIST) so ONLY the three pipe ends are
/// inherited, whatever else in this process happens to be inheritable; and process / thread security descriptors that
/// let no other process (other add-ons, other programs of the same user) open the add-on to read its memory, duplicate its
/// pipe handles or inject into it — only SYSTEM, plus "query limited information". OWNER RIGHTS is pinned so the owner
/// can't rewrite that DACL. The app keeps full access through the handles CreateProcess returns.
/// </summary>
public sealed class SandboxProcess : IDisposable
{
    public const long MemoryLimit = 512L << 20;
    public const int ProcessLimit = 8;

    private readonly IntPtr _process, _job;
    public Stream StdIn { get; }
    public Stream StdOut { get; }
    public Stream StdErr { get; }
    public int Pid { get; }
    private bool _disposed;

    private SandboxProcess(IntPtr process, IntPtr job, int pid, Stream i, Stream o, Stream e) { _process = process; _job = job; Pid = pid; StdIn = i; StdOut = o; StdErr = e; }

    /// <param name="commandLine">Full command line (quoted).</param>
    public static SandboxProcess Start(string commandLine, string workingDir, IDictionary<string, string> env)
    {
        IntPtr token = IntPtr.Zero, lowToken = IntPtr.Zero, sid = IntPtr.Zero, job = IntPtr.Zero;
        IntPtr attrs = IntPtr.Zero, inherit = IntPtr.Zero, processSd = IntPtr.Zero, threadSd = IntPtr.Zero;
        SafeFileHandle? inR = null, inW = null, outR = null, outW = null, errR = null, errW = null;
        var pi = new PROCESS_INFORMATION();
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out token)) throw Win("OpenProcessToken");
            if (!DuplicateTokenEx(token, 0, IntPtr.Zero, 2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out lowToken)) throw Win("DuplicateTokenEx");
            if (!ConvertStringSidToSid("S-1-16-4096", out sid)) throw Win("ConvertStringSidToSid");   // Low mandatory level
            var tml = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = 0x20 /*SE_GROUP_INTEGRITY*/ } };
            int size = Marshal.SizeOf(tml) + GetLengthSid(sid);
            if (!SetTokenInformation(lowToken, 25 /*TokenIntegrityLevel*/, ref tml, size)) throw Win("SetTokenInformation(IntegrityLevel)");

            Pipe(out inR, out inW, childReads: true);
            Pipe(out outR, out outW, childReads: false);
            Pipe(out errR, out errW, childReads: false);

            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>(), dwFlags = 0x100 /*STARTF_USESTDHANDLES*/, hStdInput = inR.DangerousGetHandle(), hStdOutput = outW.DangerousGetHandle(), hStdError = errW.DangerousGetHandle() },
            };
            // Only these three handles may be inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST).
            inherit = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(inherit, 0, inR.DangerousGetHandle());
            Marshal.WriteIntPtr(inherit, IntPtr.Size, outW.DangerousGetHandle());
            Marshal.WriteIntPtr(inherit, IntPtr.Size * 2, errW.DangerousGetHandle());
            var attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrs = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref attrSize)) { Marshal.FreeHGlobal(attrs); attrs = IntPtr.Zero; throw Win("InitializeProcThreadAttributeList"); }
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x20002 /*PROC_THREAD_ATTRIBUTE_HANDLE_LIST*/, inherit, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw Win("UpdateProcThreadAttribute(HANDLE_LIST)");
            si.lpAttributeList = attrs;
            // Nobody but SYSTEM may open the add-on's process or thread (no memory reads, handle duplication or injection).
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(ProcessSddl, 1, out processSd, IntPtr.Zero)) throw Win("ConvertStringSecurityDescriptorToSecurityDescriptor(process)");
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(ThreadSddl, 1, out threadSd, IntPtr.Zero)) throw Win("ConvertStringSecurityDescriptorToSecurityDescriptor(thread)");
            var pa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = processSd };
            var ta = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = threadSd };
            var block = new StringBuilder();
            foreach (var (k, v) in env.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) block.Append(k).Append('=').Append(v).Append('\0');
            block.Append('\0');
            var envPtr = Marshal.StringToHGlobalUni(block.ToString());
            try
            {
                const uint flags = 0x4 /*CREATE_SUSPENDED*/ | 0x08000000 /*CREATE_NO_WINDOW*/ | 0x400 /*CREATE_UNICODE_ENVIRONMENT*/ | 0x01000000 /*CREATE_BREAKAWAY_FROM_JOB*/ | 0x00080000 /*EXTENDED_STARTUPINFO_PRESENT*/;
                var cmd = new StringBuilder(commandLine);
                if (!CreateProcessAsUser(lowToken, null, cmd, ref pa, ref ta, true, flags, envPtr, workingDir, ref si, out pi))
                {
                    // Breakaway is only allowed when our own job permits it (e.g. not inside a restrictive CI job); retry without.
                    cmd = new StringBuilder(commandLine);
                    if (!CreateProcessAsUser(lowToken, null, cmd, ref pa, ref ta, true, flags & ~0x01000000u, envPtr, workingDir, ref si, out pi)) throw Win("CreateProcessAsUser");
                }
            }
            finally { Marshal.FreeHGlobal(envPtr); }

            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw Win("CreateJobObject");
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000 /*KILL_ON_JOB_CLOSE*/ | 0x100 /*PROCESS_MEMORY*/ | 0x8 /*ACTIVE_PROCESS*/ | 0x400 /*DIE_ON_UNHANDLED_EXCEPTION*/;
            info.BasicLimitInformation.ActiveProcessLimit = ProcessLimit;
            info.ProcessMemoryLimit = (UIntPtr)MemoryLimit;
            if (!SetInformationJobObject(job, 9 /*JobObjectExtendedLimitInformation*/, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) throw Win("SetInformationJobObject");
            if (!AssignProcessToJobObject(job, pi.hProcess)) { TerminateProcess(pi.hProcess, 1); throw Win("AssignProcessToJobObject"); }
            if (ResumeThread(pi.hThread) == uint.MaxValue) { TerminateProcess(pi.hProcess, 1); throw Win("ResumeThread"); }
            CloseHandle(pi.hThread);

            // Our copies of the child's ends must close, or reads never see end-of-file.
            inR.Dispose(); outW.Dispose(); errW.Dispose();
            var p = new SandboxProcess(pi.hProcess, job, pi.dwProcessId, new FileStream(inW, FileAccess.Write, 4096, false), new FileStream(outR, FileAccess.Read, 4096, false), new FileStream(errR, FileAccess.Read, 4096, false));
            job = IntPtr.Zero; pi.hProcess = IntPtr.Zero;
            return p;
        }
        catch
        {
            if (pi.hProcess != IntPtr.Zero) { TerminateProcess(pi.hProcess, 1); CloseHandle(pi.hProcess); }
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (job != IntPtr.Zero) CloseHandle(job);
            foreach (var h in new[] { inR, inW, outR, outW, errR, errW }) h?.Dispose();
            throw;
        }
        finally
        {
            if (sid != IntPtr.Zero) LocalFree(sid);
            if (attrs != IntPtr.Zero) { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
            if (inherit != IntPtr.Zero) Marshal.FreeHGlobal(inherit);
            if (processSd != IntPtr.Zero) LocalFree(processSd);
            if (threadSd != IntPtr.Zero) LocalFree(threadSd);
            if (lowToken != IntPtr.Zero) CloseHandle(lowToken);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static void Pipe(out SafeFileHandle read, out SafeFileHandle write, bool childReads)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out read, out write, ref sa, 0)) throw Win("CreatePipe");
        // Only the child's end may be inherited.
        if (!SetHandleInformation(childReads ? write : read, 1 /*HANDLE_FLAG_INHERIT*/, 0)) throw Win("SetHandleInformation");
    }

    public bool HasExited => WaitForSingleObject(_process, 0) == 0;

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (!HasExited)
        {
            if (DateTime.UtcNow > until || ct.IsCancellationRequested) return false;
            await Task.Delay(50, CancellationToken.None);
        }
        return true;
    }

    public int ExitCode => GetExitCodeProcess(_process, out var c) ? (int)c : -1;

    /// <summary>Protected DACL: SYSTEM full; everyone (and the owner, via OWNER RIGHTS) only SYNCHRONIZE + query information,
    /// which .NET inside the add-on needs on itself. No VM_READ / VM_WRITE / DUP_HANDLE / CREATE_THREAD for anyone but SYSTEM.</summary>
    public const string ProcessSddl = "D:P(A;;GA;;;SY)(A;;0x101400;;;WD)(A;;0x101400;;;OW)";
    public const string ThreadSddl = "D:P(A;;GA;;;SY)(A;;0x100840;;;WD)(A;;0x100840;;;OW)";

    /// <summary>Self-test: can another process of this user open the add-on to read its memory or duplicate its handles?</summary>
    public bool OthersCanOpen()
    {
        var h = OpenProcess(0x0010 /*VM_READ*/ | 0x0040 /*DUP_HANDLE*/ | 0x0020 /*VM_WRITE*/, false, Pid);
        if (h == IntPtr.Zero) return false;
        CloseHandle(h);
        return true;
    }

    /// <summary>Kills the program and everything it started (the whole job).</summary>
    public void Kill() { if (_job != IntPtr.Zero) TerminateJobObject(_job, 0xDC0); }

    /// <summary>The integrity level actually applied (for the self-test): the RID of the process token's label.</summary>
    public int IntegrityRid()
    {
        if (!OpenProcessToken(_process, TOKEN_QUERY, out var t)) return -1;
        try
        {
            GetTokenInformation(t, 25, IntPtr.Zero, 0, out var len);
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(t, 25, buf, len, out _)) return -1;
                var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buf);
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Label.Sid));
                return Marshal.ReadInt32(GetSidSubAuthority(label.Label.Sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(t); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StdIn.Dispose(); } catch { }
        try { StdOut.Dispose(); } catch { }
        try { StdErr.Dispose(); } catch { }
        Kill();
        if (_job != IntPtr.Zero) CloseHandle(_job);      // KILL_ON_JOB_CLOSE: nothing survives
        if (_process != IntPtr.Zero) CloseHandle(_process);
    }

    private static Win32Exception Win(string what) => new(Marshal.GetLastWin32Error(), what + " failed");

    // ---- interop ------------------------------------------------------------------------------------------------------

    private const uint TOKEN_DUPLICATE = 0x2, TOKEN_ASSIGN_PRIMARY = 0x1, TOKEN_QUERY = 0x8, TOKEN_ADJUST_DEFAULT = 0x80;

    [StructLayout(LayoutKind.Sequential)] private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSidToSid(string sid, out IntPtr psid);
    [DllImport("advapi32.dll")] private static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int cls, ref TOKEN_MANDATORY_LABEL info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int retLen);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? app, StringBuilder cmd, ref SECURITY_ATTRIBUTES pa, ref SECURITY_ATTRIBUTES ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, int revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle h, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
}
