# Lista os privilégios do token do processo atual (GetTokenInformation/TokenPrivileges).
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class TokenPrivs
{
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeName(string sys, ref long luid, StringBuilder name, ref int size);

    public static List<string> List()
    {
        var res = new List<string>();
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tok)) { res.Add("OpenProcessToken falhou"); return res; }

        int len;
        GetTokenInformation(tok, 3 /*TokenPrivileges*/, IntPtr.Zero, 0, out len);
        if (len <= 0) { res.Add("GetTokenInformation(len) falhou " + Marshal.GetLastWin32Error()); return res; }

        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(tok, 3, buf, len, out len))
            {
                res.Add("GetTokenInformation falhou " + Marshal.GetLastWin32Error());
                return res;
            }
            int count = Marshal.ReadInt32(buf);
            IntPtr p = (IntPtr)(buf.ToInt64() + 4);
            for (int i = 0; i < count; i++)
            {
                long luid = Marshal.ReadInt64(p);
                int attrs = Marshal.ReadInt32((IntPtr)(p.ToInt64() + 8));
                var sb = new StringBuilder(128); int sz = 128;
                string name = LookupPrivilegeName(null, ref luid, sb, ref sz) ? sb.ToString() : "?";
                res.Add(name + " attrs=0x" + attrs.ToString("X2"));
                p = (IntPtr)(p.ToInt64() + 12);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return res;
    }
}
'@
$all = [TokenPrivs]::List()
Write-Output ("total de privilegios no token: " + $all.Count)
$all | Where-Object { $_ -match 'Se(Debug|TakeOwnership|Backup|Restore|LoadDriver|Shutdown)Privilege' }
