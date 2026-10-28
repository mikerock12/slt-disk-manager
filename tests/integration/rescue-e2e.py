"""Actual HTTPS/mTLS .NET Controller <-> Go Rescue; synthetic files only.

The TLS DER pin is checked immediately after handshake, before sending any HTTP
credentials or request bytes. The local self-issued CA is not installed globally.
"""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]


def wait_for(check, description, timeout=60):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, ValueError, KeyError, http.client.HTTPException) as exc:
            last = exc
        time.sleep(0.3)
    raise AssertionError(f"Timeout: {description}; {last}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--agent", default=str(ROOT / "artifacts/slt-rescue-agent-fixture.exe"))
    parser.add_argument("--controller", default=str(ROOT / "src/server/SLT.Controller.Host/bin/Debug/net10.0/SLT.Controller.Host.dll"))
    args = parser.parse_args()
    workspace = ROOT / "runtime" / ("e2e-" + str(uuid.uuid4()))
    fixtures = workspace / "fixtures"
    fixtures.mkdir(parents=True)
    source = os.urandom(4 * 1024 * 1024 + 512)
    (fixtures / "source.raw").write_bytes(source)
    (fixtures / "target.raw").write_bytes(bytes(len(source)))
    # Batch clone target lives on a second Rescue agent; it starts with unrelated bytes.
    stream_fixtures = workspace / "stream-target"
    stream_fixtures.mkdir()
    stream_source = os.urandom(3 * 4 * 1024 * 1024 + 512)
    (fixtures / "stream-source.raw").write_bytes(stream_source)
    (stream_fixtures / "clone-target.raw").write_bytes(bytes([0xA5]) * len(stream_source))
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    processes = []
    logs = []
    try:
        log = open(workspace / "controller.log", "wb")
        logs.append(log)
        controller = subprocess.Popen(["dotnet", args.controller, "--state-dir", str(workspace / "server"), "--port", str(port), "--stream-window-chunks", "1"], stdout=log, stderr=log)
        processes.append(controller)
        connection = wait_for(lambda: json.loads((workspace / "server/connection.json").read_text()), "controller identity")

        def request(method, path, body=None, authorized=True, expected=200):
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE  # replaced by exact pin below before HTTP bytes
            conn = http.client.HTTPSConnection("127.0.0.1", port, context=context, timeout=10)
            conn.connect()
            actual_pin = hashlib.sha256(conn.sock.getpeercert(binary_form=True)).hexdigest()
            if actual_pin != connection["serverPin"]:
                conn.close()
                raise AssertionError("TLS identity pin mismatch; no credentials sent")
            headers = {"Content-Type": "application/json"}
            if authorized:
                headers["Authorization"] = "Bearer " + connection["token"]
            conn.request(method, path, json.dumps(body).encode() if body is not None else None, headers)
            response = conn.getresponse()
            data = response.read()
            conn.close()
            if response.status != expected:
                raise AssertionError(f"{method} {path} returned {response.status}: {data[:500]!r}")
            return json.loads(data) if data else None

        wait_for(lambda: request("GET", "/health"), "HTTPS listener")
        duplicate = subprocess.run(["dotnet", args.controller, "--state-dir", str(workspace / "server"), "--port", str(port)], capture_output=True, timeout=15)
        assert duplicate.returncode != 0 and controller.poll() is None
        request("GET", "/health")
        print("PASS second controller cannot open or reconcile an active state directory")
        request("GET", "/api/v1/devices", authorized=False, expected=401)
        request("GET", "/agent/v1/jobs/next", authorized=False, expected=401)
        print("PASS HTTPS listener and administrative/agent authentication barriers")
        log = open(workspace / "rescue.log", "wb")
        logs.append(log)
        agent = subprocess.Popen([args.agent, "--server", f"https://127.0.0.1:{port}", "--server-pin", connection["serverPin"], "--state-dir", str(workspace / "agent"), "--fixture-dir", str(fixtures), "--allow-file-write", "--poll-interval", "1s"], stdout=log, stderr=log)
        processes.append(agent)
        enrollments = wait_for(lambda: request("GET", "/api/v1/enrollments"), "Go CSR enrollment")
        enrollment = enrollments[0]
        request("POST", "/api/v1/enrollments/" + enrollment["id"] + "/approve", {"code": "wrong"}, expected=400)
        request("POST", "/api/v1/enrollments/" + enrollment["id"] + "/approve", {"code": enrollment["code"]})
        devices = wait_for(lambda: [d for d in request("GET", "/api/v1/devices") if d.get("inventory")], "mTLS inventory")
        device = devices[0]
        disks = device["inventory"]["disks"]
        source_disk = next(d for d in disks if os.path.basename(d["path"]) == "source.raw")
        target_disk = next(d for d in disks if os.path.basename(d["path"]) == "target.raw")
        print("PASS real Go enrollment, client certificate and .NET mTLS inventory")

        def execute(kind, disk, image=None):
            job = request("POST", "/api/v1/jobs/plan", {"kind": kind, "deviceId": device["id"], "diskFingerprint": disk["fingerprint"], "imageId": image, "consent": True})
            request("POST", "/api/v1/jobs/" + job["id"] + "/approve", {"challenge": job["plan"]["challenge"]})
            result = wait_for(lambda: next((j for j in request("GET", "/api/v1/jobs") if j["id"] == job["id"] and j["state"] in ("COMPLETED", "FAILED", "CANCELLED")), None), kind)
            assert result["state"] == "COMPLETED", result
            return result

        capture = execute("CAPTURE", source_disk)
        images = request("GET", "/api/v1/images")
        image = next(i for i in images if i["id"] == capture["plan"]["imageId"])
        image = request("GET", "/api/v1/images/" + image["id"])
        assert image["state"] == "AVAILABLE" and len(image["chunks"]) == 2
        request("POST", "/api/v1/images/" + image["id"] + "/verify", {})
        wait_for(lambda: next((i for i in request("GET", "/api/v1/images") if i["id"] == image["id"] and i["state"] == "AVAILABLE"), None), "background image verification")
        print("PASS signed CAPTURE, 2 chunks, persisted SHA256 receipts and complete manifest")
        execute("RESTORE", target_disk, image["id"])
        assert (fixtures / "target.raw").read_bytes() == source
        print("PASS signed RESTORE, hash-before-write and exact full destination equality")
        log = open(workspace / "rescue-target.log", "wb")
        logs.append(log)
        target_agent = subprocess.Popen([args.agent, "--server", f"https://127.0.0.1:{port}", "--server-pin", connection["serverPin"], "--state-dir", str(workspace / "agent-target"), "--fixture-dir", str(stream_fixtures), "--allow-file-write", "--poll-interval", "1s"], stdout=log, stderr=log)
        processes.append(target_agent)
        pending = wait_for(lambda: [e for e in request("GET", "/api/v1/enrollments") if e["status"] == "PENDING"], "second Go CSR enrollment")
        request("POST", "/api/v1/enrollments/" + pending[0]["id"] + "/approve", {"code": pending[0]["code"]})
        target_device = wait_for(lambda: next((d for d in request("GET", "/api/v1/devices") if d["id"] != device["id"] and d.get("inventory")), None), "second mTLS inventory")
        stream_disk = next(d for d in device["inventory"]["disks"] if d["path"].endswith("stream-source.raw"))
        clone_disk = target_device["inventory"]["disks"][0]
        assert "CLONE_STREAM" in device["inventory"]["capabilities"] and "CLONE_STREAM" in target_device["inventory"]["capabilities"]
        planned = request("POST", "/api/v1/clones/plan", {"sourceDeviceId": device["id"], "sourceDiskFingerprint": stream_disk["fingerprint"], "targetDeviceId": target_device["id"], "targetDiskFingerprint": clone_disk["fingerprint"], "strategy": "EXACT_RAW", "consent": True, "transfer": "STREAM"})
        images_before = len(request("GET", "/api/v1/images"))
        for job in (planned["captureJob"], planned["targetJob"]):
            request("POST", "/api/v1/jobs/" + job["id"] + "/approve", {"challenge": job["plan"]["challenge"]})
        stream_dir = workspace / "server" / "repository" / "streams" / planned["session"]["streamId"]
        peak = 0
        def finished():
            nonlocal peak
            if stream_dir.exists():
                peak = max(peak, len([f for f in stream_dir.iterdir() if f.suffix == ".bin"]))
            states = {j["id"]: j["state"] for j in request("GET", "/api/v1/jobs")}
            done = [states[planned["captureJob"]["id"]], states[planned["targetJob"]["id"]]]
            return done if all(s in ("COMPLETED", "FAILED", "CANCELLED") for s in done) else None
        result = wait_for(finished, "batch clone", timeout=120)
        assert result == ["COMPLETED", "COMPLETED"], result
        assert (stream_fixtures / "clone-target.raw").read_bytes() == stream_source
        assert not stream_dir.exists() and peak <= 1 and len(request("GET", "/api/v1/images")) == images_before
        session = next(s for s in request("GET", "/api/v1/clones") if s["id"] == planned["session"]["id"])
        assert session["state"] == "COMPLETED" and session["transfer"] == "STREAM"
        print("PASS batch clone between two Rescue agents: server window 1, batches discarded, destination byte-exact, no image kept")
        request("POST", "/api/v1/devices/" + device["id"] + "/revoke", {})
        wait_for(lambda: agent.poll() is not None, "revoked Go session terminates")
        assert agent.returncode != 0
        print("PASS revocation rejects further authenticated requests")
        print("7/7 end-to-end scenarios passed. Logs in", workspace)
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
        for log in logs:
            log.close()


if __name__ == "__main__":
    main()
