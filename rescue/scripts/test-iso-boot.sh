#!/usr/bin/env bash
set -euo pipefail
umask 077

# Boot only a regular ISO. No host disks, shares, NICs or controller credentials
# are attached. OVMF variables are private temporary copies, never host firmware.
[[ $# -eq 2 ]] || { echo 'Usage: test-iso-boot.sh ISO NEW_OUTPUT_DIR' >&2; exit 2; }
iso="$(realpath -e -- "$1")"
[[ -f "$iso" && ! -L "$1" && "$iso" == *.iso ]] || { echo 'Expected regular ISO file' >&2; exit 2; }
[[ ! -e "$2" ]] || { echo 'Choose a new evidence directory' >&2; exit 2; }
mkdir -m 700 -p -- "$2"
output="$(realpath -e -- "$2")"
for tool in qemu-system-x86_64 timeout sha256sum; do command -v "$tool" >/dev/null; done
sha256sum "$iso" > "$output/iso-tested.sha256"

ovmf_code=''
ovmf_vars=''
for size in 4M ''; do
  suffix="${size:+_$size}"
  if [[ -f "/usr/share/OVMF/OVMF_CODE${suffix}.fd" && -f "/usr/share/OVMF/OVMF_VARS${suffix}.fd" ]]; then
    ovmf_code="/usr/share/OVMF/OVMF_CODE${suffix}.fd"
    ovmf_vars="/usr/share/OVMF/OVMF_VARS${suffix}.fd"
    break
  fi
done
[[ -n "$ovmf_code" ]] || { echo 'Matching OVMF code/variables pair missing' >&2; exit 1; }
cp -- "$ovmf_vars" "$output/OVMF_VARS.fd"

for mode in BIOS UEFI; do
  firmware=()
  if [[ "$mode" == UEFI ]]; then
    firmware=(-drive "if=pflash,format=raw,unit=0,readonly=on,file=$ovmf_code" -drive "if=pflash,format=raw,unit=1,file=$output/OVMF_VARS.fd")
  fi
  echo "Booting actual Rescue ISO in $mode with no data disks or network"
  status=0
  timeout --signal=TERM --kill-after=10s 360s qemu-system-x86_64 \
    -no-user-config -machine q35,accel=tcg -cpu max -m 2048 -smp 2 \
    -display none -monitor none -serial "file:$output/$mode.serial.log" \
    -nic none -boot order=d,menu=off -cdrom "$iso" -no-reboot \
    -smbios type=1,manufacturer=SLT_QEMU_ONLY,product=SLT_SMOKE_TEST "${firmware[@]}" \
    > "$output/$mode.qemu.log" 2>&1 || status=$?
  cat "$output/$mode.serial.log" || true
  cat "$output/$mode.qemu.log" || true
  [[ $status -eq 0 ]] || { echo "$mode VM failed/timed out: $status" >&2; exit 1; }
  grep -q "SLT_RESCUE_BOOT_READY:$mode:AGENT_HELP_OK:ENGINES_OK:LOGIN_OK:NETWORK_TOOLS_OK" "$output/$mode.serial.log"
  grep -q 'SLT_RESCUE_SYNTHETIC_SMOKE_POWEROFF' "$output/$mode.serial.log"
  printf '%s PASS: live kernel/userspace, packaged Go CLI and engines; no disk job or network\n' "$mode" | tee -a "$output/results.txt"
done
sha256sum --check "$output/iso-tested.sha256"
