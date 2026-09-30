# Diagnóstico de elevação/privilégios do processo atual (por que AdjustTokenPrivileges dá 1300?)
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
Write-Output ("Identity       : " + $id.Name)
Write-Output ("Token type     : " + $id.Token)
Write-Output ("IsInRole(Admin): " + $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))

# TokenElevation via P/Invoke: a única resposta definitiva sobre UAC
$sig = @'
using System;
using System.Runtime.InteropServices;
public static class Elev {
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool GetTokenInformation(IntPtr tok, int cls, out TOKEN_ELEVATION buf, int len, out int ret);
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_ELEVATION { public int TokenIsElevated; }
    public static bool IsElevated() {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tok)) return false;
        TOKEN_ELEVATION e; int ret;
        bool ok = GetTokenInformation(tok, 20 /*TokenElevation*/, out e, Marshal.SizeOf(typeof(TOKEN_ELEVATION)), out ret);
        return ok && e.TokenIsElevated != 0;
    }
    public static int TokenElevationType() {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tok)) return -1;
        byte[] buf = new byte[4]; int ret;
        bool ok = GetTokenInformation(tok, 18 /*TokenElevationType*/, out buf, 4, out ret);
        return ok ? BitConverter.ToInt32(buf, 0) : -1;
    }
    // variante que devolve o bloco cru
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool GetTokenInformation(IntPtr tok, int cls, out byte[] buf, int len, out int ret);
}
'@
Add-Type -TypeDefinition $sig -Language CSharp
Write-Output ("TokenIsElevated: " + [Elev]::IsElevated())
Write-Output ("ElevationType  : " + [Elev]::TokenElevationType() + "  (1=Default 2=Full 3=Limited)")
