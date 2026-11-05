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
printf '#define CONSOLE_SERIAL\n' > "$work/src/config/local/console.h"
cat > "$out/slt-chain.ipxe" <<'IPXE'
#!ipxe
dhcp || exit
chain tftp://${next-server}/autoexec.ipxe || exit
IPXE
make -C "$work/src" -j2 bin/undionly.kpxe bin-x86_64-efi/ipxe.efi EMBED="$out/slt-chain.ipxe"
cp "$work/src/bin/undionly.kpxe" "$out/undionly.kpxe"
cp "$work/src/bin-x86_64-efi/ipxe.efi" "$out/ipxe.efi"
cp "$work/COPYING" "$out/COPYING"
python3 - "$out" "$revision" <<'PY'
import hashlib,json,sys
from pathlib import Path
p=Path(sys.argv[1]); revision=sys.argv[2]
artifacts=[]
for id,name in [('bios','undionly.kpxe'),('uefi','ipxe.efi')]:
    f=p/name
    artifacts.append(dict(id=id,relativePath=name,sizeBytes=f.stat().st_size,sha256=hashlib.sha256(f.read_bytes()).hexdigest(),source='https://github.com/ipxe/ipxe/commit/'+revision))
manifest=dict(schemaVersion=1,id='ipxe-'+revision[:12],displayName='iPXE SLT chainload BIOS/UEFI',kind='IPXE_BOOTSTRAP',architecture='X64',firmwareSupport=['BIOS','UEFI'],secureBootStatus='UNSUPPORTED',validationStatus='NETWORK_BOOT_DEV',version=revision,artifacts=artifacts)
(p/'netboot-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
(p/'SLT-BUILD.txt').write_text('iPXE https://ipxe.org\nPinned upstream '+revision+'\nLocal build: config/local/console.h contains #define CONSOLE_SERIAL\nEMBED=slt-chain.ipxe (included); make bin/undionly.kpxe bin-x86_64-efi/ipxe.efi\nNo Secure Boot signature/homologation. Source tar and license included.\n')
PY
