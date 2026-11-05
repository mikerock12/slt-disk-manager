#!/usr/bin/env python3
"""Real BIOS/UEFI PXE lab using a disposable Linux network namespace.

Requires root, unshare, iproute2, dnsmasq, QEMU, OVMF and dotnet. Never attaches
an ISO/USB or host disk. DHCP is a lab-only external dnsmasq, not SLT's service.
Evidence excludes the Controller state directory (individual keys/admin token).
"""
from __future__ import annotations

import argparse
import hashlib
import http.client
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
            results["profiles"] = [{"id": profile["id"], "version": profile["version"],
                                    "artifacts": [{"id": artifact["id"], "sizeBytes": artifact["sizeBytes"], "sha256": artifact["sha256"]}
                                                  for artifact in profile["artifacts"]]} for profile in profiles]
            config = {"schemaVersion": 1, "interfaceId": "sltbr0", "bindIPv4": "10.77.0.1",
                      "allowedCidr": "10.77.0.0/24", "mode": "EXTERNAL_DHCP", "httpPort": 8088,
                      "maxConcurrentTransfers": 8}
            launch = {"config": config, "storePath": str(store), "profiles": profiles, "laboratory": True}
            service_starts = 0
            service_log = None

            def start_boot_service():
                nonlocal service_starts, service_log
                name = "boot-service" if service_starts == 0 else f"boot-service-restart-{service_starts}"
                service_starts += 1
                service_log = args.output / f"{name}.log"
                process = start(["dotnet", str(args.service_dll), "--lab"], name, stdin=subprocess.PIPE)
                process.stdin.write((json.dumps(launch) + "\n").encode())
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
                                    "--bind", "10.77.0.1", "--allowed-subnet", "10.77.0.0/24"], "controller")
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
                             "smartDiagnosticAttempted": True, "smartAttributesExpected": False}
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
                        job = request("POST", "/api/v1/jobs/plan", {"kind": "INVENTORY", "deviceId": device["id"], "diskFingerprint": disk["fingerprint"], "consent": True})
                        request("POST", f"/api/v1/jobs/{job['id']}/approve", {"challenge": job["plan"]["challenge"]})
                        final = wait_for(lambda: next((j for j in request("GET", "/api/v1/jobs") if j["id"] == job["id"] and j["state"] in ("COMPLETED", "FAILED")), None), "signed INVENTORY job", 90)
                        assert final["state"] == "COMPLETED", final
                        progress(f"{mode.upper()} signed INVENTORY job completed")
                        entry["csrMtlsInventory"] = True
                        entry["signedInventoryJob"] = True
                        request("POST", f"/api/v1/devices/{device['id']}/revoke", {})
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
                    console.expect("Falha de boot", 180)
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
            stop_boot_service(service)
            results["serviceStopped"] = True
            results["serviceStarts"] = service_starts
            results["success"] = True
            results["bootAndInventoryGatePassed"] = bool(args.controller_dll) and set(modes) == {"bios", "uefi"} and all(
                entry.get("csrMtlsInventory") and entry.get("signedInventoryJob") for entry in results["firmware"])
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
