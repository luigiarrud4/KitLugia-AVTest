<#
.SYNOPSIS
    Reproduz a decisao de purge do ISLC (Intelligent Standby List Cleaner) e mede
    com que frequencia ela dispararia nesta maquina.

.DESCRIPTION
    O ISLC 1.0.4.8 mede a standby list e a "free memory" com
    NtQuerySystemInformation(SystemMemoryListInformation = 80) e decide purgar com
    DUAS condicoes:

        standby >= StandbyListValue (MB)  E  free < FreeMemoryValue (MB)

    Onde:
      standby = soma dos 8 buckets PageCountByPriority (paginas * 4096 / 1 MB)
      free    = ZeroPageCount + FreePageCount  (NAO inclui standby; e o "Livre" do
                Gerenciador de Tarefas, nao o "Disponivel")

    Este script le exatamente a mesma estrutura, aplica a mesma formula e reporta
    quantos ticks disparariam o purge. Tambem le a resolucao do timer
    (NtQueryTimerResolution) e a chave GlobalTimerResolutionRequests, que o ISLC
    exibe na aba Settings.

.PARAMETER StandbyListValue
    Tamanho da standby list (MB) a partir do qual o purge e considerado. Default 1024
    (o default do config.json do ISLC).

.PARAMETER FreeMemoryValue
    Limite de memoria livre (MB); o purge exige free ABAIXO disso. Default 1024.

.PARAMETER Samples
    Quantidade de amostras. Default 15.

.PARAMETER IntervalMs
    Intervalo entre amostras em ms. Default 1000 (o polling default do ISLC).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\islc_gate_probe.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\islc_gate_probe.ps1 -Samples 60 -IntervalMs 500
#>
param(
    [long]$StandbyListValue = 1024,
    [long]$FreeMemoryValue = 1024,
    [int]$Samples = 15,
    [int]$IntervalMs = 1000
)

$src = @"
using System;
using System.Runtime.InteropServices;

public static class IslcGate
{
    // Mesma estrutura do ISLC (MainWindowViewModel.SYSTEM_MEMORY_LIST_INFORMATION).
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public UIntPtr ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount;
        public UIntPtr P0, P1, P2, P3, P4, P5, P6, P7;
        public UIntPtr R0, R1, R2, R3, R4, R5, R6, R7;
        public UIntPtr ModifiedPageCountPageFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, IntPtr buf, uint len, out uint ret);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryTimerResolution(out uint min, out uint max, out uint cur);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    // Devolve: standbyMB, freeMB (Zero+Free), repurposedMB, availMB, totalMB
    public static long[] Sample()
    {
        var m = new MEMORYSTATUSEX(); m.dwLength = (uint)Marshal.SizeOf(m);
        GlobalMemoryStatusEx(ref m);

        int size = Marshal.SizeOf(typeof(SYSTEM_MEMORY_LIST_INFORMATION));
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            uint ret;
            int st = NtQuerySystemInformation(80, p, (uint)size, out ret);
            if (st != 0) throw new Exception("NtQuerySystemInformation status=" + st);
            var i = (SYSTEM_MEMORY_LIST_INFORMATION)Marshal.PtrToStructure(p, typeof(SYSTEM_MEMORY_LIST_INFORMATION));
            ulong sum = (ulong)i.P0 + (ulong)i.P1 + (ulong)i.P2 + (ulong)i.P3
                      + (ulong)i.P4 + (ulong)i.P5 + (ulong)i.P6 + (ulong)i.P7;
            ulong rep = (ulong)i.R0 + (ulong)i.R1 + (ulong)i.R2 + (ulong)i.R3
                      + (ulong)i.R4 + (ulong)i.R5 + (ulong)i.R6 + (ulong)i.R7;
            return new long[]
            {
                (long)(sum * 4096 >> 20),
                (long)(((ulong)i.ZeroPageCount + (ulong)i.FreePageCount) * 4096 >> 20),
                (long)(rep * 4096 >> 20),
                (long)(m.ullAvailPhys / 1048576),
                (long)(m.ullTotalPhys / 1048576)
            };
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public static string TimerRes()
    {
        uint min, max, cur;
        if (NtQueryTimerResolution(out min, out max, out cur) != 0) return "indisponivel";
        return string.Format("atual={0:F3}ms min={1:F3}ms max={2:F3}ms", cur / 10000.0, max / 10000.0, min / 10000.0);
    }
}
"@

Add-Type -TypeDefinition $src -Language CSharp

function Get-GlobalTimerResolutionRequests {
    try {
        $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\kernel', $false)
        if ($null -eq $k) { return 'chave ausente' }
        $v = $k.GetValue('GlobalTimerResolutionRequests')
        $k.Close()
        if ($null -eq $v) { return 'nao definido (0 implicito)' }
        return "$v"
    } catch { return "erro: $($_.Exception.Message)" }
}

Write-Output "ISLC gate probe - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Output "condicoes: standby >= $StandbyListValue MB  E  free(Zero+Free) < $FreeMemoryValue MB"
Write-Output "timer resolution: $([IslcGate]::TimerRes())"
Write-Output "GlobalTimerResolutionRequests: $(Get-GlobalTimerResolutionRequests)"
Write-Output ''

$hits = 0
$standbyMin = [long]::MaxValue; $standbyMax = 0
$freeMin = [long]::MaxValue; $freeMax = 0
$availMin = [long]::MaxValue
$standbySum = 0L

for ($n = 1; $n -le $Samples; $n++) {
    $s = [IslcGate]::Sample()
    $standby = $s[0]; $free = $s[1]; $rep = $s[2]; $avail = $s[3]; $total = $s[4]

    if ($standby -ge $StandbyListValue -and $free -lt $FreeMemoryValue) { $hit = 'PURGE'; $hits++ } else { $hit = '-' }

    if ($standby -lt $standbyMin) { $standbyMin = $standby }
    if ($standby -gt $standbyMax) { $standbyMax = $standby }
    if ($free -lt $freeMin) { $freeMin = $free }
    if ($free -gt $freeMax) { $freeMax = $free }
    if ($avail -lt $availMin) { $availMin = $avail }
    $standbySum += $standby

    Write-Output ("[{0,3}] standby={1,6} MB  free={2,5} MB  repurposed={3,4} MB  avail={4,6} MB  total={5,5} MB  -> {6}" -f `
            $n, $standby, $free, $rep, $avail, $total, $hit)

    if ($n -lt $Samples) { Start-Sleep -Milliseconds $IntervalMs }
}

$pct = if ($Samples -gt 0) { [math]::Round(100.0 * $hits / $Samples, 1) } else { 0 }
Write-Output ''
Write-Output "resumo: $hits/$Samples amostras disparariam o purge ($pct%)"
Write-Output ("standby: min={0} max={1} media={2} MB | free: min={3} max={4} MB | avail min={5} MB" -f `
        $standbyMin, $standbyMax, [math]::Round($standbySum / [double]$Samples, 1), $freeMin, $freeMax, $availMin)

if ($hits -eq $Samples) {
    Write-Output 'nota: 100% dos ticks com o toggle ligado = purge continuo a cada polling.'
} elseif ($hits -eq 0) {
    Write-Output 'nota: nenhuma condicao atendida nas amostras - purge nao dispararia.'
}
