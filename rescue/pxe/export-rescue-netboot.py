#!/usr/bin/env python3
"""Export an existing SLT Rescue ISO into an immutable, hash-described boot profile.

This only reads a regular ISO and writes a new directory. It never mounts the ISO,
opens a block device, runs content from the ISO, or changes a network interface.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def select_artifacts(directory: Path) -> dict[str, Path]:
    """Accept one version-matched kernel/initrd pair, including live-build aliases."""
    root = directory.resolve(strict=True)
    kernels = sorted(p for p in root.iterdir() if re.fullmatch(r"vmlinuz-.+", p.name))
    initrds = sorted(p for p in root.iterdir() if re.fullmatch(r"initrd\.img-.+", p.name))
    if len(kernels) == len(initrds) == 1:
        if kernels[0].name.removeprefix("vmlinuz-") != initrds[0].name.removeprefix("initrd.img-"):
            raise ValueError("Kernel and initrd versions differ")
        selected = {"kernel": kernels[0], "initrd": initrds[0]}
    elif not kernels and not initrds:
        selected = {"kernel": root / "vmlinuz", "initrd": root / "initrd.img"}
    else:
        raise ValueError("Expected exactly one kernel/initrd version in this ISO")
    selected["rootfs"] = root / "filesystem.squashfs"
    for kind, path in selected.items():
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(root) or not resolved.is_file() or resolved.stat().st_size == 0:
            raise ValueError(f"Unsafe or empty artifact: {kind}")
        # Aliases must describe the same payload, not a second mixed build.
        alias = root / {"kernel": "vmlinuz", "initrd": "initrd.img", "rootfs": "filesystem.squashfs"}[kind]
        if alias.exists() and (not alias.resolve().is_relative_to(root) or sha256(alias) != sha256(resolved)):
            raise ValueError(f"Artifact alias differs: {kind}")
        selected[kind] = resolved
    with selected["kernel"].open("rb") as stream:
        stream.seek(0x202)
        if stream.read(4) != b"HdrS":
            raise ValueError("Expected an x86 Linux boot-protocol kernel")
    with selected["initrd"].open("rb") as stream:
        header = stream.read(6)
        if not header.startswith((b"070701", b"070702", b"\x1f\x8b", b"\xfd7zXZ", b"\x28\xb5\x2f\xfd", b"\x02\x21\x4c\x18")):
            raise ValueError("Unsupported initramfs header")
    with selected["rootfs"].open("rb") as stream:
        if stream.read(4) != b"hsqs":
            raise ValueError("Expected little-endian SquashFS")
    return selected


def extract_live(iso: Path, output: Path, extractor: str) -> None:
    if Path(extractor).name.lower() in ("7z", "7z.exe", "7zz", "7zz.exe"):
        # -e flattens only the explicit live entries. No arbitrary ISO paths are
        # extracted and output filenames never come from commands in the media.
        listing = subprocess.check_output([extractor, "l", "-slt", "-sccUTF-8", str(iso)], text=True, encoding="utf-8")
        entries = [line[7:] for line in listing.splitlines() if line.startswith("Path = ")]
        selected = [p for p in entries if re.fullmatch(r"live[/\\](vmlinuz(?:-[A-Za-z0-9.+_-]+)?|initrd\.img(?:-[A-Za-z0-9.+_-]+)?|filesystem\.squashfs)", p)]
        if len(selected) != len(set(Path(p.replace("\\", "/")).name for p in selected)):
            raise ValueError("Duplicate ISO boot entries")
        if not selected:
            raise ValueError("No live boot artifacts found")
        subprocess.run([extractor, "e", "-y", "-bso0", "-bsp0", f"-o{output}", str(iso), *selected], check=True)
    else:
        subprocess.run([extractor, "-osirrox", "on", "-indev", str(iso), "-extract", "/live", str(output)], check=True)


def export(iso: Path, output: Path, version: str, source_commit: str, extractor: str) -> dict:
    if iso.is_symlink() or not iso.is_file() or iso.suffix.lower() != ".iso":
        raise ValueError("Source must be a regular .iso file, without symlinks")
    if not re.fullmatch(r"[0-9a-f]{40}", source_commit):
        raise ValueError("Provide the actual source commit (40 lowercase hexadecimal characters)")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.+_-]{0,47}", version):
        raise ValueError("Invalid version")
    profile_id = "rescue-" + re.sub(r"[^a-z0-9]+", "-", version.lower()).strip("-")
    if output.exists() or output.is_symlink():
        raise ValueError("Output already exists; select a new directory")
    output.parent.mkdir(parents=True, exist_ok=True)
    iso = iso.resolve(strict=True)
    iso_hash = sha256(iso)
    with tempfile.TemporaryDirectory(prefix=".slt-netboot-", dir=output.parent) as temporary:
        staging = Path(temporary)
        extracted = staging / "live"
        extracted.mkdir()
        extract_live(iso, extracted, extractor)
        artifacts = select_artifacts(extracted)
        publication = staging / "profile"
        publication.mkdir()
        manifest = {
            "schemaVersion": 1, "id": profile_id, "displayName": f"SLT Rescue {version} — BIOS/UEFI",
            "kind": "RESCUE_LINUX", "architecture": "X64", "firmwareSupport": ["BIOS", "UEFI"],
            "secureBootStatus": "UNSUPPORTED", "validationStatus": "NETWORK_BOOT_DEV",
            "version": version, "sourceCommit": source_commit, "sourceIsoSha256": iso_hash,
            "debianVersion": "13 (trixie)", "minimumMemoryMiB": 4096, "artifacts": [],
        }
        names = {"kernel": "vmlinuz", "initrd": "initrd.img", "rootfs": "filesystem.squashfs"}
        for kind, path in artifacts.items():
            target = publication / names[kind]
            expected_hash = sha256(path)
            shutil.copyfile(path, target)
            if sha256(target) != expected_hash:
                raise ValueError("Copied artifact does not match its source")
            manifest["artifacts"].append({
                "id": kind, "relativePath": target.name, "sizeBytes": target.stat().st_size,
                "sha256": expected_hash, "source": f"SLT Rescue ISO sha256:{iso_hash}; commit:{source_commit}",
            })
        if sha256(iso) != iso_hash:
            raise ValueError("Source ISO changed while exporting")
        (publication / "netboot-manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        sums = "".join(f"{entry['sha256']}  {entry['relativePath']}\n" for entry in manifest["artifacts"])
        sums += f"{sha256(publication / 'netboot-manifest.json')}  netboot-manifest.json\n"
        (publication / "SHA256SUMS.txt").write_text(sums, encoding="ascii")
        os.rename(publication, output)
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("iso", type=Path)
    parser.add_argument("output", type=Path, help="New output directory (existing directories are never overwritten)")
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-commit", required=True, help="Commit used to build this ISO, not the exporter HEAD")
    parser.add_argument("--extractor", default="xorriso", help="Path to xorriso (Linux) or 7z/7zz (Windows)")
    args = parser.parse_args()
    try:
        result = export(args.iso, args.output, args.version, args.source_commit, args.extractor)
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Netboot export failed: {error}\n")
    print(f"Exported {result['id']}: {len(result['artifacts'])} artifacts; NETWORK_BOOT_DEV (not boot-validated)")


if __name__ == "__main__":
    main()
