"""Validate release archive hashes and start its self-contained server on loopback."""
import argparse
import hashlib
import http.client
import json
from pathlib import Path
import ssl
import subprocess
import time
import uuid
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('package', type=Path)
args = parser.parse_args()
archive = args.package.resolve(strict=True)
folder = archive.with_suffix('')
assert folder.is_dir(), 'Keep the package directory beside its ZIP for executable smoke'
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    names = z.namelist()
    manifest_name = next(n for n in names if n.endswith('/SHA256SUMS.txt'))
    prefix = manifest_name[:-len('SHA256SUMS.txt')]
    checked = set()
    for line in z.read(manifest_name).decode('ascii').splitlines():
        digest, relative = line.split('  ', 1)
        relative = relative.replace('\\', '/')
        assert not relative.startswith('/') and '..' not in relative.split('/')
        assert Path(relative).suffix.lower() not in {'.pfx', '.pem', '.key', '.db', '.iso', '.wim', '.esd', '.raw', '.img'}
        entry = prefix + relative
        assert hashlib.sha256(z.read(entry)).hexdigest() == digest, relative
        assert hashlib.sha256((folder / relative).read_bytes()).hexdigest() == digest, relative
        checked.add(entry)
    assert {n for n in names if not n.endswith('/') and n != manifest_name} == checked
print('PASS archive CRC, all payload SHA256 hashes, extracted files and media/secret exclusion')
for relative in ['client/SLT.WindowsAgent.exe', 'windows-pe/slt-winpe-agent.exe']:
    result = subprocess.run([str(folder / relative), '--help'], capture_output=True, text=True, timeout=30)
    assert result.returncode == 0 and (result.stdout.strip() or result.stderr.strip()), relative
print('PASS packaged Windows client and native WinPE CLI start without external runtimes')
project = Path(__file__).resolve().parents[2]
state = project / 'runtime' / ('package-smoke-' + str(uuid.uuid4()))
state.mkdir(parents=True)
port = 17447
with (state / 'server.log').open('w', encoding='utf-8') as log:
    process = subprocess.Popen([str(folder / 'server/SLT.Controller.Host.exe'), '--state-dir', str(state), '--port', str(port)], stdout=log, stderr=log)
    try:
        connection = None
        for _ in range(120):
            assert process.poll() is None, 'Packaged server terminated'
            try:
                connection = json.loads((state / 'connection.json').read_text(encoding='utf-8-sig'))
                break
            except (OSError, ValueError):
                time.sleep(.25)
        assert connection, 'Packaged server readiness timed out'
        def api(path):
            ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
            client = http.client.HTTPSConnection('127.0.0.1', port, context=ctx, timeout=15)
            client.connect()
            assert hashlib.sha256(client.sock.getpeercert(binary_form=True)).hexdigest() == connection['serverPin']
            client.request('GET', path, headers={'Authorization': 'Bearer ' + connection['token']})
            response = client.getresponse()
            data = json.loads(response.read())
            client.close()
            assert response.status == 200
            return data
        for _ in range(60):
            try:
                assert api('/health')['status'] == 'ok'
                break
            except ConnectionError:
                time.sleep(.25)
        for path in ['/api/v1/devices', '/api/v1/clones', '/api/v1/file-bundles', '/api/v1/media']:
            assert api(path) == []
        print('PASS packaged self-contained Controller, TLS pin, admin auth and workflow catalogs')
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
