#Requires -Version 7.0
param([switch]$Force)

$ErrorActionPreference = "Stop"
$OutDir = Split-Path -Parent $PSCommandPath
$WorkDir = Join-Path $env:TEMP "alpine-build-$([System.IO.Path]::GetRandomFileName())"
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$Mirror = "http://dl-cdn.alpinelinux.org/alpine/v3.21/main/x86_64"

Write-Host "=== Baixando pacotes APK Alpine ===" -ForegroundColor Cyan

$Packages = @(
    @{ File = "busybox-static-1.37.0-r14.apk";         Out = "busybox.static";           Extract = "bin/busybox.static";               Dest = "bin/busybox" }
    @{ File = "ntfs-3g-progs-2026.2.25-r0.apk";        Out = "ntfsresize";                Extract = "usr/sbin/ntfsresize";              Dest = "usr/bin/ntfsresize" }
    @{ File = "ntfs-3g-progs-2026.2.25-r0.apk";        Out = "mkntfs";                    Extract = "usr/sbin/mkntfs";                  Dest = "sbin/mkfs.ntfs" }
    @{ File = "sfdisk-2.40.4-r1.apk";                  Out = "sfdisk";                    Extract = "sbin/sfdisk";                      Dest = "sbin/sfdisk" }
    @{ File = "util-linux-misc-2.40.4-r1.apk";         Out = "blockdev";                  Extract = "sbin/blockdev";                    Dest = "sbin/blockdev" }
    @{ File = "util-linux-misc-2.40.4-r1.apk";         Out = "switch_root";               Extract = "sbin/switch_root";                 Dest = "sbin/switch_root" }
    @{ File = "util-linux-misc-2.40.4-r1.apk";         Out = "pivot_root";                Extract = "sbin/pivot_root";                  Dest = "sbin/pivot_root" }
    @{ File = "dosfstools-4.2-r2.apk";                 Out = "mkfs.fat";                  Extract = "sbin/mkfs.fat";                    Dest = "sbin/mkfs.fat" }
    @{ File = "blkid-2.40.4-r1.apk";                   Out = "blkid";                     Extract = "sbin/blkid";                       Dest = "sbin/blkid" }
    # shared libs (needed for dynamically linked bins)
    @{ File = "musl-1.2.5-r11.apk";                    Out = "ld-musl-x86_64.so.1";       Extract = "lib/ld-musl-x86_64.so.1";          Dest = "lib/ld-musl-x86_64.so.1" }
    @{ File = "ntfs-3g-libs-2026.2.25-r0.apk";         Out = "libntfs-3g.so.89";          Extract = "usr/lib/libntfs-3g.so.89.0.0";     Dest = "lib/libntfs-3g.so.89" }
    @{ File = "libfdisk-2.40.4-r1.apk";                Out = "libfdisk.so.1";             Extract = "usr/lib/libfdisk.so.1.1.0";         Dest = "lib/libfdisk.so.1" }
    @{ File = "libncursesw-6.5_p20241006-r3.apk";      Out = "libncursesw.so.6";          Extract = "usr/lib/libncursesw.so.6.5";        Dest = "lib/libncursesw.so.6" }
    @{ File = "libeconf-0.6.3-r0.apk";                 Out = "libeconf.so.0";             Extract = "usr/lib/libeconf.so.0.6.2";         Dest = "lib/libeconf.so.0" }
    @{ File = "libblkid-2.40.4-r1.apk";                Out = "libblkid.so.1";             Extract = "usr/lib/libblkid.so.1.1.0";         Dest = "lib/libblkid.so.1" }
    @{ File = "libmount-2.40.4-r1.apk";                Out = "libmount.so.1";             Extract = "usr/lib/libmount.so.1.1.0";         Dest = "lib/libmount.so.1" }
    @{ File = "libuuid-2.40.4-r1.apk";                 Out = "libuuid.so.1";              Extract = "usr/lib/libuuid.so.1.3.0";          Dest = "lib/libuuid.so.1" }
    @{ File = "libsmartcols-2.40.4-r1.apk";            Out = "libsmartcols.so.1";         Extract = "usr/lib/libsmartcols.so.1.1.0";     Dest = "lib/libsmartcols.so.1" }
)

$InitDir = Join-Path $WorkDir "initramfs"
@("bin","sbin","usr/bin","usr/sbin","lib","dev") | ForEach-Object {
    New-Item -ItemType Directory -Path (Join-Path $InitDir $_) -Force | Out-Null
}

$ExtractDir = Join-Path $WorkDir "extract"

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(60)

function Extract-Apk {
    param($ApkPath, $InternalPath, $DestPath)
    $fullDest = Join-Path $InitDir $DestPath
    $destParent = Split-Path -Parent $fullDest
    New-Item -ItemType Directory -Path $destParent -Force | Out-Null

    if (-not (Test-Path $ApkPath)) { return $false }

    try {
        $gzStream = [System.IO.Compression.GZipStream]::new(
            [System.IO.File]::OpenRead($ApkPath),
            [System.IO.Compression.CompressionMode]::Decompress)
        $tar = [System.Formats.Tar.TarReader]::new($gzStream)
        while (($entry = $tar.GetNextEntry()) -ne $null) {
            if ($entry.Name -eq $InternalPath -or $entry.Name.EndsWith($InternalPath)) {
                $outStream = [System.IO.File]::Create($fullDest)
                $entry.DataStream.CopyTo($outStream)
                $outStream.Close()
                $tar.Dispose(); $gzStream.Dispose()
                return $true
            }
        }
        $tar.Dispose(); $gzStream.Dispose()
    } catch { return $false }
    return $false
}

foreach ($pkg in $Packages) {
    $url = "$Mirror/$($pkg.File)"
    $apkPath = Join-Path $WorkDir $pkg.File
    Write-Host "  $($pkg.File) -> $($pkg.Dest)" -ForegroundColor Gray

    if (-not (Test-Path $apkPath) -or $Force) {
        try {
            $data = $http.GetByteArrayAsync($url).Result
            [System.IO.File]::WriteAllBytes($apkPath, $data)
        } catch {
            Write-Warning "  FALHA ao baixar $($pkg.File): $_"
            continue
        }
    }

    if (-not (Extract-Apk -ApkPath $apkPath -InternalPath $pkg.Extract -DestPath $pkg.Dest)) {
        Write-Warning "  FALHA ao extrair $($pkg.Extract) de $($pkg.File)"
    } else {
        Write-Host "    OK" -ForegroundColor Green
    }
}

# symlinks em arquivo (cpio builder nao suporta symlinks, entao copiamos o conteudo)
$ldMusl = Join-Path $InitDir "lib/ld-musl-x86_64.so.1"
$libc = Join-Path $InitDir "lib/libc.musl-x86_64.so.1"
if ((Test-Path $ldMusl) -and (-not (Test-Path $libc))) {
    Copy-Item $ldMusl $libc -Force
    Write-Host "  libc.musl-x86_64.so.1 (symlink copy)" -ForegroundColor Green
}

# kernel Ubuntu (mainline 6.18 - EFI stub compatível com VMware)
Write-Host "Baixando kernel Ubuntu 6.18 mainline kernel.ubuntu.com..." -ForegroundColor Cyan
$kernelDebUrl = "https://kernel.ubuntu.com/mainline/v6.18/amd64/linux-image-unsigned-6.18.0-061800-generic_6.18.0-061800.202511302339_amd64.deb"
$kernelDeb = Join-Path $WorkDir "linux-image.deb"
$kernel7z = Join-Path $WorkDir "kernel-7z"
try {
    $data = $http.GetByteArrayAsync($kernelDebUrl).Result
    [System.IO.File]::WriteAllBytes($kernelDeb, $data)
    New-Item -ItemType Directory -Path $kernel7z -Force | Out-Null
    # Procura 7z em PATH ou locais comuns
    $7z = Get-Command "7z.exe" -ErrorAction SilentlyContinue
    if (-not $7z) { $7z = Get-Command "7z" -ErrorAction SilentlyContinue }
    if (-not $7z) {
        $commonPaths = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe")
        $7z = ($commonPaths | Where-Object { Test-Path $_ } | Select-Object -First 1)
        if ($7z) { $7z = (Get-Command $7z) }
    }
    if ($7z) {
        # 7z extrai .deb (ar) e depois data.tar.xz de uma vez
        & $7z.Source x "$kernelDeb" -o"$kernel7z" -y * 2>&1 | Out-Null
        & $7z.Source x "$kernel7z\data.tar.xz" -o"$kernel7z" -y * 2>&1 | Out-Null
        $vmlinuz = Get-ChildItem "$kernel7z\boot\vmlinuz-*" | Select-Object -First 1
        if ($vmlinuz) {
            Copy-Item $vmlinuz.FullName (Join-Path $OutDir "vmlinuz") -Force
            Write-Host "  Kernel Ubuntu OK ($($vmlinuz.Length) bytes)" -ForegroundColor Green
        } else {
            Write-Error "vmlinuz nao encontrado dentro do .deb"
            return
        }
    } else {
        Write-Warning "7z nao encontrado. Usando kernel de LinuxPreOS/ (lifeboat)..."
        $fallbackKernel = Join-Path (Split-Path -Parent (Split-Path -Parent $OutDir)) "LinuxPreOS\vmlinuz"
        if (Test-Path $fallbackKernel) {
            Copy-Item $fallbackKernel (Join-Path $OutDir "vmlinuz") -Force
            Write-Host "  Kernel LinuxPreOS OK ($((Get-Item $fallbackKernel).Length) bytes)" -ForegroundColor Green
        } else {
            Write-Error "7z nao encontrado e LinuxPreOS/vmlinuz ausente. Execute build.sh em Linux primeiro."
            return
        }
    }
} catch {
    Write-Warning "FALHA ao baixar/extrair kernel Ubuntu ($_). Tentando LinuxPreOS/..."
    $fallbackKernel = Join-Path (Split-Path -Parent (Split-Path -Parent $OutDir)) "LinuxPreOS\vmlinuz"
    if (Test-Path $fallbackKernel) {
        Copy-Item $fallbackKernel (Join-Path $OutDir "vmlinuz") -Force
        Write-Host "  Kernel LinuxPreOS OK ($((Get-Item $fallbackKernel).Length) bytes)" -ForegroundColor Green
    } else {
        Write-Error "Kernel Ubuntu nao disponivel. Execute build.sh em Linux primeiro."
        return
    }
}

# init script
Write-Host "Gerando init script..." -ForegroundColor Cyan
$initScript = @'
#!/bin/busybox sh
BB=/bin/busybox
export PATH=/bin:/sbin:/usr/bin:/usr/sbin
$BB mount -t proc none /proc
$BB mount -t sysfs none /sys
$BB mount -t devtmpfs devtmpfs /dev 2>/dev/null || $BB mount -t tmpfs none /dev
$BB mount -t tmpfs none /tmp
$BB mkdir -p /usr/bin /usr/sbin /mnt
$BB ln -s $BB /bin/sh 2>/dev/null
echo '==========================================='
echo '  KitLugia Alpine Emergency Pre-Boot'
echo '==========================================='
CMDLINE=$($BB cat /proc/cmdline)
DISK=""; PART=""; SHRINK_MB=""; LABEL=""; DL=""
for x in $CMDLINE; do
    case $x in kitlugia_disk=*) DISK="${x#kitlugia_disk=}" ;; esac
    case $x in kitlugia_part=*) PART="${x#kitlugia_part=}" ;; esac
    case $x in kitlugia_shrink_mb=*) SHRINK_MB="${x#kitlugia_shrink_mb=}" ;; esac
    case $x in kitlugia_label=*) LABEL="${x#kitlugia_label=}" ;; esac
    case $x in kitlugia_dl=*) DL="${x#kitlugia_dl=}" ;; esac
done
echo "Disk=$DISK Part=$PART Shrink=${SHRINK_MB}MB Label=$LABEL"

DISK_DEV=""; idx=0; target_idx=${DISK#PHYSICALDRIVE}
for d in /dev/sd? /dev/nvme?n? /dev/mmcblk? /dev/vd? /dev/xvd? /dev/nvme??n?; do
    [ -b "$d" ] && [ "$idx" = "$target_idx" ] && { DISK_DEV="$d"; break; }
    [ -b "$d" ] && idx=$((idx + 1))
done

[ -z "$DISK_DEV" ] && { echo "ERRO: Disco $DISK nao encontrado"; $BB lsblk -d; $BB sleep 10; $BB reboot -f; }

echo "Disco detectado: $DISK_DEV (PHYSICALDRIVE${target_idx})"

PART_DEV="${DISK_DEV}${PART}"
case "$DISK_DEV" in
    *nvme*|*mmcblk*) PART_DEV="${DISK_DEV}p${PART}" ;;
esac
[ ! -b "$PART_DEV" ] && PART_DEV="${DISK_DEV}${PART}"
[ ! -b "$PART_DEV" ] && { echo "ERRO: $PART_DEV nao encontrado"; $BB lsblk "$DISK_DEV"; $BB sleep 10; $BB reboot -f; }

echo "Particao alvo: $PART_DEV"

REDUZIR_BYTES=$(( SHRINK_MB * 1024 * 1024 ))
PART_SIZE_BYTES=$(/sbin/blockdev --getsize64 "$PART_DEV" 2>/dev/null)
[ -z "$PART_SIZE_BYTES" ] && PART_SIZE_BYTES=$($BB stat -c%s "$PART_DEV" 2>/dev/null)
[ -z "$PART_SIZE_BYTES" ] && { echo "ERRO: Nao foi possivel obter tamanho"; $BB sleep 10; $BB reboot -f; }

NEW_SIZE_BYTES=$(( PART_SIZE_BYTES - REDUZIR_BYTES ))
[ "$NEW_SIZE_BYTES" -le 0 ] && { echo "ERRO: Tamanho final negativo!"; $BB sleep 10; $BB reboot -f; }

echo "Tamanho: $PART_SIZE_BYTES bytes -> $NEW_SIZE_BYTES bytes"

echo "[1/3] Reduzindo NTFS..."
/usr/bin/ntfsresize --force --no-action "$PART_DEV" 2>&1 || true
/usr/bin/ntfsresize --force --size "$NEW_SIZE_BYTES" "$PART_DEV" 2>&1 || \
/usr/bin/ntfsresize --force -b --size "$NEW_SIZE_BYTES" "$PART_DEV" 2>&1 || \
{ echo "FALHA ntfsresize"; $BB sleep 10; $BB reboot -f; }
echo "NTFS reduzido."

NEW_SECTORS=$(( NEW_SIZE_BYTES / 512 ))
echo "[2/3] Redimensionando particao com sfdisk..."
echo ",$NEW_SECTORS" | /sbin/sfdisk --force --no-reread -N "$PART" "$DISK_DEV" 2>&1 || true

echo "[3/3] Criando nova particao com sfdisk..."
echo "type=7" | /sbin/sfdisk --force --no-reread --append "$DISK_DEV" 2>&1 || true

$BB sleep 2

NEW_PART=$(/sbin/sfdisk -l -o Device "$DISK_DEV" 2>/dev/null | $BB tail -1)
[ -n "$NEW_PART" ] && [ -b "$NEW_PART" ] && {
    echo "Formatando $NEW_PART..."
    /sbin/mkfs.ntfs -f -L "${LABEL:-KITLUGIA}" "$NEW_PART" 2>&1 || \
    /sbin/mkfs.fat -F32 -n "${LABEL:-KITLUGIA}" "$NEW_PART" 2>&1 || true
}

echo "=== Escrevendo marcador no ESP ==="
for esp_mnt in /mnt/esp1 /mnt/esp2; do
    $BB mkdir -p "$esp_mnt"
    for esp_dev in $(/sbin/blkid -t TYPE=vfat -o device 2>/dev/null); do
        $BB mount "$esp_dev" "$esp_mnt" 2>/dev/null && {
            $BB mkdir -p "$esp_mnt/EFI/KitLugia"
            echo "done" > "$esp_mnt/EFI/KitLugia/preboot_complete.txt"
            $BB umount "$esp_mnt" 2>/dev/null
            echo "  Marcador escrito!"
            break 2
        }
    done
done

echo "=== OPERACAO CONCLUIDA. Reiniciando em 5 segundos ==="
$BB sleep 5
$BB reboot -f
'@

Set-Content -Path (Join-Path $InitDir "init") -Value $initScript -Encoding ASCII

# Symlinks criados dinamicamente pelo init script via 'busybox ln -s'

# cpio archive builder - pure .NET
Write-Host "Criando initramfs (cpio + gzip)..." -ForegroundColor Cyan
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

public static class CpioBuilder
{
    public static byte[] BuildCpio(string rootDir)
    {
        var files = new List<string>();
        CollectFiles(rootDir, "", files);
        files.Sort(StringComparer.Ordinal);

        using var ms = new MemoryStream();
        uint inode = 1;
        foreach (var relPath in files)
        {
            string fullPath = Path.Combine(rootDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            bool isDir = Directory.Exists(fullPath);
            bool isSym = false;

            byte[] data;
            uint mode;
            if (isDir)
            {
                data = Array.Empty<byte>();
                mode = 0x41ED; // directory
            }
            else
            {
                data = File.ReadAllBytes(fullPath);
                mode = 0x81A4; // regular file 755
            }

            WriteEntry(ms, relPath, mode, (ulong)data.Length, inode++, data, isSym);
        }
        // TRAILER
        WriteEntry(ms, "TRAILER!!!", 0, 0, inode++, Array.Empty<byte>(), false);
        return ms.ToArray();
    }

    static void CollectFiles(string rootDir, string prefix, List<string> files)
    {
        var dir = new DirectoryInfo(rootDir);
        foreach (var f in dir.GetFiles())
        {
            string rp = (prefix + "/" + f.Name).TrimStart('/');
            files.Add(rp);
        }
        foreach (var d in dir.GetDirectories())
        {
            string rp = (prefix + "/" + d.Name).TrimStart('/');
            files.Add(rp + "/");
            CollectFiles(d.FullName, rp, files);
        }
    }

    static void WriteEntry(MemoryStream ms, string name, uint mode, ulong size, uint inode, byte[] data, bool isSymlink)
    {
        uint namesize = (uint)Encoding.ASCII.GetByteCount(name) + 1;
        uint padName = (namesize + 3) & ~3u;
        ulong padData = (size + 3) & ~3UL;

        string header =
            "070701" +
            ToHex8(inode) +
            ToHex8(mode) +
            "00000000" + // uid
            "00000000" + // gid
            "00000001" + // nlink
            "00000000" + // mtime
            ToHex8(size) +
            "00000000" + // devmajor
            "00000000" + // devminor
            "00000000" + // rdevmajor
            "00000000" + // rdevminor
            ToHex8(namesize) +
            "00000000";  // check

        byte[] hdr = Encoding.ASCII.GetBytes(header);
        ms.Write(hdr, 0, hdr.Length);
        byte[] nameB = Encoding.ASCII.GetBytes(name);
        ms.Write(nameB, 0, nameB.Length);
        ms.WriteByte(0);
        for (uint i = namesize; i < padName; i++) ms.WriteByte(0);
        if (data.Length > 0)
            ms.Write(data, 0, data.Length);
        for (ulong i = size; i < padData; i++) ms.WriteByte(0);
    }

    static string ToHex8(uint v) => v.ToString("x8");
    static string ToHex8(ulong v) => v.ToString("x8");
}
'@

$cpioData = [CpioBuilder]::BuildCpio($InitDir)
$gzPath = Join-Path $WorkDir "initrd.gz"
$gzStream = [System.IO.File]::Create($gzPath)
$gz = [System.IO.Compression.GZipStream]::new($gzStream, [System.IO.Compression.CompressionLevel]::Optimal)
$gz.Write($cpioData, 0, $cpioData.Length)
$gz.Close()
$gzStream.Close()

Copy-Item $gzPath (Join-Path $OutDir "initrd.gz") -Force

Write-Host "=== BUILD CONCLUIDO ===" -ForegroundColor Green
$vmlinuzSize = (Get-Item (Join-Path $OutDir "vmlinuz")).Length
$initrdSize = (Get-Item (Join-Path $OutDir "initrd.gz")).Length
Write-Host "vmlinuz: $([math]::Round($vmlinuzSize/1KB, 1)) KB" -ForegroundColor Yellow
Write-Host "initrd.gz: $([math]::Round($initrdSize/1KB, 1)) KB" -ForegroundColor Yellow
Write-Host "Total: $([math]::Round(($vmlinuzSize+$initrdSize)/1KB, 1)) KB" -ForegroundColor Yellow

$http.Dispose()
Remove-Item -Path $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
