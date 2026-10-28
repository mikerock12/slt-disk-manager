#!/usr/bin/env bash
set -euo pipefail
umask 077

root_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
agent_binary="${SLT_AGENT_BINARY:-$root_dir/slt-rescue-agent}"
build_dir="${SLT_BUILD_DIR:-$root_dir/build-$(date -u +%Y%m%dT%H%M%SZ)}"

if [[ $EUID -ne 0 ]]; then echo 'Execute como root em uma VM Debian de build.' >&2; exit 1; fi
for command in lb debootstrap xorriso mksquashfs sha256sum; do command -v "$command" >/dev/null || { echo "Ferramenta ausente: $command" >&2; exit 1; }; done
[[ -f "$agent_binary" && -x "$agent_binary" ]] || { echo "Compile o binário Linux executável: $agent_binary" >&2; exit 1; }
[[ ! -e "$build_dir" ]] || { echo "Diretório já existe; escolha SLT_BUILD_DIR novo: $build_dir" >&2; exit 1; }
mkdir -p -- "$(dirname -- "$build_dir")"
mkdir -m 700 -- "$build_dir"
cd -- "$build_dir"
lb config --mode debian --distribution trixie --architectures amd64 \
  --archive-areas 'main contrib non-free-firmware' --binary-images iso-hybrid \
  --bootloaders 'syslinux grub-efi' --apt-recommends false --apt-indices false \
  --firmware-chroot false --chroot-squashfs-compression-type zstd \
  --bootappend-live 'boot=live components hostname=slt-rescue username=technician console=tty0 console=ttyS0,115200n8' \
  --debian-installer none --iso-application 'SLT Disk Manager Rescue 0.1.0'
# Debian's stock ISOLINUX timeout is zero (wait for a key forever). Preserve its
# templates but select the ordinary live entry after three seconds in both modes.
mkdir -p config/bootloaders
cp -a /usr/share/live/build/bootloaders/. config/bootloaders/
[[ -f config/bootloaders/isolinux/isolinux.cfg && -f config/bootloaders/grub-pc/config.cfg ]]
sed -i 's/^timeout .*/timeout 30/' config/bootloaders/isolinux/isolinux.cfg
printf '\nserial 0 115200\ntimeout 30\n' >> config/bootloaders/isolinux/isolinux.cfg
cat >> config/bootloaders/grub-pc/config.cfg <<'GRUB'
serial --unit=0 --speed=115200 --word=8 --parity=no --stop=1
terminal_input console serial
terminal_output console serial
set default=0
set timeout_style=menu
set timeout=3
GRUB
mkdir -p config/package-lists config/includes.chroot/usr/local/bin \
  config/includes.chroot/usr/local/lib/slt config/includes.chroot/etc/systemd/system/multi-user.target.wants \
  config/includes.chroot/etc/systemd/system/getty.target.wants config/includes.chroot/etc/NetworkManager/conf.d
cat > config/package-lists/slt.list.chroot <<'PACKAGES'
linux-image-amd64
live-boot
live-config
live-config-systemd
live-boot-initramfs-tools
systemd-sysv
udev
dbus
network-manager
network-manager-l10n
wpasupplicant
wireless-regdb
iw
rfkill
sudo
user-setup
iproute2
ca-certificates
util-linux
fdisk
smartmontools
nvme-cli
gddrescue
partclone
parted
gdisk
ntfs-3g
dosfstools
e2fsprogs
rsync
openssh-client
firmware-realtek
firmware-linux
firmware-linux-nonfree
firmware-iwlwifi
firmware-atheros
firmware-brcm80211
firmware-mediatek
firmware-misc-nonfree
firmware-bnx2
firmware-bnx2x
PACKAGES
cat > config/includes.chroot/etc/NetworkManager/conf.d/20-slt-live.conf <<'NETWORK'
[main]
plugins=keyfile
[ifupdown]
managed=true
[connectivity]
enabled=false
NETWORK
ln -s /usr/lib/systemd/system/NetworkManager.service config/includes.chroot/etc/systemd/system/multi-user.target.wants/NetworkManager.service
install -m 0755 -- "$agent_binary" config/includes.chroot/usr/local/bin/slt-rescue-agent
mkdir -p config/includes.chroot/usr/share/doc/slt-rescue
install -m 0644 -- "$root_dir/../docs/licenses/Go-LICENSE.txt" config/includes.chroot/usr/share/doc/slt-rescue/Go-LICENSE.txt
install -m 0644 -- "$root_dir/README.md" config/includes.chroot/usr/share/doc/slt-rescue/README.md
install -m 0755 -- "$root_dir/scripts/start-rescue.sh" config/includes.chroot/usr/local/bin/slt-start
# Readiness never enrolls or starts a disk job. The smoke test powers down only
# when the QEMU guest carries our explicit SMBIOS fixture marker.
cat > config/includes.chroot/usr/local/lib/slt/boot-readiness.sh <<'READINESS'
#!/usr/bin/env bash
set -euo pipefail
umask 077
/usr/local/bin/slt-rescue-agent --help > /run/slt-agent-help.log 2>&1
grep -q -- '-server-pin' /run/slt-agent-help.log
bash -n /usr/local/bin/slt-start
id -u technician >/dev/null
sudo -n -u technician sudo -n true
for engine in lsblk blkid sfdisk smartctl nvme ddrescue partclone.restore ntfsresize ntfs-3g.probe resize2fs e2fsck nmcli nmtui nm-online iw wpa_supplicant; do
  command -v "$engine" >/dev/null
done
nmcli --version > /run/slt-network-tools.log
iw --version >> /run/slt-network-tools.log
for package in firmware-linux firmware-linux-nonfree firmware-realtek firmware-iwlwifi firmware-atheros firmware-brcm80211 firmware-mediatek firmware-misc-nonfree firmware-bnx2 firmware-bnx2x; do
  [[ "$(dpkg-query -W -f='${db:Status-Status}' "$package")" == installed ]]
done
firmware=BIOS
[[ ! -d /sys/firmware/efi ]] || firmware=UEFI
printf 'SLT_RESCUE_BOOT_READY:%s:AGENT_HELP_OK:ENGINES_OK:LOGIN_OK:NETWORK_TOOLS_OK\n' "$firmware" > /dev/ttyS0
if [[ -r /sys/class/dmi/id/product_name && -r /sys/class/dmi/id/sys_vendor ]] &&
   grep -qx 'SLT_SMOKE_TEST' /sys/class/dmi/id/product_name && grep -qx 'SLT_QEMU_ONLY' /sys/class/dmi/id/sys_vendor; then
  printf 'SLT_RESCUE_SYNTHETIC_SMOKE_POWEROFF\n' > /dev/ttyS0
  systemctl --no-block poweroff
fi
READINESS
chmod 0755 config/includes.chroot/usr/local/lib/slt/boot-readiness.sh
cat > config/includes.chroot/etc/systemd/system/slt-boot-readiness.service <<'SERVICE'
[Unit]
Description=SLT Rescue CLI and filesystem-engine readiness (no disk operations)
After=local-fs.target systemd-udev-trigger.service live-config.service
[Service]
Type=oneshot
ExecStart=/usr/local/lib/slt/boot-readiness.sh
StandardOutput=journal+console
StandardError=journal+console
RemainAfterExit=yes
[Install]
WantedBy=multi-user.target
SERVICE
ln -s ../slt-boot-readiness.service config/includes.chroot/etc/systemd/system/multi-user.target.wants/slt-boot-readiness.service
ln -s /lib/systemd/system/serial-getty@.service config/includes.chroot/etc/systemd/system/getty.target.wants/serial-getty@ttyS0.service
lb build
chroot chroot dpkg-query -W -f='${Package}\t${Version}\t${source:Package}\t${source:Version}\t${Architecture}\n' > packages.tsv
printf 'SourcePackage\tSourceVersion\tSourceIndexURL\n' > source-index.tsv
while IFS=$'\t' read -r package version source source_version architecture; do
  encoded_source="${source//+/%2B}"
  encoded_version="${source_version//:/%3A}"
  encoded_version="${encoded_version//+/%2B}"
  printf '%s\t%s\thttps://snapshot.debian.org/package/%s/%s/\n' "$source" "$source_version" "$encoded_source" "$encoded_version"
done < packages.tsv | sort -u >> source-index.tsv
(cd chroot && find usr/share/doc -mindepth 2 -maxdepth 2 -type f -name copyright -print0) |
  tar --null -C chroot -czf package-copyrights.tar.gz --files-from -
sha256sum live-image-amd64.hybrid.iso > live-image-amd64.hybrid.iso.sha256
printf 'ISO construída: %s/live-image-amd64.hybrid.iso\n' "$build_dir"
cat live-image-amd64.hybrid.iso.sha256
