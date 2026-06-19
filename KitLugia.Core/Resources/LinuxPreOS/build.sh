#!/bin/bash
set -e
OUTDIR="$1"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

apt-get update -qq 2>/dev/null
apt-get install -y -qq busybox-static ntfs-3g 2>/dev/null | tail -1

INIT="$WORKDIR/initramfs"
mkdir -p "$INIT"/{bin,dev,proc,sys,mnt,tmp,lib/x86_64-linux-gnu,lib64,usr/lib/x86_64-linux-gnu,sbin,usr/bin,usr/sbin}

cp /bin/busybox "$INIT/bin/busybox"
chmod +x "$INIT/bin/busybox"

for exe in /sbin/mkfs.ntfs /usr/bin/ntfs-3g /usr/sbin/ntfsresize /sbin/mount.ntfs-3g; do
    [ -f "$exe" ] || continue
    mkdir -p "$INIT$(dirname $exe)"
    cp -L "$exe" "$INIT$exe"
done

for exe in $(find "$INIT" -type f -executable 2>/dev/null); do
    ldd "$exe" 2>/dev/null | grep '=> /' | awk '{print $3}' | while read lib; do
        [ -f "$lib" ] || continue
        mkdir -p "$INIT$(dirname $lib)"
        cp -L "$lib" "$INIT$lib" 2>/dev/null
    done
done

"$INIT/bin/busybox" --install "$INIT/bin" 2>/dev/null
mknod "$INIT/dev/console" c 5 1 2>/dev/null
mknod "$INIT/dev/null" c 1 3 2>/dev/null

cat > "$INIT/init" << 'INITEOF'
#!/bin/busybox sh
export PATH=/bin:/sbin:/usr/bin:/usr/sbin
mount -t proc none /proc
mount -t sysfs none /sys
mount -t devtmpfs devtmpfs /dev 2>/dev/null || mount -t tmpfs none /dev
mount -t tmpfs none /tmp
CMDLINE=$(cat /proc/cmdline)
DISK=""; PART=""; SHRINK_MB=""
for arg in $CMDLINE; do
    case $arg in kitlugia_disk=*) DISK="${arg#kitlugia_disk=}" ;; kitlugia_part=*) PART="${arg#kitlugia_part=}" ;; kitlugia_shrink_mb=*) SHRINK_MB="${arg#kitlugia_shrink_mb=}" ;; esac
done
case $DISK in PHYSICALDRIVE0) DEV=/dev/sda ;; PHYSICALDRIVE1) DEV=/dev/sdb ;; PHYSICALDRIVE2) DEV=/dev/sdc ;; PHYSICALDRIVE3) DEV=/dev/sdd ;; *) echo DISK unknown; exec sh ;; esac
PART_DEV=${DEV}$PART; [ ! -b $PART_DEV ] && PART_DEV=${DEV}p$PART
echo === KitLugia Linux PreOS ===; echo Target: $PART_DEV Shrink: $SHRINK_MB MB; sync
if [ ! -b $PART_DEV ]; then echo "Partition $PART_DEV not found!"; ls /dev/sd* /dev/nvme* 2>/dev/null; exec sh; fi
SHRINK_BYTES=$((SHRINK_MB * 1024 * 1024))
ntfsresize --force --no-action $PART_DEV 2>&1; echo ""
ntfsresize --force -s -$SHRINK_BYTES $PART_DEV 2>&1; RC=$?
if [ $RC -eq 0 ]; then echo SHRINK OK!; sync; sleep 2; reboot -f; fi
echo FAILED exit=$RC; exec sh
INITEOF
chmod +x "$INIT/init"
cd "$INIT" && find . | cpio -H newc -o | gzip -9 > "$WORKDIR/initrd.gz"

KERNEL=$(ls /boot/vmlinuz-* 2>/dev/null | tail -1)
[ -z "$KERNEL" ] && apt-get install -y -qq linux-image-generic 2>/dev/null && KERNEL=$(ls /boot/vmlinuz-* | tail -1)
cp -L "$KERNEL" "$OUTDIR/vmlinuz"
cp "$WORKDIR/initrd.gz" "$OUTDIR/initrd.gz"

# Build standalone GRUB2 EFI binary with NTFS support
apt-get install -y -qq grub-efi-amd64-bin 2>/dev/null | tail -1
cat > "$WORKDIR/embedded.cfg" << 'GRUBEOF'
search --file --set=root /KitLugia/vmlinuz
set prefix=($root)/KitLugia
configfile $prefix/grub.cfg
GRUBEOF
grub-mkimage -O x86_64-efi -o "$OUTDIR/grubx64.efi" -p / -c "$WORKDIR/embedded.cfg" \
  ntfs linux ext2 fat part_gpt part_msdos normal configfile search echo reboot sleep gzio
echo OK
