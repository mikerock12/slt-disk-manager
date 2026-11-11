#!/usr/bin/env python3
"""Real BIOS/UEFI PXE lab using a disposable Linux network namespace.

Requires root, unshare, iproute2, dnsmasq, QEMU, OVMF, dotnet and Tesseract. Never attaches
an ISO/USB or host disk. DHCP is a lab-only external dnsmasq, not SLT's service.
Evidence excludes the Controller state directory (individual keys/admin token).
"""
from __future__ import annotations

import argparse
import hashlib
import http.client
import importlib.util
import json
import os
from pathlib import Path
import re
import select
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import uuid


def progress(message):
    timestamp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    print(f"[{timestamp}] {message}", flush=True)


def terminal_text(text):
    text = re.sub(r"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", "", text)
    text = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", text)
    text = re.sub(r"\x1b[@-_]", "", text)
    return text.replace("\r", "")


def serial_tail(text, maximum=2400):
    # Only known local launcher/diagnostic commands run in these guests. Do not
    # print credentials if a future guest unexpectedly emits a secret field.
    text = re.sub(r"-----BEGIN [^-]*PRIVATE KEY-----.*?(?:-----END [^-]*PRIVATE KEY-----|$)", "[private key redacted]", text, flags=re.S)
    text = re.sub(r"(?i)(authorization\s*:\s*bearer\s+)\S+", r"\1[redacted]", text)
    text = re.sub(r'''(?i)(["']?(?:token|secret|password)["']?[ \t]*[:=][ \t]*["']?)[^\s"']+''', r"\1[redacted]", text)
    text = re.sub(r"(?i)(c[oó]digo\s+)\d{8}", r"\1[redacted pairing code]", text)
    text = terminal_text(text)
    text = "".join(character for character in text if character.isprintable() or character in "\r\n\t")
    return "\n".join(text.replace("\r", "").splitlines()[-24:])[-maximum:]


def run(*arguments):
    return subprocess.check_output(arguments, text=True).strip()


def digest(path):
    hasher = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def wait_for(check, description, timeout=60):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, ValueError, http.client.HTTPException) as error:
            last = error
        time.sleep(0.2)
    raise AssertionError(f"Timeout: {description}; {last}")


class GuestConsole:
    def __init__(self, path, evidence, *, connected_socket=None):
        # The optional already-connected socket lets the Windows-only console
        # regression use a loopback QEMU TCP serial port. Product control is not
        # involved; the Linux PXE harness always uses its private Unix socket.
        self.connection = connected_socket or socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        if connected_socket is None:
            self.connection.connect(str(path))
        self.log = evidence.open("wb")
        self.evidence = evidence
        self.text = ""
        self.qmp = None
        self.timeout_count = 0

    def pump(self):
        while select.select([self.connection], [], [], 0)[0]:
            try:
                data = self.connection.recv(65536)
            except ConnectionResetError:
                break
            if not data:
                break
            self.log.write(data)
            self.log.flush()
            self.text += data.decode("utf-8", errors="replace")
        return self.text

    def expect(self, pattern, timeout=120, offset=0, label=None):
        description = label or f"guest serial {pattern}"
        try:
            return wait_for(lambda: re.search(pattern, terminal_text(self.pump()[offset:])), description, timeout)
        except AssertionError:
            self.diagnostics(description)
            raise

    def diagnostics(self, description, kind="TIMEOUT"):
        self.timeout_count += 1
        progress(f"{kind} {self.evidence.name}: {description}")
        print("--- bounded guest serial tail ---\n" + serial_tail(self.pump()) + "\n--- end serial tail ---", flush=True)
        if self.qmp:
            screenshot = self.evidence.with_name(self.evidence.stem + f".timeout-{self.timeout_count}.ppm")
            try:
                self.qmp.execute("screendump", {"filename": str(screenshot)})
                progress(f"Saved failure screenshot: {screenshot.name}")
            except (OSError, ValueError, RuntimeError) as error:
                progress(f"Failure screenshot unavailable: {type(error).__name__}")

    def send(self, value):
        # Serial keyboard Enter is CR. LF alone can leave agetty/login input
        # pending, while output still contains an earlier prompt in its history.
        self.connection.sendall(value.replace("\r\n", "\r").replace("\n", "\r").encode("ascii"))

    def command(self, command, marker, timeout=60):
        offset = len(self.pump())
        completion = "SLT_CMD_" + uuid.uuid4().hex + "_RC"
        # A completion line must be produced by the shell. Neither the command's
        # echoed printf format nor terminal wrapping contains `completion=N`.
        self.send(command + "; slt_lab_rc=$?; printf '\\n%s=%s\\n' '" + completion + "' \"$slt_lab_rc\"\n")
        result = self.expect(r"(?m)^" + re.escape(completion) + r"=(\d+)\r?$", timeout, offset, label="guest shell command completion")
        if result.group(1) != "0":
            self.diagnostics("guest shell command returned exit " + result.group(1), kind="FAILURE")
            raise AssertionError("Guest shell command failed with exit " + result.group(1))
        output = terminal_text(self.pump()[offset:])
        if re.search(r"(?m)^" + re.escape(marker) + r"\r?$", output) is None:
            self.diagnostics("expected command output marker absent", kind="FAILURE")
            raise AssertionError("Expected guest command output marker absent")
        return output

    def login(self, marker="SLT_PXE_LOGIN_OK", timeout=90):
        """Drive fresh getty/login prompts, then prove technician identity/sudo.

        Every transition uses an offset recorded before sending input. A stale
        `login:`/`Password:` in boot history can never trigger another password
        or be mistaken for an authenticated shell.
        """
        deadline = time.monotonic() + timeout
        offset = len(self.pump())
        self.send("\n")
        state = "AWAIT_PROMPT"
        pattern = (r"(?m)(?P<login>^[^\r\n]*\blogin:[ \t]*\r?$)|"
                   r"(?P<password>^[^\r\n]*Password:[ \t]*\r?$)|"
                   r"(?P<shell>technician@[^\r\n]*\$[ \t]*\r?$)|"
                   r"(?P<rejected>Login incorrect)")
        while True:
            result = self.expect(pattern, max(1, deadline - time.monotonic()), offset, label="fresh local login state " + state)
            transition = result.lastgroup
            offset = len(self.pump())
            if transition == "shell":
                break
            if transition == "login" and state == "AWAIT_PROMPT":
                progress("Local serial login prompt received; submitting technician username")
                self.send("technician\n")
                state = "USERNAME_SENT"
            elif transition == "password" and state == "USERNAME_SENT":
                progress("Fresh Live password prompt received")
                self.send("live\n")
                state = "PASSWORD_SENT"
            else:
                self.diagnostics("unexpected or rejected local login transition", kind="FAILURE")
                raise AssertionError("Local technician login rejected or unexpected prompt state")
        # Exact identity and sudo success are required; reaching a root shell,
        # seeing a username in MOTD or merely echoing a command is insufficient.
        self.command("sudo -n true && test \"$(id -un)\" = technician && printf '%s%s\\n' '" + marker[:4] + "' '" + marker[4:] + "'", marker)

    def close(self):
        self.pump()
        self.connection.close()
        self.log.close()


class Qmp:
    def __init__(self, path):
        self.socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.socket.settimeout(10)
        self.socket.connect(str(path))
        self.file = self.socket.makefile("rwb", buffering=0)
        self.file.readline()
        self.execute("qmp_capabilities")

    def execute(self, command, arguments=None):
        self.file.write((json.dumps({"execute": command, "arguments": arguments or {}}) + "\n").encode())
        while True:
            line = self.file.readline()
            if not line:
                raise RuntimeError("QMP disconnected")
            response = json.loads(line)
            if "error" in response:
                raise RuntimeError(response["error"])
            if "return" in response:
                return response["return"]

    def key(self, key):
        self.execute("send-key", {"keys": [{"type": "qcode", "data": key}], "hold-time": 100})

    def close(self):
        self.file.close()
        self.socket.close()


def import_profile(source, store):
    source = Path(source)
    profile = json.loads((source / "netboot-manifest.json").read_text(encoding="utf-8-sig"))
    assert re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", profile["id"])
    target = store / profile["id"]
    target.mkdir()
    for artifact in profile["artifacts"]:
        assert re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", artifact["id"])
        path = source / artifact["relativePath"]
        assert not path.is_symlink() and path.resolve().is_relative_to(source.resolve())
        assert path.stat().st_size == artifact["sizeBytes"] and digest(path) == artifact["sha256"]
        shutil.copyfile(path, target / artifact["id"])
        artifact["relativePath"] = artifact["id"]
    profile["enabled"] = True
    profile["validationStatus"] = "NETWORK_BOOT_DEV"
    return profile


def firmware_files():
    for suffix in ("_4M", ""):
        code = Path(f"/usr/share/OVMF/OVMF_CODE{suffix}.fd")
        variables = Path(f"/usr/share/OVMF/OVMF_VARS{suffix}.fd")
        if code.is_file() and variables.is_file():
            return code, variables
    raise FileNotFoundError("Matching non-Secure-Boot OVMF code/variables missing")


def slt_mode_scenario(args, private, start, request, connection, firmware_files, loader_hash):
    """SLT mode: a paired Windows client restarts into Rescue from its own EFI partition.

    The Windows side is represented by the same HTTPS calls the real agent makes (legacy
    pairing, mTLS inventory with REBOOT_RESCUE, signed job, loader download). What that
    agent would write to \\EFI\\SLT is written here to a synthetic GPT disk, which a fresh
    UEFI VM then starts from: no PXE ROM, ISO or USB. Not covered here: the Windows
    bcdedit/firmware-entry step itself.
    """
    progress("Starting SLT mode scenario: paired client restarts into Rescue from its own EFI partition")
    work = private / "slt-mode"
    work.mkdir()
    key, csr = work / "client-key.pem", work / "client.csr"
    subprocess.run(["openssl", "req", "-new", "-newkey", "rsa:2048", "-nodes", "-keyout", str(key), "-out", str(csr),
                    "-subj", "/CN=SLT-Windows-Enrollment"], check=True, capture_output=True)

    def agent(method, path, body=None, certificate=None, headers=None, raw=False, expected=200):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE  # Exact DER pin checked below, before any request byte.
        if certificate:
            context.load_cert_chain(str(certificate), str(key))
        client = http.client.HTTPSConnection("127.0.0.1", 7443, context=context, timeout=20)
        client.connect()
        assert hashlib.sha256(client.sock.getpeercert(binary_form=True)).hexdigest() == connection["serverPin"]
        client.request(method, path, json.dumps(body) if body is not None else None, {"Content-Type": "application/json", **(headers or {})})
        response = client.getresponse()
        payload = response.read()
        client.close()
        assert response.status == expected, (path, response.status, payload[:300])
        return payload if raw else (json.loads(payload) if payload else None)

    secret = os.urandom(32).hex()
    created = agent("POST", "/agent/v1/enroll", {"hostname": "PC-CLIENTE-LAB", "mode": "WINDOWS", "csrPem": csr.read_text(), "secret": secret})
    request("POST", f"/api/v1/enrollments/{created['id']}/approve", {"code": created["code"]})
    status = agent("GET", f"/agent/v1/enroll/{created['id']}", headers={"X-Enrollment-Secret": secret})
    assert status["status"] == "APPROVED"
    certificate = work / "client-cert.pem"
    certificate.write_text(status["certificatePem"])
    windows_id = status["deviceId"]
    inventory = {"deviceId": windows_id, "hostname": "PC-CLIENTE-LAB", "os": "Windows 10 (simulado pelo laboratório)", "mode": "WINDOWS",
                 "agentVersion": "0.1.0", "capabilities": ["INVENTORY", "REBOOT_RESCUE"], "disks": [],
                 "capturedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "notes": ["SYNTHETIC_FIXTURE"]}
    agent("POST", "/agent/v1/inventory", inventory, certificate)
    job = request("POST", "/api/v1/jobs/plan", {"kind": "REBOOT_RESCUE", "deviceId": windows_id, "diskFingerprint": "", "consent": True, "rescueBoot": {"unattendedWrite": True}})
    assert job["plan"]["challenge"] == "REINICIAR PC-CLIENTE-LAB MODO SLT" and not job["plan"]["rescueBoot"]["ticket"]
    request("POST", f"/api/v1/jobs/{job['id']}/approve", {"challenge": job["plan"]["challenge"]})
    listed = next(j for j in request("GET", "/api/v1/jobs") if j["id"] == job["id"])
    assert listed["plan"]["rescueBoot"]["ticket"] == "***" and listed["plan"]["rescueBoot"]["script"] == "***", "ticket leaked to the administrative listing"
    assignment = agent("GET", "/agent/v1/jobs/next", certificate=certificate)
    import base64
    payload = base64.b64decode(assignment["payload"])
    (work / "payload.bin").write_bytes(payload)
    (work / "signature.bin").write_bytes(base64.b64decode(assignment["signature"]))
    # The plan is signed by the pinned server certificate, exactly what the Windows agent checks.
    server_pem = ssl.get_server_certificate(("127.0.0.1", 7443))
    assert hashlib.sha256(ssl.PEM_cert_to_DER_cert(server_pem)).hexdigest() == connection["serverPin"]
    (work / "server.pem").write_text(server_pem)
    public = subprocess.run(["openssl", "x509", "-in", str(work / "server.pem"), "-pubkey", "-noout"], check=True, capture_output=True).stdout
    (work / "server-public.pem").write_bytes(public)
    subprocess.run(["openssl", "dgst", "-sha256", "-verify", str(work / "server-public.pem"), "-signature", str(work / "signature.bin"), str(work / "payload.bin")], check=True, capture_output=True)
    plan = json.loads(payload)
    boot = plan["rescueBoot"]
    assert plan["kind"] == "REBOOT_RESCUE" and plan["deviceId"] == windows_id and re.fullmatch("[0-9a-f]{64}", boot["ticket"])
    assert boot["loaderSha256"] == loader_hash and boot["script"].startswith("#!ipxe\n") and ("slt.ticket=" + boot["ticket"]) in boot["script"]
    loader = agent("GET", f"/agent/v1/jobs/{plan['id']}/rescue-loader", certificate=certificate, raw=True)
    assert hashlib.sha256(loader).hexdigest() == boot["loaderSha256"] and len(loader) == boot["loaderSizeBytes"]
    agent("POST", f"/agent/v1/jobs/{plan['id']}/progress", {"phase": "VALIDATING", "bytesProcessed": 0, "bytesTotal": 1}, certificate)
    agent("POST", f"/agent/v1/jobs/{plan['id']}/result", {"success": True, "message": "Reinício em modo SLT agendado (laboratório).", "details": "REBOOT_SCHEDULED"}, certificate)
    progress("SLT mode: signed REBOOT_RESCUE received over mTLS; loader hash and server signature verified")

    # The client's "system disk": GPT + EFI System Partition holding what the agent stages.
    mib = 1024 * 1024
    system_disk = work / "client-system.raw"
    with system_disk.open("wb") as stream:
        stream.truncate(96 * mib)
    subprocess.run(["sfdisk", "--quiet", str(system_disk)], input=b"label: gpt\nstart=2048, size=131072, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B, name=\"EFI\"\n", check=True)
    esp = f"{system_disk}@@{mib}"
    mtools = {**os.environ, "MTOOLS_SKIP_CHECK": "1"}
    subprocess.run(["mformat", "-i", esp, "-T", "131072", "-v", "ESP", "::"], check=True, env=mtools)
    (work / "ipxe.efi").write_bytes(loader)
    (work / "autoexec.ipxe").write_text(boot["script"], encoding="ascii", newline="\n")
    # \EFI\SLT is what the Windows agent writes. The copy in \EFI\BOOT only stands in for the
    # one-time firmware entry (BootNext) that bcdedit creates on a real PC.
    for directory in ("::/EFI", "::/EFI/SLT", "::/EFI/BOOT"):
        subprocess.run(["mmd", "-i", esp, directory], check=True, env=mtools)
    subprocess.run(["mcopy", "-i", esp, str(work / "ipxe.efi"), "::/EFI/SLT/ipxe.efi"], check=True, env=mtools)
    subprocess.run(["mcopy", "-i", esp, str(work / "autoexec.ipxe"), "::/EFI/SLT/autoexec.ipxe"], check=True, env=mtools)
    subprocess.run(["mcopy", "-i", esp, str(work / "ipxe.efi"), "::/EFI/BOOT/BOOTX64.EFI"], check=True, env=mtools)
    subprocess.run(["mcopy", "-i", esp, str(work / "autoexec.ipxe"), "::/EFI/BOOT/autoexec.ipxe"], check=True, env=mtools)
    system_hash = digest(system_disk)
    target = work / "format-target.raw"
    target.write_bytes(bytes((n * 7 + 3) % 251 for n in range(mib)) * 96)
    target_before = digest(target)

    code, template = firmware_files()
    variables = work / "OVMF_VARS.fd"
    shutil.copyfile(template, variables)
    qmp_path, serial_path = work / "qmp.sock", work / "serial.sock"
    vm = start(["qemu-system-x86_64", "-no-user-config", "-nodefaults", "-device", "VGA", "-machine", "q35,accel=tcg", "-cpu", "max",
                "-m", str(args.memory_mib), "-smp", "2", "-display", "none", "-no-reboot",
                "-qmp", f"unix:{qmp_path},server=on,wait=off", "-serial", f"unix:{serial_path},server=on,wait=off",
                "-drive", f"if=pflash,format=raw,unit=0,readonly=on,file={code}", "-drive", f"if=pflash,format=raw,unit=1,file={variables}",
                "-netdev", "tap,id=net0,ifname=slttap0,script=no,downscript=no",
                # No bootindex on the NIC and its option ROM removed: this VM cannot PXE boot by itself.
                "-device", "e1000,netdev=net0,mac=52:54:00:77:00:5a,romfile=",
                "-drive", f"if=none,id=system,file={system_disk},format=raw,readonly=on",
                "-device", "virtio-blk-pci,drive=system,serial=SLT-CLIENT-SYSTEM,bootindex=0",
                "-drive", f"if=none,id=target,file={target},format=raw",
                "-device", "virtio-blk-pci,drive=target,serial=SLT-FORMAT-TARGET",
                "-smbios", "type=1,manufacturer=SLT_QEMU_ONLY,product=SLT_MODE_LAB"], "slt-mode-qemu")
    console, qmp = None, None
    began = time.monotonic()
    try:
        wait_for(lambda: serial_path.exists() and qmp_path.exists(), "SLT mode QEMU console")
        console = GuestConsole(serial_path, args.output / "slt-mode.serial.log")
        qmp = Qmp(qmp_path)
        console.qmp = qmp
        console.expect("SLT_RESCUE_BOOT_READY:UEFI", 900, label="Rescue userspace started from the client's EFI partition")
        progress("SLT mode: Rescue userspace reached from the local EFI loader (no PXE ROM, ISO or USB)")
        boot_seconds = time.monotonic() - began
        # No login, no keyboard: the session must enrol by itself with the ticket.
        device = wait_for(lambda: next((d for d in request("GET", "/api/v1/devices") if d.get("originDeviceId") == windows_id and d.get("inventory")), None), "SLT mode session enrolled by ticket", 240)
        assert device["mode"] == "RESCUE" and device["hostname"] == "PC-CLIENTE-LAB (modo SLT)" and device["unattendedWrite"] is True
        assert "FORMAT_DISK" in device["inventory"]["capabilities"], device["inventory"]["capabilities"]
        disks = {disk["serial"]: disk for disk in device["inventory"]["disks"]}
        assert disks["SLT-CLIENT-SYSTEM"]["isReadOnly"] and not disks["SLT-FORMAT-TARGET"]["isReadOnly"]
        qmp.execute("screendump", {"filename": str(args.output / "slt-mode-session.ppm")})
        progress("SLT mode: session enrolled with the single-use ticket and sent its mTLS inventory, without typing anything on the client")
        replay = agent("POST", "/agent/v1/enroll", {"hostname": "intruso", "mode": "RESCUE", "csrPem": csr.read_text(), "secret": os.urandom(32).hex(), "ticket": boot["ticket"]}, expected=403)
        assert "utilizado" in replay["error"] or "expirado" in replay["error"], replay
        refused = False
        try:
            request("POST", "/api/v1/jobs/plan", {"kind": "FORMAT_DISK", "deviceId": device["id"], "diskFingerprint": disks["SLT-CLIENT-SYSTEM"]["fingerprint"], "consent": True, "format": {"tableType": "GPT", "fileSystem": "NTFS", "label": "X"}})
        except AssertionError:
            refused = True
        assert refused, "format of a write-protected disk was planned"
        job = request("POST", "/api/v1/jobs/plan", {"kind": "FORMAT_DISK", "deviceId": device["id"], "diskFingerprint": disks["SLT-FORMAT-TARGET"]["fingerprint"], "consent": True, "format": {"tableType": "GPT", "fileSystem": "NTFS", "label": "SLTLAB"}})
        assert job["plan"]["challenge"].startswith("FORMATAR PC-CLIENTE-LAB (modo SLT) ")
        request("POST", f"/api/v1/jobs/{job['id']}/approve", {"challenge": job["plan"]["challenge"]})
        final = wait_for(lambda: next((j for j in request("GET", "/api/v1/jobs") if j["id"] == job["id"] and j["state"] in ("COMPLETED", "FAILED")), None), "FORMAT_DISK ordered from the server in SLT mode", 300)
        assert final["state"] == "COMPLETED", final
        report = json.loads(final["result"]["details"])
        assert report["verified"] and report["fileSystem"] == "NTFS"
        progress("SLT mode: FORMAT_DISK authorised in the panel API ran on the synthetic disk with no keyboard confirmation on the client")
        qmp.execute("quit")
        vm.wait(timeout=20)
    finally:
        if qmp:
            qmp.close()
        if console:
            console.close()
        if vm.poll() is None:
            vm.terminate()
            vm.wait(timeout=15)
    table = json.loads(run("sfdisk", "--json", str(target)))["partitiontable"]
    assert table["label"] == "gpt" and len(table["partitions"]) == 1 and table["partitions"][0]["start"] == 2048, table
    filesystem = run("blkid", "-p", "-O", str(mib), "-s", "TYPE", "-o", "value", str(target))
    label = run("blkid", "-p", "-O", str(mib), "-s", "LABEL", "-o", "value", str(target))
    assert filesystem == "ntfs" and label == "SLTLAB", (filesystem, label)
    assert digest(system_disk) == system_hash and digest(target) != target_before
    request("POST", f"/api/v1/devices/{device['id']}/revoke", {})
    request("POST", f"/api/v1/devices/{windows_id}/revoke", {})
    print("PASS slt-mode: UEFI client restarted into Rescue from its own EFI partition, enrolled by ticket, formatted a synthetic disk on server order", flush=True)
    return {"firmware": "uefi", "bootSource": "LOCAL_EFI_SYSTEM_PARTITION", "pxeRomUsed": False, "isoAttached": False, "usbAttached": False,
            "windowsAgentSimulated": True, "windowsBootEntryStepCovered": False, "signedPlanVerified": True, "loaderSha256": loader_hash,
            "ticketEnrollment": True, "ticketSingleUse": True, "ticketHiddenFromAdminListing": True, "keyboardInputOnClient": False,
            "bootSeconds": round(boot_seconds, 2), "formatVerified": True, "formatTable": table["label"], "formatFileSystem": filesystem,
            "clientSystemDiskSha256Before": system_hash, "clientSystemDiskSha256After": digest(system_disk),
            "writeProtectedDiskFormatRefused": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service-dll", type=Path, required=True)
    parser.add_argument("--rescue-profile", type=Path, required=True)
    parser.add_argument("--bootstrap-profile", type=Path, required=True)
    parser.add_argument("--controller-dll", type=Path)
    parser.add_argument("--local-system-image", type=Path, help="Same-build hybrid Rescue ISO, copied to a temporary readonly virtio RAW disk for local Linux fallback")
    parser.add_argument("--output", type=Path, required=True, help="New evidence directory; contains no Controller state")
    parser.add_argument("--firmware", default="bios,uefi")
    parser.add_argument("--memory-mib", type=int, default=4096)
    parser.add_argument("--inside-netns", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--parent-netns", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("Run this dedicated lab with sudo on Linux; no DHCP is sent on the host network")
    if not shutil.which("tesseract"):
        parser.error("The strict PXE failure gate requires Tesseract; install tesseract-ocr before starting this lab")
    observer_spec = importlib.util.spec_from_file_location("slt_boot_failure_observer", Path(__file__).with_name("boot_failure_observer.py"))
    failure_observer = importlib.util.module_from_spec(observer_spec)
    observer_spec.loader.exec_module(failure_observer)
    modes = args.firmware.split(",")
    assert modes and set(modes) <= {"bios", "uefi"} and len(modes) == len(set(modes))
    assert 2048 <= args.memory_mib <= 16384
    for name in ("service_dll", "rescue_profile", "bootstrap_profile", "controller_dll", "local_system_image", "output"):
        if getattr(args, name) is not None:
            setattr(args, name, getattr(args, name).resolve())
    if not args.inside_netns:
        if args.output.exists():
            parser.error("Evidence directory already exists")
        command = ["unshare", "--net", sys.executable, str(Path(__file__).resolve()), *sys.argv[1:],
                   "--inside-netns", "--parent-netns", os.readlink("/proc/self/ns/net")]
        os.execvp(command[0], command)
    # Assertions run before adding any network address or starting DHCP. A fresh
    # unshare namespace has only lo and no route to any host/production network.
    namespace = os.readlink("/proc/self/ns/net")
    assert args.parent_netns and namespace != args.parent_netns, "Not a separate network namespace"
    interfaces = json.loads(run("ip", "-j", "link"))
    assert [interface["ifname"] for interface in interfaces] == ["lo"], interfaces
    assert not json.loads(run("ip", "-j", "route", "show", "default"))
    args.output.mkdir(parents=True)
    os.chmod(args.output, 0o700)
    results = {"namespaceIsolated": True, "hostNetworkChanged": False, "isoAttached": False,
               "usbAttached": False, "secureBoot": False, "physicalHardwareTested": False,
               "controllerValidationRequested": bool(args.controller_dll),
               "localSystemFallbackRequested": bool(args.local_system_image),
               "memoryMiB": args.memory_mib, "firmware": [], "outages": [], "fetchOutages": [],
               "localFallbacks": [], "success": False}
    processes, logs = [], []

    def start(command, name, **kwargs):
        log = (args.output / f"{name}.log").open("wb")
        logs.append(log)
        process = subprocess.Popen(command, stderr=log, stdout=log, start_new_session=True, **kwargs)
        processes.append(process)
        return process

    def on_termination(signum, _frame):
        results["cancelledBySignal"] = signum
        results["success"] = False
        progress(f"Received signal {signum}; stopping owned lab processes and preserving receipt")
        raise SystemExit(128 + signum)

    try:
        signal.signal(signal.SIGTERM, on_termination)
        progress("Creating isolated bridge/tap; host interfaces remain outside this namespace")
        run("ip", "link", "set", "lo", "up")
        run("ip", "link", "add", "sltbr0", "type", "bridge")
        run("ip", "address", "add", "10.77.0.1/24", "dev", "sltbr0")
        run("ip", "link", "set", "sltbr0", "up")
        run("ip", "tuntap", "add", "dev", "slttap0", "mode", "tap")
        run("ip", "link", "set", "slttap0", "master", "sltbr0")
        run("ip", "link", "set", "slttap0", "up")
        results["topology"] = json.loads(run("ip", "-j", "address"))
        assert not json.loads(run("ip", "-j", "route", "show", "default"))
        with tempfile.TemporaryDirectory(prefix="slt-pxe-private-") as private_dir:
            private = Path(private_dir)
            store = private / "boot-store"
            store.mkdir()
            profiles = [import_profile(args.rescue_profile, store), import_profile(args.bootstrap_profile, store)]
            progress("Verified and staged Rescue/iPXE profiles")
            local_disk = None
            local_hash = None
            if args.local_system_image:
                source = args.local_system_image
                manifest = json.loads((args.rescue_profile / "netboot-manifest.json").read_text(encoding="utf-8-sig"))
                assert source.is_file() and not source.is_symlink() and source.suffix.lower() == ".iso"
                local_hash = digest(source)
                assert local_hash == manifest["sourceIsoSha256"], "Local Linux fixture must originate from the same ISO as the netboot profile"
                local_disk = private / "local-system.raw"
                shutil.copyfile(source, local_disk)
                assert digest(local_disk) == local_hash
                results["localSystemFixture"] = {"kind": "HYBRID_RESCUE_BYTES_AS_VIRTIO_RAW_DISK",
                    "sha256": local_hash, "sizeBytes": local_disk.stat().st_size,
                    "installedPersistentSystem": False, "cdromAttached": False, "usbAttached": False}
                progress("Prepared same-build Linux local-disk fixture (readonly RAW, no CD/USB)")
            bootstrap = next(profile for profile in profiles if profile["kind"] == "IPXE_BOOTSTRAP")
            rescue = next(profile for profile in profiles if profile["kind"] == "RESCUE_LINUX")
            local_loader = next((artifact for artifact in bootstrap["artifacts"] if artifact["id"] == "uefi-local"), None)
            slt_source = None
            if local_loader:
                slt_source = private / "slt-mode-source.json"
                slt_source.write_text(json.dumps({"httpBase": "http://10.77.0.1:8088", "profileId": rescue["id"],
                    "loaderPath": str(store / bootstrap["id"] / "uefi-local"), "loaderSha256": local_loader["sha256"],
                    "loaderSizeBytes": local_loader["sizeBytes"]}))
            results["profiles"] = [{"id": profile["id"], "version": profile["version"],
                                    "artifacts": [{"id": artifact["id"], "sizeBytes": artifact["sizeBytes"], "sha256": artifact["sha256"]}
                                                  for artifact in profile["artifacts"]]} for profile in profiles]
            config = {"schemaVersion": 1, "interfaceId": "sltbr0", "bindIPv4": "10.77.0.1",
                      "allowedCidr": "10.77.0.0/24", "mode": "EXTERNAL_DHCP", "httpPort": 8088,
                      "maxConcurrentTransfers": 8}
            launch = {"config": config, "storePath": str(store), "profiles": profiles, "laboratory": True}
            service_starts = 0
            service_log = None

            def start_boot_service(mode=None):
                nonlocal service_starts, service_log
                name = "boot-service" if service_starts == 0 else f"boot-service-restart-{service_starts}"
                service_starts += 1
                service_log = args.output / f"{name}.log"
                process = start(["dotnet", str(args.service_dll), "--lab"], name, stdin=subprocess.PIPE)
                payload = launch if mode is None else {**launch, "config": {**config, "mode": mode}}
                process.stdin.write((json.dumps(payload) + "\n").encode())
                process.stdin.flush()
                wait_for(lambda: '"state":"RUNNING"' in service_log.read_text(errors="replace"), "boot service RUNNING", 90)
                progress(f"SLT boot service RUNNING ({name})")
                return process

            def transmitted_bytes():
                disciplines = json.loads(run("tc", "-j", "-s", "qdisc", "show", "dev", "slttap0"))
                discipline = next(item for item in disciplines if item["kind"] == "tbf")
                count = discipline.get("bytes", discipline.get("stats", {}).get("bytes"))
                assert isinstance(count, int), discipline
                return count

            def stop_boot_service(process):
                process.stdin.write(b"STOP\n")
                process.stdin.flush()
                process.wait(timeout=15)
                assert process.returncode == 0
                # Prove listener release, not only that the child exited. These
                # checks stay within the disposable namespace and send no UDP.
                with socket.socket() as probe:
                    probe.settimeout(2)
                    assert probe.connect_ex(("10.77.0.1", 8088)) != 0, "HTTP listener remained after stop"
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                    probe.bind(("10.77.0.1", 69))
                progress("SLT boot service stopped; HTTP and TFTP sockets released")

            service = start_boot_service()
            dnsconfig = private / "dnsmasq.conf"
            dnsconfig.write_text("\n".join([
                "port=0", "interface=sltbr0", "bind-interfaces", "except-interface=lo", "log-dhcp",
                "dhcp-authoritative", "dhcp-range=10.77.0.20,10.77.0.40,255.255.255.0,5m",
                "dhcp-option=option:router", "dhcp-option=option:dns-server", "dhcp-no-override",
                "dhcp-match=set:efi64,option:client-arch,7",
                "dhcp-match=set:efi64,option:client-arch,9",
                # SLT loaders embed DHCP + chain tftp://${next-server}/autoexec.ipxe.
                # Always chainload them, even QEMU's built-in BIOS iPXE ROM:
                # the gate must exercise SLT's real TFTP, not bypass it via HTTP.
                "dhcp-boot=tag:!efi64,undionly.kpxe,,10.77.0.1",
                "dhcp-boot=tag:efi64,ipxe.efi,,10.77.0.1",
                f"dhcp-leasefile={private / 'leases'}", "user=root", ""]))
            dhcp = start(["dnsmasq", "--keep-in-foreground", "--conf-file=" + str(dnsconfig)], "external-dhcp")
            time.sleep(0.3)
            assert dhcp.poll() is None
            progress("External lab DHCP running exclusively in isolated namespace")
            connection = None
            if args.controller_dll:
                controller = start(["dotnet", str(args.controller_dll), "--state-dir", str(private / "controller"),
                                    "--bind", "10.77.0.1", "--allowed-subnet", "10.77.0.0/24",
                                    *(["--lab-rescue-source", str(slt_source)] if slt_source else [])], "controller")
                connection = wait_for(lambda: json.loads((private / "controller/connection.json").read_text()), "Controller identity")

            def request(method, path, body=None):
                context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
                context.check_hostname = False
                context.verify_mode = ssl.CERT_NONE  # Exact DER pin checked before HTTP/token bytes.
                client = http.client.HTTPSConnection("127.0.0.1", 7443, context=context, timeout=10)
                client.connect()
                assert hashlib.sha256(client.sock.getpeercert(binary_form=True)).hexdigest() == connection["serverPin"]
                client.request(method, path, json.dumps(body) if body is not None else None,
                               {"Content-Type": "application/json", "Authorization": "Bearer " + connection["token"]})
                response = client.getresponse()
                payload = response.read()
                client.close()
                assert response.status == 200, (response.status, payload[:300])
                return json.loads(payload) if payload else None

            if connection:
                wait_for(lambda: request("GET", "/health"), "Controller HTTPS")
                progress("Controller HTTPS ready; administrative token stays in private temporary state")
            for index, mode in enumerate(modes):
                progress(f"Starting {mode.upper()} positive PXE boot")
                fixture = private / f"{mode}-synthetic.raw"
                fixture.write_bytes(bytes(n % 251 for n in range(8 * 1024 * 1024)))
                fixture_hash = digest(fixture)
                qmp_path, serial_path = private / "qmp.sock", private / "serial.sock"
                qmp_path.unlink(missing_ok=True)
                serial_path.unlink(missing_ok=True)
                firmware = []
                if mode == "uefi":
                    code, template = firmware_files()
                    variables = private / "OVMF_VARS.fd"
                    shutil.copyfile(template, variables)
                    firmware = ["-drive", f"if=pflash,format=raw,unit=0,readonly=on,file={code}",
                                "-drive", f"if=pflash,format=raw,unit=1,file={variables}"]
                vm_command = ["qemu-system-x86_64", "-no-user-config", "-nodefaults", "-device", "VGA", "-machine", "q35,accel=tcg", "-cpu", "max",
                            "-m", str(args.memory_mib), "-smp", "2", "-display", "none", "-no-reboot",
                            "-qmp", f"unix:{qmp_path},server=on,wait=off", "-serial", f"unix:{serial_path},server=on,wait=off",
                            "-boot", "order=n,menu=off", "-netdev", "tap,id=net0,ifname=slttap0,script=no,downscript=no",
                            "-device", f"e1000,netdev=net0,mac=52:54:00:77:00:{index + 1:02x}",
                            "-drive", f"if=none,id=fixture,file={fixture},format=raw,readonly=on",
                            "-device", "virtio-blk-pci,drive=fixture,serial=SLT-PXE-FIXTURE",
                            "-smbios", "type=1,manufacturer=SLT_QEMU_ONLY,product=SLT_PXE_LAB", *firmware]
                vm = start(vm_command, mode + "-qemu")
                console, qmp = None, None
                started = time.monotonic()
                try:
                    wait_for(lambda: serial_path.exists() and qmp_path.exists(), "QEMU console")
                    console = GuestConsole(serial_path, args.output / f"{mode}.serial.log")
                    qmp = Qmp(qmp_path)
                    console.qmp = qmp
                    console.expect("SLT Network Boot", 180)
                    progress(f"{mode.upper()} PXE menu reached")
                    menu_seconds = time.monotonic() - started
                    qmp.execute("screendump", {"filename": str(args.output / f"{mode}-menu.ppm")})
                    qmp.key("down")
                    time.sleep(0.2)
                    qmp.key("ret")
                    console.expect("SLT_RESCUE_BOOT_READY:" + mode.upper(), 600)
                    progress(f"{mode.upper()} Rescue Linux userspace readiness confirmed")
                    boot_seconds = time.monotonic() - started
                    console.login("SLT_PXE_LOGIN_OK")
                    progress(f"{mode.upper()} local technician login and sudo confirmed")
                    console.command("ip -4 route; ip -4 addr; printf '%s%s\\n' 'SLT_PXE_' 'NETWORK_OK'", "SLT_PXE_NETWORK_OK")
                    console.command("sudo -n smartctl -a /dev/vda; printf 'SMART_EXIT=%s\\n' \"$?\"; printf '%s%s\\n' 'SLT_PXE_' 'SMART_CHECKED'", "SLT_PXE_SMART_CHECKED")
                    qmp.execute("screendump", {"filename": str(args.output / f"{mode}-rescue.ppm")})
                    entry = {"firmware": mode, "menuSeconds": round(menu_seconds, 2), "bootSeconds": round(boot_seconds, 2),
                             "syntheticSha256Before": fixture_hash, "bootReady": True, "localLogin": True,
                             "smartDiagnosticAttempted": True, "smartAttributesExpected": False,
                             "inventoryAfterBootServiceStop": False}
                    if connection:
                        old_devices = {d["id"] for d in request("GET", "/api/v1/devices")}
                        offset = len(console.text)
                        console.send("sudo slt-start\n")
                        progress(f"{mode.upper()} launching local slt-start in its default mode without physical write")
                        console.expect("Escolha \\[1\\]:", 30, offset)
                        console.send("1\n")
                        console.expect("Endere", 45, offset)
                        console.send("https://10.77.0.1:7443\n")
                        console.expect("64 caracteres", 30, offset)
                        console.send(connection["serverPin"] + "\n")
                        console.expect("Modo \\[1\\]:", 30, offset)
                        console.send("1\n")
                        pending = wait_for(lambda: next((e for e in request("GET", "/api/v1/enrollments") if e["status"] == "PENDING"), None), "guest CSR enrollment", 90)
                        progress(f"{mode.upper()} CSR enrollment received; checking code on local console")
                        console.expect(re.escape(pending["code"]), 30, offset, label="pairing code shown on guest console")
                        request("POST", f"/api/v1/enrollments/{pending['id']}/approve", {"code": pending["code"]})
                        device = wait_for(lambda: next((d for d in request("GET", "/api/v1/devices") if d["id"] not in old_devices and d.get("inventory")), None), "guest mTLS inventory", 120)
                        disk = next(d for d in device["inventory"]["disks"] if d["serial"] == "SLT-PXE-FIXTURE")
                        assert disk["isReadOnly"] and disk["sizeBytes"] == fixture.stat().st_size
                        progress(f"{mode.upper()} individual certificate/mTLS inventory received; synthetic disk is readonly")
                        # fetch= has loaded the Live filesystem into RAM. Prove
                        # the already enrolled agent can still perform a NEW
                        # signed job after the entire boot distribution process
                        # (HTTP/TFTP) exits, while Controller HTTPS remains live.
                        stop_boot_service(service)
                        assert service.poll() == 0 and vm.poll() is None and controller.poll() is None
                        assert request("GET", "/health"), "Controller stopped responding after boot service shutdown"
                        assert digest(fixture) == fixture_hash
                        entry["postBootServiceStop"] = {"serviceExited": True, "returnCode": service.returncode,
                            "httpAndTftpReleased": True, "controllerHealthResponding": True,
                            "existingGuestRetained": True}
                        progress(f"{mode.upper()} boot HTTP/TFTP process exited after enrollment; Controller HTTPS still responds; submitting a new signed INVENTORY")
                        job = request("POST", "/api/v1/jobs/plan", {"kind": "INVENTORY", "deviceId": device["id"], "diskFingerprint": disk["fingerprint"], "consent": True})
                        request("POST", f"/api/v1/jobs/{job['id']}/approve", {"challenge": job["plan"]["challenge"]})
                        final = wait_for(lambda: next((j for j in request("GET", "/api/v1/jobs") if j["id"] == job["id"] and j["state"] in ("COMPLETED", "FAILED")), None), "signed INVENTORY job", 90)
                        assert final["state"] == "COMPLETED", final
                        assert service.poll() == 0 and vm.poll() is None and controller.poll() is None
                        assert request("GET", "/health") and digest(fixture) == fixture_hash
                        progress(f"{mode.upper()} signed INVENTORY completed with boot HTTP/TFTP still stopped; existing agent and readonly data remain intact")
                        entry["csrMtlsInventory"] = True
                        entry["signedInventoryJob"] = True
                        entry["inventoryAfterBootServiceStop"] = True
                        entry["postBootServiceStop"]["signedJobState"] = final["state"]
                        entry["postBootServiceStop"]["syntheticSha256After"] = digest(fixture)
                        request("POST", f"/api/v1/devices/{device['id']}/revoke", {})
                        # The next negative test must independently reach a new
                        # menu before its own server outage; restore boot first.
                        service = start_boot_service()
                    qmp.execute("quit")
                    vm.wait(timeout=15)
                    entry["syntheticSha256After"] = digest(fixture)
                    assert entry["syntheticSha256After"] == fixture_hash
                    results["firmware"].append(entry)
                    print(f"PASS {mode}: actual PXE menu, Rescue userspace, local login, readonly fixture intact", flush=True)
                finally:
                    if qmp:
                        qmp.close()
                    if console:
                        console.close()
                    if vm.poll() is None:
                        vm.terminate()
                        vm.wait(timeout=15)
                # Fresh firmware/guest memory: no kernel/initrd/rootfs from the
                # successful VM can leak into the negative pre-filesystem gate.
                qmp_path.unlink(missing_ok=True)
                serial_path.unlink(missing_ok=True)
                if mode == "uefi":
                    shutil.copyfile(template, variables)
                vm = start(vm_command, mode + "-outage-qemu")
                progress(f"Starting {mode.upper()} negative boot: stop server after menu, before kernel/filesystem")
                console, qmp = None, None
                try:
                    wait_for(lambda: serial_path.exists() and qmp_path.exists(), "outage QEMU console")
                    console = GuestConsole(serial_path, args.output / f"{mode}-outage.serial.log")
                    qmp = Qmp(qmp_path)
                    console.qmp = qmp
                    console.expect("SLT Network Boot", 180)
                    progress(f"{mode.upper()} negative boot menu reached; stopping SLT boot service")
                    assert "SLT_RESCUE_BOOT_READY:" not in console.pump()
                    stop_boot_service(service)
                    failed_at = time.monotonic()
                    qmp.key("down")
                    time.sleep(0.2)
                    qmp.key("ret")
                    failure_evidence = failure_observer.observe_failure(console, qmp, args.output, mode + "-outage", terminal_text,
                        selected_at=time.monotonic(), timeout=180)
                    assert failure_evidence["passed"]
                    progress(f"{mode.upper()} explicit boot failure confirmed via {failure_evidence['source']}")
                    # Also observe the renderer's exit/fallback, rather than
                    # stopping the VM immediately after the first error line.
                    until = time.monotonic() + 10
                    while time.monotonic() < until:
                        assert "SLT_RESCUE_BOOT_READY:" not in console.pump()
                        assert vm.poll() is None, "VM exited unexpectedly during outage observation"
                        time.sleep(0.2)
                    qmp.execute("screendump", {"filename": str(args.output / f"{mode}-outage.ppm")})
                    assert digest(fixture) == fixture_hash
                    results["outages"].append({"firmware": mode, "stage": "AFTER_MENU_BEFORE_KERNEL",
                        "serviceStopped": True, "httpAndTftpReleased": True, "explicitBootFailure": True,
                        "failureEvidenceSource": failure_evidence["source"],
                        "failureEvidenceReport": mode + "-outage-visual-observation.json",
                        "serialExplicitFailureObserved": failure_evidence["serialExplicitFailureObserved"],
                        "visualCorroboration": failure_evidence["visualCorroboration"],
                        "userspaceReached": False, "observedSeconds": round(time.monotonic() - failed_at, 2),
                        "syntheticSha256Before": fixture_hash, "syntheticSha256After": digest(fixture)})
                    qmp.execute("quit")
                    vm.wait(timeout=15)
                    print(f"PASS {mode}: server lost before kernel/filesystem; explicit failure, no userspace, readonly fixture intact", flush=True)
                finally:
                    if qmp:
                        qmp.close()
                    if console:
                        console.close()
                    if vm.poll() is None:
                        vm.terminate()
                        vm.wait(timeout=15)
                # A separate throttled boot proves interruption while rootfs
                # bytes are in flight, not only an unavailable pre-boot server.
                service = start_boot_service()
                current_service_log = service_log
                log_offset = current_service_log.stat().st_size
                qmp_path.unlink(missing_ok=True)
                serial_path.unlink(missing_ok=True)
                if mode == "uefi":
                    shutil.copyfile(template, variables)
                run("tc", "qdisc", "add", "dev", "slttap0", "root", "tbf", "rate", "16mbit", "burst", "64kb", "latency", "100ms")
                progress(f"Starting {mode.upper()} negative boot: interrupt rootfs fetch with isolated NIC limited to 16 Mbit/s")
                vm = None
                console, qmp = None, None
                try:
                    vm = start(vm_command, mode + "-fetch-outage-qemu")
                    wait_for(lambda: serial_path.exists() and qmp_path.exists(), "fetch outage QEMU console")
                    console = GuestConsole(serial_path, args.output / f"{mode}-fetch-outage.serial.log")
                    qmp = Qmp(qmp_path)
                    console.qmp = qmp
                    console.expect("SLT Network Boot", 180)
                    progress(f"{mode.upper()} throttled PXE menu reached; waiting for real rootfs HTTP transfer")
                    qmp.key("down")
                    time.sleep(0.2)
                    qmp.key("ret")

                    def rootfs_started():
                        console.pump()
                        assert "SLT_RESCUE_BOOT_READY:" not in console.text
                        for line in current_service_log.read_text(errors="replace")[log_offset:].splitlines():
                            try:
                                event = json.loads(line)
                            except ValueError:
                                continue
                            if event.get("event") == "HTTP_BEGIN" and "-rootfs" in event.get("message", ""):
                                return event
                        return None

                    try:
                        event = wait_for(rootfs_started, "actual rootfs HTTP_BEGIN (new service log)", 300)
                    except AssertionError:
                        console.diagnostics("actual rootfs HTTP_BEGIN in new service log")
                        raise
                    progress(f"{mode.upper()} rootfs HTTP_BEGIN observed; measuring partial network transfer")
                    bytes_before = transmitted_bytes()
                    time.sleep(1)
                    bytes_after = transmitted_bytes()
                    delta = bytes_after - bytes_before
                    rootfs_size = next(a["sizeBytes"] for p in profiles if p["kind"] == "RESCUE_LINUX" for a in p["artifacts"] if a["id"] == "rootfs")
                    assert 65536 < delta < rootfs_size, (delta, rootfs_size)
                    progress(f"{mode.upper()} observed {delta} network bytes after rootfs HTTP_BEGIN; stopping boot service")
                    # Namespace traffic since HTTP_BEGIN consists of this boot
                    # and its TCP framing. At 16 Mbit/s, even the bounded 3-second
                    # graceful stop cannot finish the hundreds-of-MiB rootfs.
                    failed_at = time.monotonic()
                    stop_boot_service(service)
                    failure = console.expect(r"(?i)(wget:.*(?:closed|reset|refused|error|failed|short read)|connection reset|Unable to find.*(?:live|medium)|SQUASHFS error|ALERT!|\(initramfs\))", 180)
                    until = time.monotonic() + 10
                    while time.monotonic() < until:
                        assert "SLT_RESCUE_BOOT_READY:" not in console.pump()
                        assert vm.poll() is None
                        time.sleep(0.2)
                    qmp.execute("screendump", {"filename": str(args.output / f"{mode}-fetch-outage.ppm")})
                    assert digest(fixture) == fixture_hash
                    results["fetchOutages"].append({"firmware": mode, "stage": "DURING_ROOTFS_FETCH",
                        "rootfsHttpBegin": event, "linkRateMbit": 16, "trafficBytesAfterHttpBeginBeforeStop": delta,
                        "rootfsSizeBytes": rootfs_size, "partialTransferObserved": True, "serviceStopped": True,
                        "explicitFetchFailure": failure.group(0), "userspaceReached": False,
                        "observedSecondsAfterStop": round(time.monotonic() - failed_at, 2),
                        "syntheticSha256Before": fixture_hash, "syntheticSha256After": digest(fixture)})
                    qmp.execute("quit")
                    vm.wait(timeout=15)
                    print(f"PASS {mode}: rootfs transfer interrupted after {delta} network bytes; explicit failure, no userspace, readonly fixture intact", flush=True)
                finally:
                    if qmp:
                        qmp.close()
                    if console:
                        console.close()
                    if vm is not None and vm.poll() is None:
                        vm.terminate()
                        vm.wait(timeout=15)
                    run("tc", "qdisc", "del", "dev", "slttap0", "root")
                if local_disk:
                    # Service remains stopped after the outage. Retain the NIC
                    # and external DHCP, so firmware actually attempts PXE with
                    # the unavailable SLT TFTP before its local disk fallback.
                    assert service.poll() is not None and dhcp.poll() is None
                    qmp_path.unlink(missing_ok=True)
                    serial_path.unlink(missing_ok=True)
                    if mode == "uefi":
                        shutil.copyfile(template, variables)
                    local_command = list(vm_command)
                    local_command[local_command.index("order=n,menu=off")] = "order=nc,menu=off"
                    local_command[local_command.index(f"e1000,netdev=net0,mac=52:54:00:77:00:{index + 1:02x}")] += ",bootindex=1"
                    local_command += ["-drive", f"if=none,id=local-system,file={local_disk},format=raw,readonly=on",
                        "-device", "virtio-blk-pci,drive=local-system,serial=SLT-LOCAL-SYSTEM,bootindex=2"]
                    vm = start(local_command, mode + "-local-fallback-qemu")
                    progress(f"Starting {mode.upper()} local Linux fallback with SLT boot disabled and Ethernet retained")
                    console, qmp = None, None
                    began = time.monotonic()
                    try:
                        wait_for(lambda: serial_path.exists() and qmp_path.exists(), "local fallback QEMU console")
                        console = GuestConsole(serial_path, args.output / f"{mode}-local-fallback.serial.log")
                        qmp = Qmp(qmp_path)
                        console.qmp = qmp
                        console.expect("SLT_RESCUE_BOOT_READY:" + mode.upper(), 600)
                        progress(f"{mode.upper()} local Linux userspace readiness confirmed")
                        assert "SLT Network Boot" not in console.pump(), "Boot menu unexpectedly served while SLT boot service was stopped"
                        console.login("SLT_LOCAL_LOGIN_OK")
                        progress(f"{mode.upper()} local-disk technician login confirmed; checking block-device boot provenance")
                        # A userspace readiness marker alone could be confused
                        # with PXE. Prove no fetch= parameter and a local virtio
                        # block device as live medium, then record real login.
                        console.command("cat /proc/cmdline; findmnt /run/live/medium; lsblk -d -o NAME,RO,SERIAL; "
                            "! grep -Eq '(^| )fetch=' /proc/cmdline && "
                            "findmnt -n -o SOURCE /run/live/medium | grep -Eq '^/dev/vdb[0-9]*$' && "
                            "printf '%s%s\\n' 'SLT_LOCAL_' 'DISK_CONFIRMED'", "SLT_LOCAL_DISK_CONFIRMED")
                        qmp.execute("screendump", {"filename": str(args.output / f"{mode}-local-fallback.ppm")})
                        assert digest(fixture) == fixture_hash and digest(local_disk) == local_hash
                        results["localFallbacks"].append({"firmware": mode, "bootServiceStopped": True,
                            "ethernetRetained": True, "bootOrder": "NETWORK_THEN_LOCAL_DISK",
                            "realLinuxUserspace": True, "localLogin": True, "noFetchParameter": True,
                            "liveMedium": "/dev/vdb (or its partition)", "installedPersistentSystem": False,
                            "bootSeconds": round(time.monotonic() - began, 2), "localSystemSha256Before": local_hash,
                            "localSystemSha256After": digest(local_disk), "syntheticDataSha256Before": fixture_hash,
                            "syntheticDataSha256After": digest(fixture)})
                        qmp.execute("quit")
                        vm.wait(timeout=15)
                        print(f"PASS {mode}: SLT boot disabled, real Linux fixture started from local readonly virtio disk; hashes intact", flush=True)
                    finally:
                        if qmp:
                            qmp.close()
                        if console:
                            console.close()
                        if vm.poll() is None:
                            vm.terminate()
                            vm.wait(timeout=15)
                service = start_boot_service()
            if connection and slt_source and "uefi" in modes:
                results["sltMode"] = slt_mode_scenario(args, private, start, request, connection, firmware_files, digest(store / bootstrap["id"] / "uefi-local"))
            if set(modes) == {"bios", "uefi"}:
                # ProxyDHCP: the lab DHCP server stops announcing any boot information. Only SLT's
                # optional ProxyDHCP tells PXE clients what to load, and it must never hand out an address.
                progress("Starting ProxyDHCP scenario: plain DHCP server without boot options")
                stop_boot_service(service)
                dhcp.terminate()
                dhcp.wait(timeout=10)
                plain_leases = private / "leases-plain"
                plain_config = private / "dnsmasq-plain.conf"
                plain_config.write_text("\n".join([
                    "port=0", "interface=sltbr0", "bind-interfaces", "except-interface=lo", "dhcp-authoritative",
                    "dhcp-range=10.77.0.60,10.77.0.90,255.255.255.0,5m", "dhcp-option=option:router", "dhcp-option=option:dns-server",
                    f"dhcp-leasefile={plain_leases}", "user=root", ""]))
                # No boot directive of any kind (the private folder name itself contains "pxe").
                assert not any(line.split("=")[0] in ("dhcp-boot", "dhcp-match", "pxe-service", "pxe-prompt", "enable-tftp", "dhcp-option-force") for line in plain_config.read_text().splitlines())
                dhcp = start(["dnsmasq", "--keep-in-foreground", "--conf-file=" + str(plain_config)], "external-dhcp-plain")
                time.sleep(0.3)
                assert dhcp.poll() is None

                def leased(mac):
                    return plain_leases.exists() and any(mac in line.lower() and " 10.77.0." in line for line in plain_leases.read_text().splitlines())

                def proxy_vm(mode, mac, name):
                    for stale in (private / "qmp.sock", private / "serial.sock"):
                        stale.unlink(missing_ok=True)
                    firmware = []
                    if mode == "uefi":
                        code, template = firmware_files()
                        variables = private / "OVMF_VARS.fd"
                        shutil.copyfile(template, variables)
                        firmware = ["-drive", f"if=pflash,format=raw,unit=0,readonly=on,file={code}", "-drive", f"if=pflash,format=raw,unit=1,file={variables}"]
                    return start(["qemu-system-x86_64", "-no-user-config", "-nodefaults", "-device", "VGA", "-machine", "q35,accel=tcg", "-cpu", "max",
                                  "-m", "1024", "-smp", "2", "-display", "none", "-no-reboot",
                                  "-qmp", f"unix:{private / 'qmp.sock'},server=on,wait=off", "-serial", f"unix:{private / 'serial.sock'},server=on,wait=off",
                                  "-boot", "order=n,menu=off", "-netdev", "tap,id=net0,ifname=slttap0,script=no,downscript=no",
                                  "-device", f"e1000,netdev=net0,mac={mac}", "-smbios", "type=1,manufacturer=SLT_QEMU_ONLY,product=SLT_PROXYDHCP_LAB", *firmware], name)

                def address_only(mac, name):
                    """A PXE client gets its address from the network's DHCP server and no SLT boot menu."""
                    vm = proxy_vm("bios", mac, name)
                    console = None
                    try:
                        wait_for(lambda: (private / "serial.sock").exists(), "ProxyDHCP control VM console")
                        console = GuestConsole(private / "serial.sock", args.output / f"{name}.serial.log")
                        wait_for(lambda: leased(mac), f"plain DHCP lease for {mac}", 120)
                        time.sleep(8)
                        assert "SLT Network Boot" not in console.pump(), "boot menu reached without SLT boot information"
                    finally:
                        if console:
                            console.close()
                        vm.terminate()
                        vm.wait(timeout=15)

                proxy_result = {"externalDhcpBootOptions": False, "menus": []}
                address_only("52:54:00:77:00:7a", "proxy-before-qemu")
                proxy_result["dhcpWorkingBeforeProxy"] = True
                progress("ProxyDHCP: with the service off, the client gets an address from the lab DHCP and no boot menu")
                service = start_boot_service("PROXY_DHCP")
                for mode, mac in (("bios", "52:54:00:77:00:7b"), ("uefi", "52:54:00:77:00:7c")):
                    vm = proxy_vm(mode, mac, f"proxy-{mode}-qemu")
                    console = None
                    try:
                        wait_for(lambda: (private / "serial.sock").exists(), "ProxyDHCP VM console")
                        console = GuestConsole(private / "serial.sock", args.output / f"proxy-{mode}.serial.log")
                        console.expect("SLT Network Boot", 240, label=f"{mode} PXE menu through ProxyDHCP")
                        events = service_log.read_text(errors="replace")
                        tag = mac.replace(":", "").upper()
                        assert leased(mac), "client address did not come from the lab DHCP server"
                        assert f'"event":"PROXY"' in events and tag in events and "nenhum endere" in events
                        assert not any(mac in line.lower() and " 10.77.0.1 " in line for line in plain_leases.read_text().splitlines())
                        proxy_result["menus"].append({"firmware": mode, "menuReached": True, "addressFromExternalDhcp": True, "proxyOfferedAddress": False})
                        progress(f"ProxyDHCP: {mode.upper()} client reached the SLT menu; its address came from the lab DHCP server")
                    finally:
                        if console:
                            console.close()
                        vm.terminate()
                        vm.wait(timeout=15)
                assert dhcp.poll() is None
                proxy_result["dhcpWorkingDuringProxy"] = True
                stop_boot_service(service)
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                    probe.bind(("10.77.0.1", 4011))
                address_only("52:54:00:77:00:7d", "proxy-after-qemu")
                assert dhcp.poll() is None
                proxy_result["dhcpWorkingAfterProxy"] = True
                progress("ProxyDHCP: after stopping the service the lab DHCP keeps leasing and no boot menu is offered")
                results["proxyDhcp"] = proxy_result
                print("PASS proxydhcp: BIOS and UEFI PXE menus with a DHCP server that sends no boot options; SLT offered no address; DHCP unaffected before, during and after", flush=True)
                service = start_boot_service()
            stop_boot_service(service)
            results["serviceStopped"] = True
            results["serviceStarts"] = service_starts
            results["success"] = True
            results["proxyDhcpGatePassed"] = len(results.get("proxyDhcp", {}).get("menus", [])) == 2 and bool(results["proxyDhcp"].get("dhcpWorkingAfterProxy"))
            results["sltModeGatePassed"] = bool(results.get("sltMode", {}).get("formatVerified"))
            results["bootAndInventoryGatePassed"] = bool(args.controller_dll) and set(modes) == {"bios", "uefi"} and all(
                entry.get("csrMtlsInventory") and entry.get("signedInventoryJob") for entry in results["firmware"])
            results["postBootServiceStopGatePassed"] = bool(args.controller_dll) and set(modes) == {"bios", "uefi"} and len(results["firmware"]) == 2 and all(
                entry.get("inventoryAfterBootServiceStop") is True for entry in results["firmware"])
            results["preFilesystemFailureGatePassed"] = set(modes) == {"bios", "uefi"} and len(results["outages"]) == 2
            results["duringFilesystemFailureGatePassed"] = set(modes) == {"bios", "uefi"} and len(results["fetchOutages"]) == 2
            results["localLinuxFallbackGatePassed"] = bool(local_disk) and set(modes) == {"bios", "uefi"} and len(results["localFallbacks"]) == 2
    finally:
        # Repeated termination signals must not abort cleanup halfway through.
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        progress("Stopping all remaining owned lab process groups")
        remaining = [process for process in reversed(processes) if process.poll() is None]
        # Send termination to every owned group before waiting. A cancelled CI
        # job must not spend ten seconds per process before preserving evidence.
        for process in remaining:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        deadline = time.monotonic() + 6
        for process in remaining:
            try:
                process.wait(timeout=max(0, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                pass
        for process in remaining:
            if process.poll() is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
        for process in remaining:
            process.wait(timeout=2)
        for log in logs:
            log.close()
        results["allOwnedProcessesStopped"] = all(process.poll() is not None for process in processes)
        (args.output / "receipt.json").write_text(json.dumps(results, indent=2) + "\n")


if __name__ == "__main__":
    main()
