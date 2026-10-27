"""Local media catalog integration. Optional --media imports an operator-owned ISO.
Never applies an image, mounts a volume, or opens a physical disk.
"""
import argparse
import hashlib
import http.client
import json
from pathlib import Path
import shutil
import ssl
import struct
import subprocess
import time
import uuid

project = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--media')
args = parser.parse_args()
directory = project / 'artifacts' / ('media-e2e-' + str(uuid.uuid4()))
directory.mkdir()
state = directory / 'state'
state.mkdir()
if args.media:
    source = Path(args.media).resolve(strict=True)
else:
    source = directory / 'synthetic.esd'
    xml = ('\ufeff<WIM><IMAGE INDEX="1"><NAME>Synthetic Windows</NAME><DESCRIPTION>Fixture only</DESCRIPTION>'
           '<TOTALBYTES>1024</TOTALBYTES><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR>'
           '<BUILD>19045</BUILD><SPBUILD>0</SPBUILD></VERSION></WINDOWS></IMAGE></WIM>').encode('utf-16le')
    header = bytearray(208)
    header[:8] = b'MSWIM\0\0\0'
    struct.pack_into('<I', header, 8, 208)
    struct.pack_into('<HHI', header, 40, 1, 1, 1)
    struct.pack_into('<QQQ', header, 72, len(xml), 208, len(xml))
    source.write_bytes(header + xml)
port = 17446
log = (directory / 'server.log').open('w', encoding='utf-8')
dll = project / 'src/server/SLT.Controller.Host/bin/Release/net10.0/SLT.Controller.Host.dll'
if not dll.exists():
    dll = project / 'src/server/SLT.Controller.Host/bin/Debug/net10.0/SLT.Controller.Host.dll'
process = subprocess.Popen(['dotnet', str(dll),
                            '--state-dir', str(state), '--port', str(port)], cwd=project, stdout=log, stderr=log)
try:
    connection = None
    for _ in range(120):
        if process.poll() is not None:
            raise RuntimeError('Controller failed; inspect ' + str(directory))
        try:
            connection = json.loads((state / 'connection.json').read_text(encoding='utf-8-sig'))
            break
        except (OSError, ValueError):
            time.sleep(.25)
    if not connection:
        raise RuntimeError('Controller readiness timed out')
    def api(method, path, body=None):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        client = http.client.HTTPSConnection('127.0.0.1', port, context=context, timeout=60)
        client.connect()
        if hashlib.sha256(client.sock.getpeercert(binary_form=True)).hexdigest() != connection['serverPin']:
            raise RuntimeError('TLS pin mismatch')
        client.request(method, path, None if body is None else json.dumps(body),
                       {'Authorization': 'Bearer ' + connection['token'], 'Content-Type': 'application/json'})
        response = client.getresponse()
        value = json.loads(response.read())
        client.close()
        if response.status != 200:
            raise RuntimeError(str(response.status) + ' ' + str(value))
        return value
    for _ in range(60):
        try:
            api('GET', '/api/v1/media')
            break
        except ConnectionError:
            time.sleep(.25)
    media = api('POST', '/api/v1/media/import', {'path': str(source), 'name': 'Windows media integration'})
    assert media['state'] == 'IMPORTING'
    for _ in range(1800):
        media = next(m for m in api('GET', '/api/v1/media') if m['id'] == media['id'])
        if media['state'] != 'IMPORTING':
            break
        time.sleep(1)
    assert media['state'] == 'AVAILABLE', media
    assert media['images'] and any(i['architecture'] == 'AMD64' for i in media['images'])
    payload = state / 'repository/media' / media['id'] / media['fileName']
    with payload.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    assert actual == media['sha256'] and payload.stat().st_size == media['sizeBytes']
    print('PASS media import, Windows editions, full SHA256 and catalog readback')
    print(json.dumps({'mediaId': media['id'], 'sizeBytes': media['sizeBytes'], 'sha256': actual,
                      'editions': [{'index': i['index'], 'name': i['name'], 'architecture': i['architecture'], 'version': i['version']} for i in media['images']]}, ensure_ascii=False))
finally:
    process.terminate()
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
    log.close()
    # Keep the private test catalog only for real operator-owned media inspection.
    if not args.media:
        shutil.rmtree(directory)
