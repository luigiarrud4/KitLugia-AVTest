#!/bin/sh
set -e
# Build Alpine-based emergency initramfs for KitLugia
# Requires: wget, cpio, gzip
# Run on Linux (WSL, Docker, CI)

ALPINE_MIRROR="http://dl-cdn.alpinelinux.org/alpine/v3.21/main/x86_64"

OUTDIR="$(cd "$(dirname "$0")" && pwd)"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

INITDIR="$WORKDIR/initramfs"
mkdir -p "$INITDIR"/{bin,sbin,usr/bin,usr/sbin,lib,dev,proc,sys,mnt,tmp}
mkdir -p "$INITDIR/lib64"

echo "=== Downloading APK packages ==="
fetch_apk() {
    local pkg="$1" url="${ALPINE_MIRROR}/${pkg}"
    echo "  Fetching $pkg..."
    wget -q "$url" -O "$WORKDIR/${pkg}" || { echo "  FAILED: $url"; exit 1; }
}

fetch_apk "busybox-static-1.37.0-r14.apk"
fetch_apk "ntfs-3g-progs-2026.2.25-r0.apk"
fetch_apk "ntfs-3g-libs-2026.2.25-r0.apk"
fetch_apk "sfdisk-2.40.4-r1.apk"
fetch_apk "util-linux-misc-2.40.4-r1.apk"
fetch_apk "dosfstools-4.2-r2.apk"
fetch_apk "blkid-2.40.4-r1.apk"
fetch_apk "musl-1.2.5-r11.apk"
fetch_apk "libfdisk-2.40.4-r1.apk"
fetch_apk "libncursesw-6.5_p20241006-r3.apk"
fetch_apk "libeconf-0.6.3-r0.apk"
fetch_apk "libblkid-2.40.4-r1.apk"
fetch_apk "libmount-2.40.4-r1.apk"
fetch_apk "libuuid-2.40.4-r1.apk"
fetch_apk "libsmartcols-2.40.4-r1.apk"
# Kernel (Ubuntu generic - funciona com EFI stub no VMware)
echo "=== Installing Ubuntu kernel ==="
apt-get update -qq 2>/dev/null
apt-get install -y -qq linux-image-generic 2>/dev/null
KERNEL=$(ls /boot/vmlinuz-* 2>/dev/null | tail -1)
if [ -n "$KERNEL" ] && [ -f "$KERNEL" ]; then
    cp -L "$KERNEL" "$OUTDIR/vmlinuz"
    echo "  Kernel: $(basename $KERNEL) ($(stat -c%s "$KERNEL") bytes)"
else
    echo "WARNING: apt-get failed, using pre-built kernel from LinuxPreOS/"
    KERNEL2="$(cd "$OUTDIR/../../LinuxPreOS" && pwd)/vmlinuz"
    if [ -f "$KERNEL2" ]; then
        cp -L "$KERNEL2" "$OUTDIR/vmlinuz"
        echo "  Copied from LinuxPreOS ($(stat -c%s "$OUTDIR/vmlinuz") bytes)"
    else
        echo "ERROR: No kernel found!"
        exit 1
    fi
fi

echo "=== Extracting binaries ==="
extract_apk() {
    local apk="$1" path="$2" dest="$3"
    mkdir -p "$(dirname "$INITDIR/$dest")"
    tar -xzf "$WORKDIR/$apk" -C "$WORKDIR/xtmp" 2>/dev/null
    [ -f "$WORKDIR/xtmp/$path" ] && cp -L "$WORKDIR/xtmp/$path" "$INITDIR/$dest"
    rm -rf "$WORKDIR/xtmp"/*
    mkdir -p "$WORKDIR/xtmp"
}

mkdir -p "$WORKDIR/xtmp"

# busybox static
extract_apk "busybox-static-1.37.0-r14.apk" "./bin/busybox.static" "bin/busybox"
chmod +x "$INITDIR/bin/busybox"

# ntfsresize (from ntfs-3g-progs, dynamically linked)
extract_apk "ntfs-3g-progs-2026.2.25-r0.apk" "./usr/sbin/ntfsresize" "usr/bin/ntfsresize"
chmod +x "$INITDIR/usr/bin/ntfsresize"

# mkntfs
extract_apk "ntfs-3g-progs-2026.2.25-r0.apk" "./usr/sbin/mkntfs" "sbin/mkfs.ntfs"
chmod +x "$INITDIR/sbin/mkfs.ntfs"

# sfdisk
extract_apk "sfdisk-2.40.4-r1.apk" "./sbin/sfdisk" "sbin/sfdisk"
chmod +x "$INITDIR/sbin/sfdisk"

# blockdev, switch_root, pivot_root (from util-linux-misc)
extract_apk "util-linux-misc-2.40.4-r1.apk" "./sbin/blockdev" "sbin/blockdev"
extract_apk "util-linux-misc-2.40.4-r1.apk" "./sbin/switch_root" "sbin/switch_root"
extract_apk "util-linux-misc-2.40.4-r1.apk" "./sbin/pivot_root" "sbin/pivot_root"
chmod +x "$INITDIR/sbin/blockdev" "$INITDIR/sbin/switch_root" "$INITDIR/sbin/pivot_root"

# mkfs.fat
extract_apk "dosfstools-4.2-r2.apk" "./sbin/mkfs.fat" "sbin/mkfs.fat"
chmod +x "$INITDIR/sbin/mkfs.fat"

# blkid
extract_apk "blkid-2.40.4-r1.apk" "./sbin/blkid" "sbin/blkid"
chmod +x "$INITDIR/sbin/blkid"

# musl libc
extract_apk "musl-1.2.5-r11.apk" "./lib/ld-musl-x86_64.so.1" "lib/ld-musl-x86_64.so.1"
# symlink copy: libc.musl-x86_64.so.1 -> ld-musl-x86_64.so.1
cp -L "$INITDIR/lib/ld-musl-x86_64.so.1" "$INITDIR/lib/libc.musl-x86_64.so.1"

# shared libs for dynamically linked bins
extract_apk "ntfs-3g-libs-2026.2.25-r0.apk" "./usr/lib/libntfs-3g.so.89.0.0" "lib/libntfs-3g.so.89"
extract_apk "libfdisk-2.40.4-r1.apk" "./usr/lib/libfdisk.so.1.1.0" "lib/libfdisk.so.1"
extract_apk "libncursesw-6.5_p20241006-r3.apk" "./usr/lib/libncursesw.so.6.5" "lib/libncursesw.so.6"
extract_apk "libeconf-0.6.3-r0.apk" "./usr/lib/libeconf.so.0.6.2" "lib/libeconf.so.0"
extract_apk "libblkid-2.40.4-r1.apk" "./usr/lib/libblkid.so.1.1.0" "lib/libblkid.so.1"
extract_apk "libmount-2.40.4-r1.apk" "./usr/lib/libmount.so.1.1.0" "lib/libmount.so.1"
extract_apk "libuuid-2.40.4-r1.apk" "./usr/lib/libuuid.so.1.3.0" "lib/libuuid.so.1"
extract_apk "libsmartcols-2.40.4-r1.apk" "./usr/lib/libsmartcols.so.1.1.0" "lib/libsmartcols.so.1"

echo "=== Creating symlinks ==="
"$INITDIR/bin/busybox" --install "$INITDIR/bin" 2>/dev/null || true

echo "=== Creating device nodes ==="
mknod "$INITDIR/dev/console" c 5 1 2>/dev/null || true
mknod "$INITDIR/dev/null" c 1 3 2>/dev/null || true

echo "=== Writing init script ==="
cat > "$INITDIR/init" << 'INITEOF'
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
# Try kernel_params.txt from ESP first (for BCD firmware boot)
DISK=""; PART=""; SHRINK_MB=""; LABEL=""; DL=""; BCD_GUID=""
$BB mkdir -p /tmp/esp_scan
for esp_try_dev in $($BB ls /dev/sd? /dev/nvme?n? /dev/mmcblk? /dev/vd? 2>/dev/null); do
    $BB mount "$esp_try_dev" /tmp/esp_scan 2>/dev/null && {
        if [ -f "/tmp/esp_scan/EFI/KitLugia/kernel_params.txt" ]; then
            echo "Lendo kernel_params.txt do ESP via $esp_try_dev"
            for kv in $($BB cat "/tmp/esp_scan/EFI/KitLugia/kernel_params.txt"); do
                case $kv in kitlugia_disk=*) DISK="${kv#kitlugia_disk=}" ;; esac
                case $kv in kitlugia_part=*) PART="${kv#kitlugia_part=}" ;; esac
                case $kv in kitlugia_shrink_mb=*) SHRINK_MB="${kv#kitlugia_shrink_mb=}" ;; esac
                case $kv in kitlugia_label=*) LABEL="${kv#kitlugia_label=}" ;; esac
                case $kv in kitlugia_dl=*) DL="${kv#kitlugia_dl=}" ;; esac
                case $kv in kitlugia_bcd_guid=*) BCD_GUID="${kv#kitlugia_bcd_guid=}" ;; esac
            done
            $BB umount /tmp/esp_scan 2>/dev/null
            break
        fi
        $BB umount /tmp/esp_scan 2>/dev/null
    }
done

# Fallback: parse from cmdline (for rEFInd/EFI stub boots)
if [ -z "$DISK" ]; then
    echo "Falling back to /proc/cmdline..."
    CMDLINE=$($BB cat /proc/cmdline)
    for x in $CMDLINE; do
        case $x in kitlugia_disk=*) DISK="${x#kitlugia_disk=}" ;; esac
        case $x in kitlugia_part=*) PART="${x#kitlugia_part=}" ;; esac
        case $x in kitlugia_shrink_mb=*) SHRINK_MB="${x#kitlugia_shrink_mb=}" ;; esac
        case $x in kitlugia_label=*) LABEL="${x#kitlugia_label=}" ;; esac
        case $x in kitlugia_dl=*) DL="${x#kitlugia_dl=}" ;; esac
        case $x in kitlugia_bcd_guid=*) BCD_GUID="${x#kitlugia_bcd_guid=}" ;; esac
    done
fi
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
INITEOF
chmod +x "$INITDIR/init"

echo "=== Building initramfs ==="
cd "$INITDIR" && find . | cpio -H newc -o | gzip -9 > "$WORKDIR/initrd.gz"
cp "$WORKDIR/initrd.gz" "$OUTDIR/initrd.gz"

echo "=== Done ==="
ls -lh "$OUTDIR/vmlinuz" "$OUTDIR/initrd.gz"
