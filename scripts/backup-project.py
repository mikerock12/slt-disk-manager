"""Create a source/history backup without caches, operational secrets or customer data."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--name", default="slt-disk-manager-source-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ"))
args = parser.parse_args()
if not args.name or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_" for c in args.name):
    raise SystemExit("Invalid backup name")
output = root / "artifacts" / (args.name + ".zip")
if output.exists():
    raise SystemExit("Backup already exists; use a new name")
output.parent.mkdir(exist_ok=True)


def git(*arguments):
    return subprocess.check_output(["git", *arguments], cwd=root)


files = sorted(set(git("ls-files", "--cached", "--others", "--exclude-standard", "-z").decode().split("\0")) - {""})
manifest = {"createdAtUtc": datetime.now(timezone.utc).isoformat(), "commit": git("rev-parse", "HEAD").decode().strip(), "branch": git("branch", "--show-current").decode().strip(), "files": []}
bundle = output.with_suffix(".bundle")
subprocess.run(["git", "bundle", "create", str(bundle), "--all"], cwd=root, check=True)
with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED) as archive:
    for relative in files:
        path = root / relative
        if not path.is_file() or path.is_symlink() or not path.resolve().is_relative_to(root):
            raise SystemExit("Refusing missing, symlink or external source file: " + relative)
        if path.suffix.lower() in {".pfx", ".p12", ".pem", ".key", ".db", ".zip", ".raw", ".img", ".iso", ".wim", ".esd", ".swm", ".vhd", ".vhdx", ".qcow2", ".vmdk", ".pyc", ".log"} or any(part.lower() in {"state", "runtime", ".tools", ".build", ".git", "bin", "obj", "artifacts", "isos", "__pycache__", ".aws", ".codex"} for part in path.relative_to(root).parts):
            raise SystemExit("Refusing potentially sensitive/generated file: " + relative)
        contents = path.read_bytes()
        archive.writestr(relative, contents)
        manifest["files"].append({"path": relative, "sizeBytes": len(contents), "sha256": hashlib.sha256(contents).hexdigest()})
    archive.write(bundle, "slt-disk-manager.bundle")
    archive.writestr("BACKUP-MANIFEST.json", json.dumps(manifest, ensure_ascii=False, indent=2))
with zipfile.ZipFile(output) as archive:
    assert archive.testzip() is None
print(json.dumps({"path": str(output), "sizeBytes": output.stat().st_size, "sha256": hashlib.sha256(output.read_bytes()).hexdigest(), "files": len(files)}, indent=2))
