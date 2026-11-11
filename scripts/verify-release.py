"""Download every asset of a published release and check it against GitHub's own records.

Checks: size and SHA256 of each asset against the digest GitHub reports, the .sha256 side
files and SHA256SUMS.txt, CRC of every ZIP, and that the Go agents inside the portable
package were built from the release commit with a clean tree (vcs.revision/vcs.modified).
Read only: nothing is installed or executed except `go version -m` on the downloaded agents.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("tag")
parser.add_argument("--repo", default="mikerock12/slt-disk-manager")
parser.add_argument("--output", type=Path, required=True, help="New folder for the downloaded assets")
parser.add_argument("--go", default="go")
args = parser.parse_args()
if not re.fullmatch(r"v[0-9A-Za-z.-]+", args.tag):
    parser.error("Unexpected tag")
if args.output.exists():
    parser.error("Output folder already exists; choose a new one")


def gh(*arguments):
    return subprocess.check_output(["gh", *arguments], text=True, encoding="utf-8")


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


release = json.loads(gh("release", "view", args.tag, "--repo", args.repo, "--json", "tagName,targetCommitish,isPrerelease,assets"))
commit = gh("api", f"repos/{args.repo}/commits/{release['targetCommitish']}", "--jq", ".sha").strip()
assert re.fullmatch(r"[0-9a-f]{40}", commit), commit
args.output.mkdir(parents=True)
subprocess.run(["gh", "release", "download", args.tag, "--repo", args.repo, "--dir", str(args.output)], check=True)
report = {"tag": args.tag, "commit": commit, "prerelease": release["isPrerelease"], "assets": [], "zips": [], "agents": []}
names = {asset["name"] for asset in release["assets"]}
assert names == {p.name for p in args.output.iterdir()}, "Downloaded files differ from the release asset list"
for asset in sorted(release["assets"], key=lambda a: a["name"]):
    path = args.output / asset["name"]
    actual = sha256(path)
    assert path.stat().st_size == asset["size"], asset["name"] + ": size differs"
    remote = (asset.get("digest") or "").removeprefix("sha256:")
    assert remote == actual, asset["name"] + ": SHA256 differs from the digest recorded by GitHub"
    report["assets"].append({"name": asset["name"], "sizeBytes": asset["size"], "sha256": actual})
hashes = {a["name"]: a["sha256"] for a in report["assets"]}
for side in sorted(n for n in names if n.endswith(".sha256") or n == "SHA256SUMS.txt"):
    for line in (args.output / side).read_text(encoding="ascii").splitlines():
        digest, _, name = line.partition("  ")
        name = name.strip().lstrip("*")
        if name in hashes:
            assert hashes[name] == digest.lower(), f"{side}: {name} differs"
        elif side != "SHA256SUMS.txt" and name == "live-image-amd64.hybrid.iso":
            continue
        else:
            raise AssertionError(f"{side} names a file that is not in the release: {name}")
for name in sorted(n for n in names if n.endswith(".zip")):
    with zipfile.ZipFile(args.output / name) as archive:
        assert archive.testzip() is None, name + ": CRC failure"
        report["zips"].append({"name": name, "entries": len(archive.infolist())})
portable = next(n for n in names if re.fullmatch(r"SLT-Disk-Manager-.*-win-x64\.zip", n))
with zipfile.ZipFile(args.output / portable) as archive:
    for suffix in ("rescue/slt-rescue-agent", "windows-pe/slt-winpe-agent.exe"):
        entry = next(n for n in archive.namelist() if n.endswith("/" + suffix))
        target = args.output / "_agents" / Path(suffix).name
        target.parent.mkdir(exist_ok=True)
        target.write_bytes(archive.read(entry))
        info = subprocess.check_output([args.go, "version", "-m", str(target)], text=True)
        revision = re.search(r"vcs\.revision=([0-9a-f]{40})", info)
        modified = re.search(r"vcs\.modified=(\w+)", info)
        assert revision and revision.group(1) == commit and modified and modified.group(1) == "false", suffix + ": not built from the clean release commit"
        report["agents"].append({"path": suffix, "vcsRevision": revision.group(1), "vcsModified": False})
(args.output / "_verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report, indent=2))
print(f"PASS {len(report['assets'])} assets of {args.tag} match GitHub digests; {len(report['zips'])} ZIP(s) CRC; agents built from {commit[:7]}", file=sys.stderr)
