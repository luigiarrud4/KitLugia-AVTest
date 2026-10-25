// Sonda: por que OpenProcess(PROCESS_SUSPEND_RESUME) falha?
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr h);

    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED = 0x1000;

    private static int Main()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var p = Process.Start(new ProcessStartInfo(ping, "-t 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        Thread.Sleep(1200);
        Console.WriteLine($"filho PID {p.Id}");

        foreach (uint mask in new[] { PROCESS_SUSPEND_RESUME, PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED,
                                     PROCESS_SET_INFORMATION, PROCESS_SUSPEND_RESUME | 0x000F })
        {
            IntPtr h = OpenProcess(mask, false, p.Id);
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"  access=0x{mask:X4} -> handle={(h == IntPtr.Zero ? "NULL" : "ok")} erro={err}" +
                              (err == 5 ? " (ACCESS_DENIED)" : err == 87 ? " (INVALID_PARAMETER)" : ""));
            if (h != IntPtr.Zero)
            {
                int nt = NtSuspendProcess(h);
                Console.WriteLine($"      NtSuspendProcess = 0x{nt:X8}");
                CloseHandle(h);
            }
        }

        try { if (!p.HasExited) p.Kill(); } catch { }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
