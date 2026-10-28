"""Full SLT Disk Manager lab in local QEMU VMs (WHPX when available, TCG otherwise).

Every writable disk is a NEW qcow2 file inside runtime/lab-<id>; no host disk, bridge, TAP,
passthrough or host boot change is used. The operator's ISOs stay read-only and are never
uploaded. VMs are driven only through their own console (serial for SLT Rescue, virtual
keyboard for WinPE/Windows): the same local confirmations the product requires.

Phases (in order, each resumable with --workspace):
  install    client1 (64 GiB, GPT/UEFI) receives Xubuntu by DEPLOY_LINUX from SLT Rescue while
             client2 (128 GiB, MBR/BIOS) receives Windows 10 by DEPLOY_WINDOWS from WinPE.
  firstboot  both installed systems boot from disk: Xubuntu greeter and Windows desktop.
  clone      both boot SLT Rescue; batch FIT clone client2 -> client1 (Windows shrinks).
  convert    client1 boots WinPE in UEFI; CONVERT_GPT with MBR2GPT from the Windows media.
  boot       client1 boots the converted disk in UEFI up to the Windows desktop.
Screenshots, serial logs and evidence.json stay in the workspace for review.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import socket
import ssl
import subprocess
import threading
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
HIDDEN = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
QEMU_DIR = ROOT / ".tools/qemu-20260811/bin"
WINDOWS_ISO = ROOT / "ISOs/Windows 10.iso"
XUBUNTU_ISO = ROOT / "ISOs/xubuntu-26.04-minimal-amd64.iso"
GIB = 1024 ** 3


def log(message):
    print(f"[{datetime.now():%H:%M:%S}] {message}", flush=True)


def wait_for(check, description, timeout, interval=1.0):
    deadline, last = time.monotonic() + timeout, None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, ValueError, KeyError, http.client.HTTPException, RuntimeError) as exc:
            last = exc
        time.sleep(interval)
    raise TimeoutError(f"{description}: {last}")


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class QMP:
    KEYS = {" ": ("spc", False), "-": ("minus", False), "_": ("minus", True), ".": ("dot", False), "/": ("slash", False),
            "\\": ("backslash", False), ":": ("semicolon", True), "%": ("5", True), "(": ("9", True), ")": ("0", True),
            "@": ("2", True), "\n": ("ret", False)}

    def __init__(self, port):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=30)
        self.file = self.sock.makefile("rwb", buffering=0)
        if "QMP" not in json.loads(self.file.readline()):
            raise RuntimeError("QMP inválido")
        self.seq = 0
        self.lock = threading.Lock()
        self.command("qmp_capabilities")

    def command(self, name, arguments=None):
        with self.lock:
            return self._command(name, arguments)

    def _command(self, name, arguments=None):
        self.seq += 1
        request = {"execute": name, "id": self.seq}
        if arguments is not None:
            request["arguments"] = arguments
        self.file.write(json.dumps(request).encode() + b"\r\n")
        while True:
            line = self.file.readline()
            if not line:
                raise EOFError("QMP fechado")
            response = json.loads(line)
            if response.get("id") == self.seq:
                if "error" in response:
                    raise RuntimeError(str(response["error"]))
                return response.get("return")

    def key(self, code, shift=False):
        keys = ([{"type": "qcode", "data": "shift"}] if shift else []) + [{"type": "qcode", "data": code}]
        self.command("send-key", {"keys": keys, "hold-time": 50})
        time.sleep(0.07)

    def type(self, text):
        # Only harness-generated values: challenges, temporary passwords and fixed commands.
        for ch in text:
            if ch.isascii() and ch.isalnum():
                self.key(ch.lower(), ch.isupper())
            elif ch in self.KEYS:
                self.key(*self.KEYS[ch])
            else:
                raise ValueError("caractere não permitido para digitação: " + repr(ch))

    def screenshot(self, path):
        self.command("screendump", {"filename": str(path), "format": "png"})

    def close(self):
        try:
            self.file.close()
            self.sock.close()
        except OSError:
            pass


class Serial:
    def __init__(self, port, path):
        self.sock = wait_for(lambda: socket.create_connection(("127.0.0.1", port), timeout=2), "console serial", 60)
        self.sock.settimeout(1)
        self.raw, self.lock, self.closed = bytearray(), threading.Lock(), False
        self.log = open(path, "ab")
        self.thread = threading.Thread(target=self.read, daemon=True)
        self.thread.start()

    def read(self):
        while not self.closed:
            try:
                data = self.sock.recv(65536)
            except socket.timeout:
                continue
            except OSError:
                break
            if not data:
                break
            with self.lock:
                self.raw.extend(data)
            self.log.write(data)
            self.log.flush()

    def text(self, start=0):
        with self.lock:
            value = bytes(self.raw[start:]).decode("utf-8", errors="replace")
        return re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", value)

    def mark(self):
        with self.lock:
            return len(self.raw)

    def send(self, text, enter=True):
        self.sock.sendall(text.encode() + (b"\r" if enter else b""))

    def expect(self, pattern, timeout, start=0):
        return wait_for(lambda: re.search(pattern, self.text(start)), "console: " + pattern, timeout, 0.5)

    def command(self, command, timeout=120):
        token = uuid.uuid4().hex[:10]
        start = self.mark()
        self.send(command + f'; printf "\\nSLT_RC_{token}=%s\\n" "$?"')
        match = self.expect(rf"\nSLT_RC_{token}=(\d+)", timeout, start)
        if match.group(1) != "0":
            raise RuntimeError("comando no console falhou: " + self.text(start)[-2000:])
        return self.text(start)

    def close(self):
        self.closed = True
        try:
            self.sock.close()
        except OSError:
            pass
        self.thread.join(timeout=3)
        self.log.close()


class Lab:
    def __init__(self, workspace):
        self.ws = workspace
        self.ws.mkdir(parents=True, exist_ok=True)
        self.state_path = self.ws / "lab.json"
        self.state = json.loads(self.state_path.read_text()) if self.state_path.exists() else {"createdAtUtc": datetime.now(timezone.utc).isoformat(), "port": free_port(), "evidence": {}}
        self.save()
        self.processes, self.handles, self.vms = [], [], []
        self.accel = self.detect_accel()
        self.connection = None

    def save(self):
        self.state_path.write_text(json.dumps(self.state, indent=2, ensure_ascii=False))

    def evidence(self, key, value):
        self.state["evidence"][key] = value
        self.save()
        log(f"EVIDÊNCIA {key}: {value if isinstance(value, (str, int, float, bool)) else json.dumps(value, ensure_ascii=False)[:300]}")

    def detect_accel(self):
        try:
            p = subprocess.Popen([str(QEMU_DIR / "qemu-system-x86_64.exe"), "-L", str(QEMU_DIR / "share"), "-accel", "whpx", "-machine", "q35", "-m", "128", "-nodefaults", "-display", "none", "-S", "-monitor", "none"],
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=HIDDEN)
            time.sleep(5)
            alive = p.poll() is None
            p.kill()
            output = p.communicate()[0].decode(errors="replace")
            return "whpx" if alive and "No accelerator" not in output else "tcg"
        except OSError:
            return "tcg"

    # ---- controller -------------------------------------------------------------------
    def start_controller(self):
        source = ROOT / "src/server/SLT.Controller.Host/bin/Debug/net10.0"
        binaries = self.ws / "controller-bin"
        if binaries.exists():
            shutil.rmtree(binaries)
        shutil.copytree(source, binaries, ignore=shutil.ignore_patterns("state", "runtime", "*.db", "*.pfx", "connection.json"))
        out = open(self.ws / "controller.log", "ab")
        self.handles.append(out)
        p = subprocess.Popen(["dotnet", str(binaries / "SLT.Controller.Host.dll"), "--state-dir", str(self.ws / "server"), "--port", str(self.state["port"])], stdout=out, stderr=out, creationflags=HIDDEN)
        self.processes.append(p)
        self.connection = wait_for(lambda: json.loads((self.ws / "server/connection.json").read_text()), "identidade do controlador", 60)
        wait_for(lambda: self.api("GET", "/health"), "HTTPS do controlador", 60)
        log(f"Controlador em https://127.0.0.1:{self.state['port']} pin {self.connection['serverPin'][:16]}… (VMs usam 10.0.2.2)")

    def api(self, method, path, body=None):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname, ctx.verify_mode = False, ssl.CERT_NONE  # exact DER pin checked below
        conn = http.client.HTTPSConnection("127.0.0.1", self.state["port"], context=ctx, timeout=60)
        conn.connect()
        if hashlib.sha256(conn.sock.getpeercert(binary_form=True)).hexdigest() != self.connection["serverPin"]:
            conn.close()
            raise RuntimeError("pin do controlador divergente")
        conn.request(method, path, json.dumps(body).encode() if body is not None else None, {"Authorization": "Bearer " + self.connection["token"], "Content-Type": "application/json"})
        response = conn.getresponse()
        data = response.read()
        conn.close()
        if response.status != 200:
            raise RuntimeError(f"{method} {path} -> {response.status}: {data[:600]!r}")
        return json.loads(data) if data else None

    def job(self, job_id):
        return next(j for j in self.api("GET", "/api/v1/jobs") if j["id"] == job_id)

    def wait_job(self, job_id, states, timeout, vm=None, label=""):
        last = [None, 0.0]

        def check():
            j = self.job(job_id)
            progress = j.get("progress") or {}
            text = f"{j['state']} {progress.get('phase')} {progress.get('bytesProcessed', 0)}/{progress.get('bytesTotal', 0)}"
            if text != last[0] and time.monotonic() - last[1] > 30:
                log(f"{label} {text}")
                last[0], last[1] = text, time.monotonic()
                if vm:
                    vm.shot("progress")
            if j["state"] in ("FAILED", "CANCELLED", "INTERRUPTED") and j["state"] not in states:
                raise AssertionError(f"{label} {j['state']}: {j.get('result')}")
            return j if j["state"] in states or progress.get("phase") in states else None
        return wait_for(check, label, timeout, 3)

    def import_media(self, key, path, name):
        media = self.state.get(key)
        if media:
            current = next((m for m in self.api("GET", "/api/v1/media") if m["id"] == media), None)
            if current and current["state"] == "AVAILABLE":
                return current
        m = self.api("POST", "/api/v1/media/import", {"path": str(path), "name": name})
        self.state[key] = m["id"]
        self.save()
        return m

    def wait_media(self, key):
        return wait_for(lambda: next((m for m in self.api("GET", "/api/v1/media") if m["id"] == self.state[key] and m["state"] in ("AVAILABLE", "FAILED")), None), "importação " + key, 3600, 5)

    def pending_enrollment(self, mode, exclude):
        return next((e for e in self.api("GET", "/api/v1/enrollments") if e["mode"] == mode and e["status"] == "PENDING" and e["id"] not in exclude), None)

    def close(self):
        for vm in self.vms:
            vm.stop()
        for p in reversed(self.processes):
            if p.poll() is None:
                p.terminate()
                try:
                    p.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    p.kill()
        for h in self.handles:
            h.close()


class VM:
    def __init__(self, lab, name, size_gib, uefi):
        self.lab, self.name, self.uefi = lab, name, uefi
        self.serial_number = "SLT-LAB-" + name.upper()
        self.disk = lab.ws / f"{name}.qcow2"
        if not self.disk.exists():
            subprocess.run([str(QEMU_DIR / "qemu-img.exe"), "create", "-f", "qcow2", str(self.disk), f"{size_gib}G"], check=True, creationflags=HIDDEN)
        self.vars = lab.ws / f"{name}-vars.fd"
        if uefi and not self.vars.exists():
            shutil.copyfile(QEMU_DIR / "share/edk2-i386-vars.fd", self.vars)
        self.scratch = lab.ws / f"{name}-pendrive.qcow2"
        self.process = self.qmp = self.serial = None
        self.shots = 0
        lab.vms.append(self)

    def start(self, cdrom=None, seed=None, scratch=False, memory=4096):
        assert self.process is None or self.process.poll() is not None
        qmp_port, serial_port = free_port(), free_port()
        # Under WHPX the host CPU model made Windows guests (WinPE) hang mid-download, sleep
        # forever or blue-screen. A conservative named model (SSE4.2, no x2APIC/TSC-deadline)
        # keeps them stable; the default qemu64 was stable too, but lacks SSE4 and is slower.
        cpu = "Nehalem" if self.lab.accel == "whpx" else "max"
        args = [str(QEMU_DIR / "qemu-system-x86_64.exe"), "-L", str(QEMU_DIR / "share"), "-name", f"SLT LAB {self.name}",
                "-machine", f"q35,accel={self.lab.accel}", "-cpu", cpu, "-m", str(memory), "-smp", "2", "-display", "none", "-vga", "std",
                "-rtc", "base=localtime", "-qmp", f"tcp:127.0.0.1:{qmp_port},server=on,wait=off",
                "-serial", f"tcp:127.0.0.1:{serial_port},server=on,wait=off",
                "-drive", f"file={self.disk},if=none,id=disk,format=qcow2,cache=writeback",
                "-device", f"ide-hd,drive=disk,bus=ide.0,serial={self.serial_number},bootindex={2 if cdrom else 1}",
                "-netdev", "user,id=lan,ipv6=off", "-device", f"e1000,netdev=lan,id=nic,mac=52:54:00:5a:{1 if self.name == 'client1' else 2:02x}:01"]
        if self.uefi:
            args += ["-drive", f"if=pflash,format=raw,readonly=on,file={QEMU_DIR / 'share/edk2-x86_64-code.fd'}", "-drive", f"if=pflash,format=raw,file={self.vars}"]
        if scratch:
            if not self.scratch.exists():
                subprocess.run([str(QEMU_DIR / "qemu-img.exe"), "create", "-f", "qcow2", str(self.scratch), "16G"], check=True, creationflags=HIDDEN)
            args += ["-drive", f"file={self.scratch},if=none,id=scratch,format=qcow2", "-device", "ide-hd,drive=scratch,bus=ide.1,serial=SLT-LAB-PENDRIVE"]
        if cdrom:
            args += ["-drive", f"file={cdrom},if=none,id=cd,media=cdrom,readonly=on", "-device", "ide-cd,drive=cd,bus=ide.2,bootindex=0"]
        if seed:
            args += ["-device", "qemu-xhci,id=xhci", "-drive", f"file=fat:{seed},format=raw,readonly=on,if=none,id=seed", "-device", "usb-storage,bus=xhci.0,drive=seed,removable=on,serial=SLT-LAB-KIT"]
        (self.lab.ws / f"{self.name}-qemu-args.json").write_text(json.dumps(args, indent=1))
        out = open(self.lab.ws / f"{self.name}-qemu.log", "ab")
        self.lab.handles.append(out)
        self.process = subprocess.Popen(args, stdout=out, stderr=out, creationflags=HIDDEN)
        self.qmp = wait_for(lambda: QMP(qmp_port), "QMP " + self.name, 60)
        self.serial = Serial(serial_port, self.lab.ws / f"{self.name}-serial.log")
        if self.lab.accel == "whpx":
            threading.Thread(target=self.nudge, args=(self.process, self.qmp), daemon=True).start()
        log(f"{self.name} iniciada ({self.lab.accel}, {'UEFI' if self.uefi else 'BIOS'}, cd={Path(cdrom).name if cdrom else '-'})")

    def nudge(self, process, qmp):
        # WHPX lab quirk: an idle Windows guest (WinPE or installed) sometimes stops getting its
        # timer interrupt and freezes until some input arrives; the agent then sits at 100% CPU
        # with its timeouts never firing. A 1-pixel mouse wiggle every few seconds keeps it going.
        while process.poll() is None and qmp is self.qmp:
            try:
                for value in (1, -1):
                    qmp.command("input-send-event", {"events": [{"type": "rel", "data": {"axis": "x", "value": value}}]})
            except (OSError, RuntimeError, EOFError, ValueError):
                return
            time.sleep(3)

    def shot(self, label):
        self.shots += 1
        path = self.lab.ws / "screens" / f"{self.name}-{self.shots:04d}-{label}.png"
        path.parent.mkdir(exist_ok=True)
        try:
            self.qmp.screenshot(path)
            (self.lab.ws / f"{self.name}-latest.png").write_bytes(path.read_bytes())
        except (OSError, RuntimeError, EOFError):
            pass
        return path

    def press_for_cd(self, seconds=25):
        # "Press any key to boot from CD or DVD" of Windows media, only during the first seconds.
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            self.qmp.key("ret")
            time.sleep(0.5)

    def powerdown(self, timeout=600):
        if self.process is None or self.process.poll() is not None:
            return True
        try:
            self.qmp.command("system_powerdown")
        except (OSError, RuntimeError, EOFError):
            pass
        try:
            self.process.wait(timeout=timeout)
            clean = True
        except subprocess.TimeoutExpired:
            clean = False
        self.stop()
        return clean

    def stop(self):
        if self.qmp:
            try:
                self.qmp.command("quit")
            except (OSError, RuntimeError, EOFError):
                pass
            self.qmp.close()
        if self.process and self.process.poll() is None:
            try:
                self.process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self.process.kill()
        if self.serial:
            self.serial.close()
        self.qmp = self.serial = None


# ---- SLT Rescue console --------------------------------------------------------------------
def rescue_login(vm):
    vm.serial.expect(r"SLT_RESCUE_BOOT_READY|login:", 900)
    start = vm.serial.mark()
    vm.serial.send("")
    time.sleep(2)
    if "technician@" not in vm.serial.text(start):
        vm.serial.send("technician")
        vm.serial.expect("Password:", 60, start)
        vm.serial.send("live")
        time.sleep(4)
    vm.serial.command("sudo -n true && whoami", 60)
    # Fixture isolation: the client keeps only the 10.0.2/24 link to the lab controller.
    vm.serial.command('nm-online --quiet --timeout=60; for c in $(nmcli -g NAME connection show --active); do sudo -n nmcli connection modify "$c" ipv4.never-default yes ipv6.method disabled && sudo -n nmcli connection up "$c"; done; ip -4 route', 120)


def rescue_start_agent(lab, vm, write, used):
    start = vm.serial.mark()
    vm.serial.send("sudo slt-start")
    vm.serial.expect(r"Escolha \[1\]:", 120, start)
    vm.serial.send("1")
    vm.serial.expect(r"Endereço HTTPS do servidor", 60, start)
    vm.serial.send(f"https://10.0.2.2:{lab.state['port']}")
    vm.serial.expect(r"Pin SHA-256", 30, start)
    vm.serial.send(lab.connection["serverPin"])
    vm.serial.expect(r"Modo \[1\]:", 30, start)
    if write:
        vm.serial.send("2")
        vm.serial.expect("PERMITIR GRAVACAO para habilitar", 30, start)
        vm.serial.send("PERMITIR GRAVACAO")
        vm.serial.expect(r"vazio para clone em lotes", 30, start)
        vm.serial.send("")
    else:
        vm.serial.send("1")
    match = vm.serial.expect(r"código (\d{8})", 180, start)
    enrollment = wait_for(lambda: next((e for e in lab.api("GET", "/api/v1/enrollments") if e["mode"] == "RESCUE" and e["status"] == "PENDING" and e["code"] == match.group(1) and e["id"] not in used), None), "pareamento Rescue " + vm.name, 120)
    approved = lab.api("POST", f"/api/v1/enrollments/{enrollment['id']}/approve", {"code": match.group(1)})
    used.add(enrollment["id"])
    device = wait_for(lambda: next((d for d in lab.api("GET", "/api/v1/devices") if d["id"] == approved["deviceId"] and d.get("inventory")), None), "inventário Rescue " + vm.name, 180)
    log(f"{vm.name} pareado no Rescue (código conferido no console {match.group(1)}): {device['id']}")
    return device


def rescue_confirm(lab, vm, job, label):
    lab.wait_job(job["id"], ["AWAITING_LOCAL_CONFIRMATION"], 3600, vm, label)
    vm.serial.expect(re.escape(job["plan"]["challenge"]), 120)
    vm.shot("confirmacao-local")
    vm.serial.send(job["plan"]["challenge"])
    log(f"{label}: confirmação local digitada no console de {vm.name}")


def rescue_shutdown(vm):
    try:
        vm.serial.send("\x03", enter=False)
        time.sleep(3)
        vm.serial.send("sudo -n systemctl poweroff")
    except OSError:
        pass
    try:
        vm.process.wait(timeout=180)
    except subprocess.TimeoutExpired:
        pass
    vm.stop()


# ---- WinPE kit -------------------------------------------------------------------------------
def build_kit(lab, name):
    seed = lab.ws / f"kit-{name}"
    if seed.exists():
        shutil.rmtree(seed)
    seed.mkdir()
    kit = seed / "kit"
    subprocess.run(["pwsh", "-NoProfile", "-File", str(ROOT / "rescue/windows-pe/New-WinPEKit.ps1"), "-OutputDirectory", str(kit), "-AgentPath", str(ROOT / "artifacts/slt-winpe-agent.exe"),
                    "-Server", f"https://10.0.2.2:{lab.state['port']}", "-ServerPin", lab.connection["serverPin"], "-ScratchDirectory", "R:\\SLT-Scratch"], check=True, creationflags=HIDDEN)
    for item in kit.iterdir():
        shutil.move(str(item), str(seed / item.name))
    kit.rmdir()
    marker = json.loads((seed / "SLT/kit-manifest.json").read_text(encoding="utf-8-sig"))["locatorMarker"]
    start = seed / "SLT/Start-SLT.cmd"
    text = start.read_text()
    step = '"%SystemRoot%\\System32\\wpeutil.exe" InitializeNetwork\nif errorlevel 1 goto blocked\n'
    assert text.count(step) == 1
    isolated = step + '"%SystemRoot%\\System32\\wpeutil.exe" WaitForNetwork\nif errorlevel 1 goto blocked\n"%SystemRoot%\\System32\\route.exe" delete 0.0.0.0\n"%SystemRoot%\\System32\\route.exe" print -4\n'
    # Lab only: QEMU user networking (slirp) stops forwarding a Windows guest's TCP after a few
    # hundred MB with receive-window autotuning; a fixed window keeps the download flowing.
    isolated += '"%SystemRoot%\\System32\\netsh.exe" interface tcp set global autotuninglevel=disabled\n'
    start.write_bytes(text.replace(step, isolated).replace("\r\n", "\n").replace("\n", "\r\n").encode("ascii"))
    # Fixture-only: disk 1 is the lab's NEW 16 GiB "pendrive" qcow2 on AHCI port 1; disk 0 is the client.
    (seed / "SLT/scratch-fixture.txt").write_bytes(b"select disk 1\r\nclean\r\nconvert mbr\r\ncreate partition primary\r\nformat quick fs=ntfs label=SLT-Pendrive\r\nassign letter=R\r\nexit\r\n")
    (seed / "SLT/Boot-VM.cmd").write_bytes(('@echo off\r\nreg query HKLM\\SYSTEM\\CurrentControlSet\\Control\\MiniNT >nul 2>&1\r\nif errorlevel 1 exit /b 99\r\n'
                                            'diskpart /s "%~dp0scratch-fixture.txt"\r\nif errorlevel 1 goto hold\r\ncall "%~dp0Start-SLT.cmd"\r\n:hold\r\npause >nul\r\ngoto hold\r\n').encode("ascii"))
    xml = ET.parse(seed / "Autounattend.xml")
    ns = "{urn:schemas-microsoft-com:unattend}"
    xml.find(f".//{ns}Path").text = 'cmd.exe /d /c "for %d in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\\SLT\\' + marker + ' call %d:\\SLT\\Boot-VM.cmd & cmd.exe /d /k echo SLT LAB parado."'
    keyboard = ET.SubElement(xml.find(f"{ns}settings"), f"{ns}component", {"name": "Microsoft-Windows-International-Core-WinPE", "processorArchitecture": "amd64", "publicKeyToken": "31bf3856ad364e35", "language": "neutral", "versionScope": "nonSxS"})
    ET.SubElement(keyboard, f"{ns}InputLocale").text = "0409:00000409"
    ET.register_namespace("", "urn:schemas-microsoft-com:unattend")
    ET.register_namespace("wcm", "http://schemas.microsoft.com/WMIConfig/2002/State")
    xml.write(seed / "Autounattend.xml", encoding="utf-8", xml_declaration=True)
    (seed / "boot").mkdir()
    subprocess.run([str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "7-Zip/7z.exe"), "e", str(WINDOWS_ISO), "boot/bootsect.exe", "-o" + str(seed / "boot"), "-y", "-bso0"], check=True, creationflags=HIDDEN)
    return seed


def winpe_pair(lab, vm, used):
    def find():
        if int(time.monotonic()) % 60 < 3:
            vm.shot("winpe")
        return lab.pending_enrollment("WINPE", used)
    enrollment = wait_for(find, "pareamento WinPE " + vm.name, 2400, 3)
    approved = lab.api("POST", f"/api/v1/enrollments/{enrollment['id']}/approve", {"code": enrollment["code"]})
    used.add(enrollment["id"])
    device = wait_for(lambda: next((d for d in lab.api("GET", "/api/v1/devices") if d["id"] == approved["deviceId"] and d.get("inventory")), None), "inventário WinPE " + vm.name, 300)
    vm.shot("winpe-pareado")
    log(f"{vm.name} pareado no WinPE: {device['id']} firmware={[n for n in device['inventory']['notes'] if 'firmware' in n]}")
    return device


def winpe_confirm(lab, vm, job, label):
    lab.wait_job(job["id"], ["AWAITING_LOCAL_CONFIRMATION"], 7200, vm, label)
    time.sleep(5)
    vm.shot("confirmacao-local")
    vm.qmp.type(job["plan"]["challenge"] + "\n")
    log(f"{label}: confirmação local digitada no teclado virtual de {vm.name}")


def disk_of(device, serial):
    return next(d for d in device["inventory"]["disks"] if d["serial"] == serial)


def approve(lab, job):
    return lab.api("POST", f"/api/v1/jobs/{job['id']}/approve", {"challenge": job["plan"]["challenge"]})


def windows_logon(vm, password, label, wait_minutes):
    """Waits for the logon screen produced by the unattend and signs in with the temporary password."""
    end = time.monotonic() + wait_minutes * 60
    while time.monotonic() < end:
        vm.shot(label + "-aguardando")
        time.sleep(30)
    vm.qmp.key("spc")
    time.sleep(5)
    vm.qmp.type(password + "\n")
    for _ in range(30):
        time.sleep(20)
        vm.shot(label + "-logon")


# ---- phases --------------------------------------------------------------------------------------
def phase_install(lab):
    used = set(lab.state.setdefault("usedEnrollments", []))
    lab.import_media("windowsMedia", WINDOWS_ISO, "Windows 10 (laboratório)")
    lab.import_media("linuxMedia", XUBUNTU_ISO, "Xubuntu 26.04 minimal (laboratório)")
    windows_media, linux_media = lab.wait_media("windowsMedia"), lab.wait_media("linuxMedia")
    assert windows_media["state"] == "AVAILABLE" and linux_media["state"] == "AVAILABLE" and linux_media["kind"] == "LINUX", (windows_media, linux_media)
    edition = next(i for i in windows_media["images"] if i["name"].strip().lower() == "windows 10 pro")
    lab.evidence("media", {"windows": windows_media["sha256"], "windowsEdition": edition["name"], "linux": linux_media["sha256"], "linuxName": linux_media["images"][0]["name"]})
    # Fresh blank disks, as for a new install. With no bootable hard disk the Windows media
    # boots straight into WinPE, without the "press any key" prompt a keypress could miss.
    for name in ("client1", "client2"):
        for suffix in (".qcow2", "-vars.fd"):
            (lab.ws / (name + suffix)).unlink(missing_ok=True)
    c1, c2 = VM(lab, "client1", 64, True), VM(lab, "client2", 128, False)
    seed = build_kit(lab, "client2")
    c2.start(cdrom=WINDOWS_ISO, seed=seed, scratch=True)
    c1.start(cdrom=ROOT / lab.args.rescue_iso)
    rescue_login(c1)
    d1 = rescue_start_agent(lab, c1, True, used)
    d2 = winpe_pair(lab, c2, used)
    lab.state["usedEnrollments"] = sorted(used)
    lab.save()
    assert "DEPLOY_LINUX" in d1["inventory"]["capabilities"] and "DEPLOY_WINDOWS" in d2["inventory"]["capabilities"]
    k1, k2 = disk_of(d1, c1.serial_number), disk_of(d2, c2.serial_number)
    linux_job = lab.api("POST", "/api/v1/jobs/plan", {"kind": "DEPLOY_LINUX", "deviceId": d1["id"], "diskFingerprint": k1["fingerprint"], "consent": True,
                                                       "deployment": {"mediaId": linux_media["id"], "imageIndex": 1, "firmware": "GPT_UEFI", "rebootAfterInstall": False, "account": {"computerName": "cliente1", "userName": "tecnico"}}})
    windows_job = lab.api("POST", "/api/v1/jobs/plan", {"kind": "DEPLOY_WINDOWS", "deviceId": d2["id"], "diskFingerprint": k2["fingerprint"], "consent": True,
                                                         "deployment": {"mediaId": windows_media["id"], "imageIndex": edition["index"], "firmware": "MBR_BIOS", "rebootAfterInstall": True, "account": {"computerName": "CLIENTE2", "userName": "tecnico"}}})
    lab.state["passwords"] = {"client1-linux": linux_job["temporaryPassword"], "client2-windows": windows_job["temporaryPassword"]}
    lab.state["installJobs"] = {"linux": linux_job["id"], "windows": windows_job["id"]}
    lab.save()
    approve(lab, linux_job)
    approve(lab, windows_job)
    log("Instalações aprovadas no servidor ao mesmo tempo: Xubuntu no cliente1 e Windows no cliente2")
    confirmations = [threading.Thread(target=rescue_confirm, args=(lab, c1, linux_job, "Xubuntu")), threading.Thread(target=winpe_confirm, args=(lab, c2, windows_job, "Windows"))]
    for t in confirmations:
        t.start()
    for t in confirmations:
        t.join()
    timeline = []

    def both_done():
        states = {k: lab.job(v)["state"] for k, v in lab.state["installJobs"].items()}
        timeline.append({"t": datetime.now(timezone.utc).isoformat(), **states})
        if any(s in ("FAILED", "CANCELLED", "INTERRUPTED") for s in states.values()):
            raise AssertionError(f"instalação falhou: {[lab.job(v).get('result') for v in lab.state['installJobs'].values()]}")
        if int(time.monotonic()) % 120 < 4:
            c1.shot("instalando")
            c2.shot("instalando")
        return states if all(s == "COMPLETED" for s in states.values()) else None
    wait_for(both_done, "instalações simultâneas", 4 * 3600, 4)
    overlap = [t for t in timeline if t["linux"] == "RUNNING" and t["windows"] == "RUNNING"]
    lab.evidence("installConcurrent", {"samplesBothRunning": len(overlap), "first": overlap[0]["t"] if overlap else None})
    for key in ("linux", "windows"):
        lab.evidence("install-" + key, lab.job(lab.state["installJobs"][key])["result"])
    rescue_shutdown(c1)
    lab.state["client2PoweredAfterInstall"] = True
    lab.save()
    log("cliente2 reinicia sozinho no Windows instalado (rebootAfterInstall); fase firstboot segue")
    c2.stop()


def phase_firstboot(lab):
    c1, c2 = VM(lab, "client1", 64, True), VM(lab, "client2", 128, False)
    c2.start(memory=4096)
    c1.start(memory=3072)
    threads = [threading.Thread(target=windows_logon, args=(c2, lab.state["passwords"]["client2-windows"], "windows-primeiro-boot", lab.args.windows_wait))]
    threads[0].start()
    for _ in range(40):
        time.sleep(15)
        c1.shot("xubuntu-boot")
    c1.qmp.key("ret")
    time.sleep(3)
    c1.qmp.type(lab.state["passwords"]["client1-linux"] + "\n")
    for _ in range(8):
        time.sleep(10)
        c1.shot("xubuntu-login")
    threads[0].join()
    lab.evidence("firstboot", {"windowsShot": str(c2.lab.ws / "client2-latest.png"), "xubuntuShot": str(c1.lab.ws / "client1-latest.png"), "review": "conferir telas: área de trabalho Windows e greeter/troca de senha Xubuntu"})
    log("Desligando os dois sistemas pelo botão de energia ACPI (Windows sem hibernação)")
    clean = [c2.powerdown(900), c1.powerdown(300)]
    lab.evidence("firstbootShutdown", {"windowsClean": clean[0], "xubuntuClean": clean[1]})


def phase_clone(lab):
    used = set(lab.state.setdefault("usedEnrollments", []))
    c1, c2 = VM(lab, "client1", 64, True), VM(lab, "client2", 128, False)
    iso = ROOT / lab.args.rescue_iso
    c2.start(cdrom=iso)
    c1.start(cdrom=iso)
    rescue_login(c2)
    rescue_login(c1)
    d2 = rescue_start_agent(lab, c2, False, used)
    d1 = rescue_start_agent(lab, c1, True, used)
    lab.state["usedEnrollments"] = sorted(used)
    lab.save()
    source, target = disk_of(d2, c2.serial_number), disk_of(d1, c1.serial_number)
    lab.evidence("cloneSourceLayout", source.get("layout"))
    planned = lab.api("POST", "/api/v1/clones/plan", {"sourceDeviceId": d2["id"], "sourceDiskFingerprint": source["fingerprint"], "targetDeviceId": d1["id"], "targetDiskFingerprint": target["fingerprint"], "strategy": "FIT", "consent": True, "transfer": "STREAM"})
    lab.evidence("clonePreview", planned["preview"])
    approve(lab, planned["captureJob"])
    approve(lab, planned["targetJob"])
    rescue_confirm(lab, c1, planned["targetJob"], "Clone destino")
    lab.wait_job(planned["targetJob"]["id"], ["COMPLETED"], 6 * 3600, c1, "Clone")
    lab.evidence("cloneSource", lab.job(planned["captureJob"]["id"])["state"])
    lab.evidence("cloneResult", lab.job(planned["targetJob"]["id"])["result"])
    session = next(s for s in lab.api("GET", "/api/v1/clones") if s["id"] == planned["session"]["id"])
    stream_dir = lab.ws / "server/repository/streams" / planned["session"]["streamId"]
    lab.evidence("cloneSession", {"state": session["state"], "transfer": session["transfer"], "serverDataLeft": stream_dir.exists()})
    rescue_shutdown(c1)
    rescue_shutdown(c2)


def phase_convert(lab):
    used = set(lab.state.setdefault("usedEnrollments", []))
    c1 = VM(lab, "client1", 64, True)
    seed = build_kit(lab, "client1")
    c1.start(cdrom=WINDOWS_ISO, seed=seed, scratch=True)
    c1.press_for_cd(25)
    d1 = winpe_pair(lab, c1, used)
    lab.state["usedEnrollments"] = sorted(used)
    lab.save()
    assert "CONVERT_GPT" in d1["inventory"]["capabilities"], d1["inventory"]["capabilities"]
    media = next(m for m in lab.api("GET", "/api/v1/media") if m["id"] == lab.state["windowsMedia"])
    edition = next(i for i in media["images"] if i["name"].strip().lower() == "windows 10 pro")
    target = disk_of(d1, c1.serial_number)
    job = lab.api("POST", "/api/v1/jobs/plan", {"kind": "CONVERT_GPT", "deviceId": d1["id"], "diskFingerprint": target["fingerprint"], "consent": True, "deployment": {"mediaId": media["id"], "imageIndex": edition["index"], "firmware": "GPT_UEFI", "rebootAfterInstall": False}})
    approve(lab, job)
    winpe_confirm(lab, c1, job, "Conversão")
    lab.wait_job(job["id"], ["COMPLETED"], 3600, c1, "Conversão")
    lab.evidence("convertResult", lab.job(job["id"])["result"])
    c1.shot("convertido")
    time.sleep(10)
    c1.stop()


def phase_boot(lab):
    c1 = VM(lab, "client1", 64, True)
    c1.start(memory=4096)
    windows_logon(c1, lab.state["passwords"]["client2-windows"], "windows-uefi-cliente1", lab.args.windows_wait)
    lab.evidence("uefiBoot", {"shot": str(lab.ws / "client1-latest.png"), "review": "conferir área de trabalho do Windows clonado em UEFI/GPT no cliente1"})
    lab.evidence("uefiShutdownClean", c1.powerdown(900))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("phases", nargs="+", choices=["install", "firstboot", "clone", "convert", "boot"])
    parser.add_argument("--workspace")
    parser.add_argument("--rescue-iso", default="artifacts/lab/SLT-Rescue-lab.iso")
    parser.add_argument("--windows-wait", type=int, default=12, help="minutos até digitar a senha no logon do Windows")
    args = parser.parse_args()
    workspace = Path(args.workspace) if args.workspace else ROOT / "runtime" / ("lab-" + uuid.uuid4().hex[:12])
    if not workspace.resolve().is_relative_to((ROOT / "runtime").resolve()):
        raise SystemExit("O workspace do laboratório precisa ficar em runtime/")
    lab = Lab(workspace)
    lab.args = args
    log(f"Laboratório em {workspace} (aceleração {lab.accel})")
    try:
        lab.start_controller()
        for phase in args.phases:
            log(f"=== fase {phase} ===")
            globals()["phase_" + phase](lab)
            lab.state.setdefault("phasesDone", []).append(phase)
            lab.save()
            log(f"=== fase {phase} concluída ===")
    finally:
        lab.close()
        log("Evidências em " + str(workspace))


if __name__ == "__main__":
    main()
