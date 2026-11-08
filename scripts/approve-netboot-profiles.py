"""Produce the installed whitelist from a successful isolated lab, never imported assertions."""
import hashlib
import json
import os
from pathlib import Path
import re
import sys

receipt_path, *manifest_paths, output_path = map(Path, sys.argv[1:])
receipt = json.loads(receipt_path.read_text())
for gate in ['success', 'namespaceIsolated', 'allOwnedProcessesStopped', 'bootAndInventoryGatePassed', 'postBootServiceStopGatePassed', 'preFilesystemFailureGatePassed', 'duringFilesystemFailureGatePassed', 'localLinuxFallbackGatePassed']:
    if receipt.get(gate) is not True:
        raise SystemExit('Laboratory gate missing/failed: ' + gate)
if receipt.get('isoAttached') or receipt.get('usbAttached') or receipt.get('physicalHardwareTested') or receipt.get('secureBoot'):
    raise SystemExit('Unexpected environment; refuse broad support claims')
commit = os.environ.get('GITHUB_SHA', '')
if not re.fullmatch('[0-9a-f]{40}', commit):
    raise SystemExit('CI snapshot SHA required')
profiles = []
for path in manifest_paths:
    profile = json.loads(path.read_text())
    evidence = next(p for p in receipt['profiles'] if p['id'] == profile['id'])
    if evidence['version'] != profile['version'] or sorted(evidence['artifacts'], key=lambda a:a['id']) != sorted([{key: a[key] for key in ['id','sizeBytes','sha256']} for a in profile['artifacts']], key=lambda a:a['id']):
        raise SystemExit('Profile does not match the actual laboratory payload')
    fields = ['SLT_BOOT_PROFILE_V1', profile['id'], profile['kind'], profile['architecture'], profile['version']]
    fields += [f"{a['id']}:{a['sizeBytes']}:{a['sha256'].lower()}" for a in sorted(profile['artifacts'], key=lambda a:a['id'])]
    fingerprint = hashlib.sha256(('\n'.join(fields)+'\n').encode()).hexdigest()
    profiles.append(dict(id=profile['id'],fingerprint=fingerprint))
if len(profiles) != 2:
    raise SystemExit('Exactly Rescue and iPXE are required')
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_text(json.dumps(dict(schemaVersion=1,sourceCommit=commit,profiles=profiles),indent=2)+'\n')
print('Approved exact laboratory payload fingerprints for '+commit)
