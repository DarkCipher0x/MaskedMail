using System.Runtime.InteropServices;

// Every P/Invoke in MaskedMail targets a Windows system DLL (kernel32, user32, advapi32, shell32, dwmapi, wintrust), so
// native libraries are only ever resolved from System32 -- never from the application folder or the current directory,
// which closes off DLL planting. Linked into every project by Directory.Build.props (CA5392).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
