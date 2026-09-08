# KitLugia - Desktop Icon Layout backup/restore (standalone, DesktopOK-style)
# Save:   powershell -File DesktopIconLayout.ps1 -OutFile <json>
# Restore: powershell -File DesktopIconLayout.ps1 -RestoreFile <json>
# Read-only capture of positions from the live desktop ListView (Progman/WorkerW/SysListView32).
# Restore maps icons by NAME so it survives re-sorting after a shell restart.

param(
    [string]$OutFile,
    [string]$RestoreFile
)

$ErrorActionPreference = 'Stop'

$src = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskIcons
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LVITEMW
    {
        public uint mask; public int iItem; public int iSubItem; public uint state; public uint stateMask;
        public IntPtr pszText; public int cchTextMax; public int iImage; public IntPtr lParam;
        public int iIndent; public int iGroupId; public uint cColumns; public IntPtr puColumns; public IntPtr piColFmt; public int iGroup;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    private delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int written);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint LVM_GETITEMCOUNT = 0x1004;
    private const uint LVM_GETITEMPOSITION = 0x1010;
    private const uint LVM_GETITEMTEXTW = 0x1073;
    private const uint LVM_SETITEMPOSITION = 0x100F;

    // Finds the desktop ListView: Progman > SHELLDLL_DefView > SysListView32.
    // With wallpaper slideshows the DefView moves to a WorkerW window - covered below.
    private static IntPtr FindDesktopList()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() == "WorkerW")
                {
                    IntPtr dv = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero) { defView = dv; return false; }
                }
                return true;
            }, IntPtr.Zero);
        }
        if (defView == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private static byte[] StructBytes(object o)
    {
        int n = Marshal.SizeOf(o);
        byte[] b = new byte[n];
        IntPtr p = Marshal.AllocHGlobal(n);
        try { Marshal.StructureToPtr(o, p, false); Marshal.Copy(p, b, 0, n); }
        finally { Marshal.FreeHGlobal(p); }
        return b;
    }

    private static string ReadItemName(IntPtr hp, IntPtr remoteItem, int index, int structSize)
    {
        LVITEMW lvi = new LVITEMW();
        lvi.mask = 1; lvi.iItem = index; lvi.iSubItem = 0; lvi.cchTextMax = 512;
        lvi.pszText = (IntPtr)((long)remoteItem + structSize);
        byte[] sb2 = StructBytes(lvi);
        int wr, rd;
        WriteProcessMemory(hp, remoteItem, sb2, sb2.Length, out wr);
        SendMessage(listHwnd, LVM_GETITEMTEXTW, (IntPtr)index, remoteItem);
        byte[] ib = new byte[structSize];
        ReadProcessMemory(hp, remoteItem, ib, structSize, out rd);
        long ptr = BitConverter.ToInt64(ib, Marshal.OffsetOf(typeof(LVITEMW), "pszText").ToInt32());
        byte[] tb = new byte[1024];
        ReadProcessMemory(hp, (IntPtr)ptr, tb, 1024, out rd);
        string s = Encoding.Unicode.GetString(tb);
        int z = s.IndexOf('\0');          // corta no primeiro NUL - o buffer remoto guarda lixo de nomes anteriores
        if (z >= 0) s = s.Substring(0, z);
        return s;
    }

    private static IntPtr listHwnd;

    private static bool EnsureProcess()
    {
        listHwnd = FindDesktopList();
        return listHwnd != IntPtr.Zero;
    }

    // Returns lines in the form: "x,y|name" (one per icon)
    public static List<string> Capture()
    {
        List<string> result = new List<string>();
        if (!EnsureProcess()) { result.Add("ERROR|desktop SysListView32 nao encontrado"); return result; }
        uint pid;
        GetWindowThreadProcessId(listHwnd, out pid);
        IntPtr hp = OpenProcess(0x38, false, pid);
        if (hp == IntPtr.Zero) { result.Add("ERROR|OpenProcess falhou err=" + Marshal.GetLastWin32Error()); return result; }
        try
        {
            int count = (int)SendMessage(listHwnd, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            int structSize = Marshal.SizeOf(typeof(LVITEMW));
            IntPtr remoteItem = VirtualAllocEx(hp, IntPtr.Zero, (UIntPtr)(structSize + 2048), 0x3000, 4);
            IntPtr remotePos = VirtualAllocEx(hp, IntPtr.Zero, (UIntPtr)16, 0x3000, 4);
            int rd;
            for (int i = 0; i < count; i++)
            {
                SendMessage(listHwnd, LVM_GETITEMPOSITION, (IntPtr)i, remotePos);
                byte[] pb = new byte[8];
                ReadProcessMemory(hp, remotePos, pb, 8, out rd);
                int x = BitConverter.ToInt32(pb, 0);
                int y = BitConverter.ToInt32(pb, 4);
                string name = ReadItemName(hp, remoteItem, i, structSize);
                result.Add(x + "," + y + "|" + name);
            }
            VirtualFreeEx(hp, remoteItem, UIntPtr.Zero, 0x8000);
            VirtualFreeEx(hp, remotePos, UIntPtr.Zero, 0x8000);
        }
        finally { CloseHandle(hp); }
        return result;
    }

    // Restores positions by icon NAME. Returns number of icons positioned (-1 on error).
    public static int Restore(List<string> items)
    {
        if (!EnsureProcess()) return -1;
        uint pid;
        GetWindowThreadProcessId(listHwnd, out pid);
        IntPtr hp = OpenProcess(0x38, false, pid);
        if (hp == IntPtr.Zero) return -1;
        Dictionary<string, int> nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int count = (int)SendMessage(listHwnd, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            int structSize = Marshal.SizeOf(typeof(LVITEMW));
            IntPtr remoteItem = VirtualAllocEx(hp, IntPtr.Zero, (UIntPtr)(structSize + 2048), 0x3000, 4);
            for (int i = 0; i < count; i++)
            {
                string name = ReadItemName(hp, remoteItem, i, structSize);
                if (!nameToIndex.ContainsKey(name)) nameToIndex[name] = i;
            }
            VirtualFreeEx(hp, remoteItem, UIntPtr.Zero, 0x8000);
        }
        finally { CloseHandle(hp); }

        int applied = 0;
        foreach (string it in items)
        {
            string[] parts = it.Split(new char[] { '|' }, 2);
            if (parts.Length < 2) continue;
            string[] xy = parts[0].Split(',');
            int x, y;
            if (xy.Length < 2) continue;
            if (!int.TryParse(xy[0], out x) || !int.TryParse(xy[1], out y)) continue;
            int idx;
            if (!nameToIndex.TryGetValue(parts[1], out idx)) continue;
            SendMessage(listHwnd, LVM_SETITEMPOSITION, (IntPtr)idx, (IntPtr)((y << 16) | (x & 0xFFFF)));
            applied++;
        }
        return applied;
    }
}
'@

Add-Type -TypeDefinition $src | Out-Null

if ($OutFile) {
    $items = [DeskIcons]::Capture()
    if ($items.Count -gt 0 -and $items[0].StartsWith('ERROR')) {
        Write-Output $items[0]
        exit 2
    }
    $dir = Split-Path $OutFile -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllLines($OutFile, $items)
    Write-Output ("SAVED {0} icones -> {1}" -f $items.Count, $OutFile)
    exit 0
}

if ($RestoreFile) {
    $items = @([System.IO.File]::ReadAllLines($RestoreFile) | Where-Object { $_ -match '^-?\d+,-?\d+\|' })
    $n = [DeskIcons]::Restore($items)
    if ($n -lt 0) { Write-Output "RESTORE FALHOU (listview nao encontrado)"; exit 2 }
    Write-Output ("RESTAURADOS {0} de {1} icones" -f $n, $items.Count)
    exit 0
}

Write-Output "Uso: -OutFile <json> para salvar | -RestoreFile <json> para restaurar"
