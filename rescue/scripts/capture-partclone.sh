#!/usr/bin/env bash
# Local filesystem-aware capture only; no restore/format command is exposed here.
set -euo pipefail
umask 077
fail() { printf '%s\n' "$*" >&2; exit 1; }
if [[ ${1:-} == --help || $# -ne 2 ]]; then
  echo 'Uso: sudo capture-partclone.sh /dev/disk/by-id/IDENTIDADE-partN /storage/particao.partclone'
  echo 'Captura inteligente local de partição ext4/NTFS offline, sem restauração física.'
  exit 0
fi
[[ $EUID -eq 0 ]] || fail 'Execute no Rescue Linux como root.'
for command in lsblk blkid realpath sha256sum partclone.chkimg; do command -v "$command" >/dev/null || fail "Ferramenta ausente: $command"; done
[[ $1 == /dev/disk/by-id/*-part* ]] || fail 'Selecione partição via identidade estável /dev/disk/by-id/...-partN.'
source_device=$(realpath -e -- "$1")
[[ -b $source_device && $(lsblk -dnro TYPE -- "$source_device") == part ]] || fail 'Origem não é uma partição física.'
[[ -z $(lsblk -nr -o MOUNTPOINTS -- "$source_device" | tr -d '[:space:]') ]] || fail 'Partição montada/swap: operação bloqueada.'
filesystem=$(blkid -s TYPE -o value -- "$source_device")
case $filesystem in ext4) engine=partclone.ext4 ;; ntfs) engine=partclone.ntfs ;; *) fail "Filesystem não habilitado: $filesystem" ;; esac
command -v "$engine" >/dev/null || fail "Ferramenta ausente: $engine"
parent=$(realpath -e -- "$(dirname -- "$2")")
target_image="$parent/$(basename -- "$2")"
[[ $target_image == *.partclone && ! -e $target_image && ! -L $target_image ]] || fail 'Escolha arquivo .partclone novo; não sobrescrever saída.'
[[ ! -e $target_image.log && ! -L $target_image.log && ! -e $target_image.sha256 && ! -L $target_image.sha256 ]] || fail 'Log e checksum também precisam de caminhos novos.'
identity=$(lsblk -dnbr -o NAME,UUID,SIZE -- "$source_device")
printf 'ORIGEM: %s\nFilesystem: %s\nIdentidade: %s\nARQUIVO: %s\n' "$1" "$filesystem" "$identity" "$target_image"
printf 'Confirme digitando CAPTURAR: '
IFS= read -r confirmation
[[ $confirmation == CAPTURAR ]] || fail 'Confirmação incorreta.'
[[ $(lsblk -dnbr -o NAME,UUID,SIZE -- "$source_device") == "$identity" ]] || fail 'Identidade da partição mudou.'
[[ -z $(lsblk -nr -o MOUNTPOINTS -- "$source_device" | tr -d '[:space:]') ]] || fail 'Partição foi montada.'
"$engine" --clone --source "$source_device" --output "$target_image" --logfile "$target_image.log"
partclone.chkimg --source "$target_image"
sha256sum -- "$target_image" > "$target_image.sha256"
echo 'Captura e verificação Partclone concluídas; esta imagem não é RAW nem pode ser importada no protocolo RAW v1.'
