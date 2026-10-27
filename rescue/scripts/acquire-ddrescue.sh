#!/usr/bin/env bash
# Local, read-only acquisition to a regular image file. Not a remote job executor.
set -euo pipefail
umask 077
fail() { printf '%s\n' "$*" >&2; exit 1; }
if [[ ${1:-} == --help || $# -ne 2 ]]; then
  echo 'Uso: sudo acquire-ddrescue.sh /dev/disk/by-id/IDENTIDADE /storage/caso/disco.img'
  echo 'Somente aquisição local offline. Mapfile e identidade persistem ao lado da imagem.'
  exit 0
fi
[[ $EUID -eq 0 ]] || fail 'Execute no Rescue Linux como root.'
for command in ddrescue ddrescuelog lsblk realpath sha256sum flock; do command -v "$command" >/dev/null || fail "Ferramenta ausente: $command"; done
source_link=$1
[[ $source_link == /dev/disk/by-id/* && $source_link != *-part* ]] || fail 'Selecione disco inteiro via /dev/disk/by-id; partições não são aceitas neste fluxo.'
source_device=$(realpath -e -- "$source_link")
[[ -b $source_device ]] || fail 'Origem não é dispositivo de bloco.'
[[ -z $(lsblk -nr -o MOUNTPOINTS -- "$source_device" | tr -d '[:space:]') ]] || fail 'Origem ou partições montadas/swap: adquira offline.'
source_identity=$(lsblk -dnbr -o MODEL,SERIAL,SIZE,LOG-SEC -- "$source_device")
serial=$(lsblk -dnro SERIAL -- "$source_device" | xargs)
[[ -n $serial ]] || fail 'Origem sem serial; identidade ambígua.'
source_fingerprint=$(printf '%s\n' "$source_identity" | sha256sum | cut -d ' ' -f 1)
target_parent=$(realpath -e -- "$(dirname -- "$2")")
[[ -d $target_parent && $target_parent != /dev && $target_parent != /proc && $target_parent != /sys ]] || fail 'Diretório de saída inválido.'
target_image="$target_parent/$(basename -- "$2")"
[[ $target_image == *.img && ! -L $target_image && ! -b $target_image && ! -c $target_image ]] || fail 'Saída deve ser arquivo regular .img, nunca dispositivo ou link.'
[[ ! -e $target_image || -f $target_image ]] || fail 'Saída existente não é arquivo regular.'
mapfile_path="$target_image.map"
identity_path="$target_image.source.sha256"
[[ ! -L $mapfile_path && ! -L $identity_path ]] || fail 'Metadados não podem ser links.'
[[ ! -L $target_image.lock && ( ! -e $target_image.lock || -f $target_image.lock ) ]] || fail 'Lock inválido.'
exec 9>"$target_image.lock"
flock -n 9 || fail 'Outra aquisição usa esta imagem.'
write_identity=false
if [[ -e $target_image || -e $mapfile_path ]]; then
  [[ -f $identity_path && -f $mapfile_path && -f $target_image ]] || fail 'Retomada exige imagem, mapfile e identidade da primeira execução.'
  [[ $(cat -- "$identity_path") == "$source_fingerprint" ]] || fail 'Disco de origem mudou: retomada bloqueada.'
elif [[ -e $identity_path ]]; then
  [[ -f $identity_path && $(cat -- "$identity_path") == "$source_fingerprint" ]] || fail 'Identidade existente não corresponde à origem.'
else
  write_identity=true
fi
printf 'ORIGEM: %s\nModelo/serial/tamanho/setor: %s\nARQUIVO DE SAÍDA: %s\n' "$source_link" "$source_identity" "$target_image"
printf 'Confirme digitando ADQUIRIR %s: ' "$serial"
IFS= read -r confirmation
[[ $confirmation == "ADQUIRIR $serial" ]] || fail 'Confirmação incorreta.'
[[ $(lsblk -dnbr -o MODEL,SERIAL,SIZE,LOG-SEC -- "$source_device" | sha256sum | cut -d ' ' -f 1) == "$source_fingerprint" ]] || fail 'Origem mudou após a confirmação.'
[[ -z $(lsblk -nr -o MOUNTPOINTS -- "$source_device" | tr -d '[:space:]') ]] || fail 'Origem foi montada; operação bloqueada.'
if $write_identity; then (set -o noclobber; printf '%s\n' "$source_fingerprint" > "$identity_path"); fi
ddrescue --no-scrape -- "$source_device" "$target_image" "$mapfile_path"
ddrescuelog --show-status -- "$mapfile_path"
printf 'Primeira passagem terminou. Consulte o mapfile: arquivo de imagem não implica recuperação integral.\n'
