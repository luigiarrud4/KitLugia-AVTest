$ErrorActionPreference = 'Stop'

$oldDll = "C:\Users\Lugia\KitLugia\KitLugia.GUI\bin\Debug\net10.0-windows10.0.26100.0\rust_native.dll.bak"
$newDll = "C:\Users\Lugia\KitLugia\rust_native\target\release\rust_native.dll"
$tmp = "C:\Users\Lugia\KitLugia\mft_cmp"
New-Item -ItemType Directory -Force $tmp | Out-Null
Copy-Item $oldDll "$tmp\mft_old.dll" -Force
Copy-Item $newDll "$tmp\mft_new.dll" -Force

$src = @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    [DllImport("kernel32")] public static extern bool FreeLibrary(IntPtr hModule);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MftScan(IntPtr volumeRoot, IntPtr prefixes, IntPtr outBuf, int outCapacity);
}
'@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Runtime.InteropServices

function Invoke-MftScan([string]$dllPath, [string]$volume, [string]$prefixes) {
    $h = [Native]::LoadLibrary($dllPath)
    if ($h -eq [IntPtr]::Zero) { throw "LoadLibrary falhou: $dllPath (err $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))" }
    $proc = [Native]::GetProcAddress($h, "mft_scan_ffi")
    if ($proc -eq [IntPtr]::Zero) { throw "GetProcAddress falhou" }
    $del = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($proc, [type][Native+MftScan])

    # UTF-16 NUL-terminated
    $volArr = [char[]]($volume.ToCharArray() + [char]0)
    $preArr = [char[]]($prefixes.ToCharArray() + [char]0)
    $volH = [Runtime.InteropServices.Marshal]::AllocHGlobal($volArr.Length * 2)
    $preH = [Runtime.InteropServices.Marshal]::AllocHGlobal($preArr.Length * 2)
    [Runtime.InteropServices.Marshal]::Copy($volArr, 0, $volH, $volArr.Length)
    [Runtime.InteropServices.Marshal]::Copy($preArr, 0, $preH, $preArr.Length)

    # pass 1: small buffer -> needed
    $tiny = [Runtime.InteropServices.Marshal]::AllocHGlobal(16)
    $rc1 = $del.Invoke($volH, $preH, $tiny, 16)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($tiny)
    if ($rc1 -le -1000) { $needed = -$rc1 - 1000 } else { $needed = $rc1 }
    if ($needed -le 0) { [Native]::FreeLibrary($h); return @{ rc = $rc1; entries = @(); raw = $null } }

    $buf = [Runtime.InteropServices.Marshal]::AllocHGlobal($needed + 4096)
    $rc = $del.Invoke($volH, $preH, $buf, $needed)

    $entries = @()
    if ($rc -gt 0) {
        $br = New-Object byte[] ($rc)
        [Runtime.InteropServices.Marshal]::Copy($buf, $br, 0, $rc)
        $ms = New-Object IO.MemoryStream(,$br)
        $rd = New-Object IO.BinaryReader($ms)
        $pc = $rd.ReadUInt32()
        $prefixRecs = for ($i=0; $i -lt $pc; $i++) { $rd.ReadUInt32() }
        while ($ms.Position + 36 -le $rc) {
            $rec = $rd.ReadUInt64(); $parent = $rd.ReadUInt64(); $size = $rd.ReadUInt64()
            $lw = $rd.ReadUInt64(); $fl = $rd.ReadUInt16(); $nl = $rd.ReadUInt16()
            $nameBytes = $rd.ReadBytes($nl * 2)
            $name = [Text.Encoding]::Unicode.GetString($nameBytes)
            $entries += [pscustomobject]@{ rec=$rec; parent=$parent; size=$size; lw=$lw; flags=$fl; name=$name }
        }
        $ms.Close()
    }
    [Runtime.InteropServices.Marshal]::FreeHGlobal($buf)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($volH)
    [Runtime.InteropServices.Marshal]::FreeHGlobal($preH)
    [Native]::FreeLibrary($h)
    return @{ rc = $rc; entries = $entries; prefixRecs = $prefixRecs }
}

$volume = "C:"
$prefixes = "Users/Lugia/Desktop"

Write-Host "=== OLD dll ==="
$old = Invoke-MftScan "$tmp\mft_old.dll" $volume $prefixes
Write-Host "rc=$($old.rc) entries=$($old.entries.Count) prefixRecs=$($old.prefixRecs -join ',')"

Write-Host "=== NEW dll ==="
$new = Invoke-MftScan "$tmp\mft_new.dll" $volume $prefixes
Write-Host "rc=$($new.rc) entries=$($new.entries.Count) prefixRecs=$($new.prefixRecs -join ',')"

# diff por rec
$oldMap = @{}; foreach ($e in $old.entries) { $oldMap[$e.rec] = $e }
$newMap = @{}; foreach ($e in $new.entries) { $newMap[$e.rec] = $e }
$onlyOld = $oldMap.Keys | Where-Object { -not $newMap.ContainsKey($_) }
$onlyNew = $newMap.Keys | Where-Object { -not $oldMap.ContainsKey($_) }
$diff = 0; $diffSamples = @()
foreach ($k in $oldMap.Keys) {
    if ($newMap.ContainsKey($k)) {
        $a = $oldMap[$k]; $b = $newMap[$k]
        if ($a.name -ne $b.name -or $a.parent -ne $b.parent -or $a.size -ne $b.size -or $a.flags -ne $b.flags -or $a.lw -ne $b.lw) {
            $diff++
            if ($diffSamples.Count -lt 15) { $diffSamples += "rec=$k OLD(name='$($a.name)' parent=$($a.parent) size=$($a.size) flags=$($a.flags)) NEW(name='$($b.name)' parent=$($b.parent) size=$($b.size) flags=$($b.flags))" }
        }
    }
}
Write-Host "onlyOld=$($onlyOld.Count) onlyNew=$($onlyNew.Count) different=$diff"
$diffSamples | ForEach-Object { Write-Host $_ }
$onlyOld | Select-Object -First 10 | ForEach-Object { $e=$oldMap[$_]; Write-Host "SO'' OLD rec=$($_) name='$($e.name)' parent=$($e.parent)" }
$onlyNew | Select-Object -First 10 | ForEach-Object { $e=$newMap[$_]; Write-Host "SO'' NEW rec=$($_) name='$($e.name)' parent=$($e.parent)" }
