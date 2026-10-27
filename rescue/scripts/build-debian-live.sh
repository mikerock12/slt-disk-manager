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
  --bootappend-live 'boot=live components hostname=slt-rescue username=technician' \
  --debian-installer false --iso-application 'SLT Disk Manager Rescue 0.1.0'
mkdir -p config/package-lists config/includes.chroot/usr/local/bin
cat > config/package-lists/slt.list.chroot <<'PACKAGES'
linux-image-amd64
live-boot
live-config
systemd-sysv
sudo
iproute2
isc-dhcp-client
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
PACKAGES
install -m 0755 -- "$agent_binary" config/includes.chroot/usr/local/bin/slt-rescue-agent
lb build
sha256sum live-image-amd64.hybrid.iso > live-image-amd64.hybrid.iso.sha256
printf 'ISO construída: %s/live-image-amd64.hybrid.iso\n' "$build_dir"
cat live-image-amd64.hybrid.iso.sha256
