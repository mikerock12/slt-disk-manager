"""Exercise actual loopback HTTPS admin barriers without enabling boot or changing NICs."""
import hashlib
import http.client
import json
from pathlib import Path
import ssl
import subprocess
import time
import uuid

root = Path(__file__).resolve().parents[2]
state = root / "runtime" / ("network-admin-" + uuid.uuid4().hex)
state.mkdir(parents=True)
host = root / "src/server/SLT.Controller.Host/bin/Debug/net10.0/SLT.Controller.Host.dll"
port = 17449
checks = 0


def check(value, message):
    global checks
    assert value, message
    checks += 1
    print("PASS " + message, flush=True)


with (state / "server.log").open("w", encoding="utf-8") as log:
    process = subprocess.Popen(["dotnet", str(host), "--state-dir", str(state / "controller"), "--port", str(port)], stdout=log, stderr=log)
    try:
        connection = None
        for _ in range(120):
            assert process.poll() is None, "Controller exited; inspect ignored local server.log"
            try:
                connection = json.loads((state / "controller/connection.json").read_text(encoding="utf-8-sig"))
                break
            except (OSError, ValueError):
                time.sleep(.25)
        assert connection

        def request(method, path, body=None, authenticate=True):
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE  # Exact DER pin before token or HTTP bytes.
            client = http.client.HTTPSConnection("127.0.0.1", port, context=context, timeout=15)
            client.connect()
            assert hashlib.sha256(client.sock.getpeercert(binary_form=True)).hexdigest() == connection["serverPin"]
            headers = {"Content-Type": "application/json"}
            if authenticate:
                headers["Authorization"] = "Bearer " + connection["token"]
            client.request(method, path, json.dumps(body) if body is not None else None, headers)
            response = client.getresponse()
            data = response.read()
            result = json.loads(data) if data else None
            client.close()
            return response.status, result

        for _ in range(80):
            try:
                if request("GET", "/health")[0] == 200:
                    break
            except OSError:
                time.sleep(.25)
        prefix = "/api/v1/network-boot"
        check(request("GET", prefix + "/status", authenticate=False)[0] == 401, "Network Boot admin requires existing local bearer token")
        status, value = request("GET", prefix + "/status")
        check(status == 200 and value["state"] == "STOPPED" and value["profiles"] == [] and value["startedAtUtc"] is None, "real Controller starts with boot disabled and empty boot catalog")
        status, value = request("POST", prefix + "/start", {"confirmIsolatedNetwork": False})
        check(status == 400 and "Confirme" in value["error"], "explicit network consent enforced in actual HTTP endpoint")
        status, value = request("POST", prefix + "/start", {"confirmIsolatedNetwork": True})
        check(status == 400 and "CIDR" in value["error"], "consent cannot bypass missing topology or boot profiles")
        bad_config = {"schemaVersion": 1, "interfaceId": "synthetic-nonexistent", "bindIPv4": "8.8.8.8", "allowedCidr": "0.0.0.0/0", "mode": "PROXY_DHCP", "httpPort": 7443, "maxConcurrentTransfers": 100}
        check(request("PUT", prefix + "/config", bad_config)[0] == 400, "WAN/global CIDR/proxy/API port rejected before persistence")
        status, value = request("GET", prefix + "/status")
        check(status == 200 and value["config"]["bindIPv4"] == "" and value["state"] == "STOPPED", "invalid config leaves disabled state unchanged")
        source = state / "synthetic-import"
        source.mkdir()
        manifest = dict(schemaVersion=1, id="synthetic-traversal", displayName="Synthetic rejected profile", kind="RESCUE_LINUX", architecture="X64", firmwareSupport=["BIOS", "UEFI"], secureBootStatus="UNSUPPORTED", validationStatus="NETWORK_BOOT_DEV", version="fixture", artifacts=[dict(id=id, relativePath="../controller/admin.token", sizeBytes=64, sha256="0"*64, source="synthetic") for id in ["kernel", "initrd", "rootfs"]])
        # Give unique traversal paths so rejection exercises path validation as well as metadata.
        for index, artifact in enumerate(manifest["artifacts"]):
            artifact["relativePath"] = "../controller/" + ["admin.token", "connection.json", "server-info.txt"][index]
        (source / "netboot-manifest.json").write_text(json.dumps(manifest))
        check(request("POST", prefix + "/profiles/import", {"directoryPath": str(source)})[0] == 400, "HTTP import refuses paths escaping the selected source directory")
        check(request("GET", prefix + "/profiles") == (200, []), "rejected import publishes no profile")
        status, value = request("POST", prefix + "/stop", {})
        check(status == 200 and value["state"] == "STOPPED", "real stop endpoint remains idempotent without boot sockets")
        audit = request("GET", "/api/v1/audit")[1]
        check(any(event["action"] == "NETWORK_BOOT_STOPPED" for event in audit), "Network Boot administrative action recorded in chained audit")
        check(request("GET", "/api/v1/jobs") == (200, []), "boot administration created no disk job")
        print(f"Network Boot admin E2E: {checks}/{checks} PASS; no boot listeners, DHCP, NIC changes or physical disks", flush=True)
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=15)
