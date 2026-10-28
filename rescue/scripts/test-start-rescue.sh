#!/usr/bin/env bash
set -euo pipefail
root_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# Source functions and substitute only the local execution/root boundary. These
# tests never run the real agent, contact the network or open any disk.
source "$root_dir/start-rescue.sh"
slt_require_root() { return 0; }
slt_agent_available() { return 0; }
slt_agent_exec() { captured=("$@"); }
slt_prepare_network() { return 0; }
declare -a captured=()
pin='0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
run_case() {
  captured=()
  slt_start_main <<< "$1" > /dev/null
}
run_case $'https://192.168.10.5:7443\n'"$pin"$'\n\n'
[[ ${#captured[@]} -eq 4 && "${captured[0]}" == --server && "${captured[1]}" == https://192.168.10.5:7443 && "${captured[3]}" == "$pin" ]]
printf 'PASS: default capability has no physical-write flag or automatic job\n'
run_case $'https://192.168.10.5:7443\n'"$pin"$'\n2\nPERMITIR GRAVACAO\n\n'
[[ ${#captured[@]} -eq 5 && "${captured[4]}" == --allow-physical-write ]]
printf 'PASS: explicit mode and exact typed confirmation enable physical capability only\n'
fixture_root="$(mktemp -d)"
scratch="$(realpath -e -- "$fixture_root")/Scratch folder"
mkdir -- "$scratch"
trap 'rmdir -- "$scratch" "$fixture_root"' EXIT
run_case $'https://192.168.10.5:7443\n'"$pin"$'\n2\nPERMITIR GRAVACAO\n'"$scratch"$'\n'
[[ ${#captured[@]} -eq 7 && "${captured[5]}" == --scratch-dir && "${captured[6]}" == "$scratch" ]]
printf 'PASS: scratch containing spaces remains one literal argument; no mount or disk is opened\n'
if run_case $'https://192.168.10.5:7443\n'"$pin"$'\n2\nwrong\n\n' 2>/dev/null; then exit 1; fi
[[ ${#captured[@]} -eq 0 ]]
printf 'PASS: wrong local confirmation never invokes agent\n'
if run_case $'https://192.168.10.5:7443;echo-bad\n'"$pin"$'\n1\n' 2>/dev/null; then exit 1; fi
[[ ${#captured[@]} -eq 0 ]]
printf 'PASS: shell characters in URL rejected without evaluation\n'
if run_case $'http://192.168.10.5:7443\n'"$pin"$'\n1\n' 2>/dev/null; then exit 1; fi
[[ ${#captured[@]} -eq 0 ]]
printf 'PASS: unencrypted URL never invokes agent\n'
if run_case $'https://192.168.10.5:7443\ninvalid\n1\n' 2>/dev/null; then exit 1; fi
[[ ${#captured[@]} -eq 0 ]]
printf 'PASS: malformed pin never invokes agent\n'
