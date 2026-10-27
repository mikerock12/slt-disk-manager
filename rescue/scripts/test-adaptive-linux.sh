#!/usr/bin/env bash
set -euo pipefail
root_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
[[ $(uname -s) == Linux ]] || { echo 'Este teste exige Linux.' >&2; exit 1; }
[[ $EUID -eq 0 ]] || { echo 'Execute como root em VM/runner dedicado; somente loops de arquivos temporários serão usados.' >&2; exit 1; }
for command in go sfdisk blkid e2fsck resize2fs mkfs.ext4 mkfs.fat mkntfs ntfscp ntfscat ntfsresize ntfs-3g.probe debugfs losetup; do
  command -v "$command" >/dev/null || { echo "Ferramenta obrigatória ausente: $command" >&2; exit 1; }
done
cd -- "$root_dir/src/rescue-agent"
export SLT_LINUX_INTEGRATION=1
go test -v -timeout 15m -run '^TestAdaptiveLinux' ./internal/agent
