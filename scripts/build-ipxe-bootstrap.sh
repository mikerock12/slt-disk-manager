#!/usr/bin/env bash
# Build the two SLT chainloaders from a pinned upstream commit. Linux build host.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
revision=efd0b257a924f7c83cfcf27dfbf3e3ee7df7adac
out=${1:-$root/artifacts/ipxe-bootstrap}
work=${SLT_IPXE_BUILD_DIR:-$root/.tools/ipxe-$revision}
test ! -e "$out" || { echo 'Preserve existing versioned output; choose a new directory.' >&2; exit 1; }
mkdir -p "$work" "$out"
git -C "$work" init -q
git -C "$work" fetch --depth 1 https://github.com/ipxe/ipxe.git "$revision"
git -C "$work" checkout --detach FETCH_HEAD
test "$(git -C "$work" rev-parse HEAD)" = "$revision"
git -C "$work" archive --format=tar --prefix=ipxe/ HEAD | gzip -n > "$out/ipxe-source.tar.gz"
mkdir -p "$work/src/config/local"
# OVMF already mirrors EFI ConOut to COM1. A second UART console duplicates
# every character. BIOS needs the explicit UART driver; EFI retains its native
# screen/keyboard console (and firmware-provided serial redirection).
printf '#if defined ( PLATFORM_pcbios )\n#define CONSOLE_SERIAL\n#endif\n' > "$work/src/config/local/console.h"
cat > "$out/slt-chain.ipxe" <<'IPXE'
#!ipxe
dhcp || exit
chain tftp://${next-server}/autoexec.ipxe || exit
IPXE
make -C "$work/src" -j2 bin/undionly.kpxe bin-x86_64-efi/ipxe.efi EMBED="$out/slt-chain.ipxe"
cp "$work/src/bin/undionly.kpxe" "$out/undionly.kpxe"
cp "$work/src/bin-x86_64-efi/ipxe.efi" "$out/ipxe.efi"
# SLT mode: started from the client's own EFI System Partition (\EFI\SLT), this loader only
# runs the script a paired Windows client wrote next to it; anything else returns to the firmware.
cat > "$out/slt-local.ipxe" <<'IPXE'
#!ipxe
chain file:autoexec.ipxe || exit
IPXE
make -C "$work/src" -j2 bin-x86_64-efi/ipxe.efi EMBED="$out/slt-local.ipxe"
cp "$work/src/bin-x86_64-efi/ipxe.efi" "$out/ipxe-local.efi"
cmp -s "$out/ipxe.efi" "$out/ipxe-local.efi" && { echo 'Local loader was not rebuilt with its own script.' >&2; exit 1; }
cp "$work/COPYING" "$out/COPYING"
python3 - "$out" "$revision" "$root/VERSION" <<'PY'
import hashlib,json,re,sys
from pathlib import Path
p=Path(sys.argv[1]); revision=sys.argv[2]
app_version=Path(sys.argv[3]).read_text().strip()
profile_version=re.sub('[^a-z0-9]+','-',app_version.lower()).strip('-')
artifacts=[]
for id,name in [('bios','undionly.kpxe'),('uefi','ipxe.efi'),('uefi-local','ipxe-local.efi')]:
    f=p/name
    artifacts.append(dict(id=id,relativePath=name,sizeBytes=f.stat().st_size,sha256=hashlib.sha256(f.read_bytes()).hexdigest(),source='https://github.com/ipxe/ipxe/commit/'+revision))
manifest=dict(schemaVersion=1,id='ipxe-'+profile_version+'-'+revision[:12],displayName='iPXE SLT chainload BIOS/UEFI '+app_version,kind='IPXE_BOOTSTRAP',architecture='X64',firmwareSupport=['BIOS','UEFI'],secureBootStatus='UNSUPPORTED',validationStatus='NETWORK_BOOT_DEV',version=revision,artifacts=artifacts)
(p/'netboot-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
(p/'SLT-BUILD.txt').write_text('iPXE https://ipxe.org\nPinned upstream '+revision+'\nSLT package '+app_version+'\nLocal build: config/local/console.h enables CONSOLE_SERIAL only for PLATFORM_pcbios; native EFI console retained.\nEMBED=slt-chain.ipxe (included); make bin/undionly.kpxe bin-x86_64-efi/ipxe.efi\nipxe-local.efi: same tree, EMBED=slt-local.ipxe (included), for the SLT mode started from a client EFI partition.\nNo Secure Boot signature/homologation. Source tar and license included.\n')
PY
