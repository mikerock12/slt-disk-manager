"""Opt-in real Windows PE VM validation; ALL writable disks are fresh local files.

No host disk, TAP driver, privileged mount, ADK or host boot configuration is used.
The ISO belongs to the operator, stays read-only and is never uploaded. The private
runtime folder includes Microsoft media/credentials and must stay out of backups.
TCG can be slow: a timeout is an incomplete test, never a successful installation.
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
import sys
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
CREATE_HIDDEN = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0


def safe_local_file(value, suffix=None):
    path = Path(value).absolute()
    if str(path).startswith("\\\\") or not path.is_file() or path.is_symlink():
        raise ValueError("Only existing regular local files are accepted")
    if suffix and path.suffix.lower() != suffix:
        raise ValueError("Unexpected file extension")
    if "," in str(path) or any(c in str(path) for c in '\r\n"&|<>^%!'):
        raise ValueError("Unsupported control/command character in fixture path")
    return path.resolve()


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class QMP:
    def __init__(self, port):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.file = self.socket.makefile("rwb", buffering=0)
        greeting = json.loads(self.file.readline())
        if "QMP" not in greeting:
            raise RuntimeError("Invalid QMP greeting")
        self.sequence = 0
        self.command("qmp_capabilities")

    def command(self, command, arguments=None):
        self.sequence += 1
        request = {"execute": command, "id": self.sequence}
        if arguments is not None:
            request["arguments"] = arguments
        self.file.write(json.dumps(request).encode() + b"\r\n")
        while True:
            line = self.file.readline()
            if not line:
                raise EOFError("QMP closed")
            response = json.loads(line)
            if response.get("id") == self.sequence:
                if "error" in response:
                    raise RuntimeError(str(response["error"]))
                return response.get("return")

    def key(self, code, shift=False):
        keys = ([{"type": "qcode", "data": "shift"}] if shift else [])
        keys.append({"type": "qcode", "data": code})
        self.command("send-key", {"keys": keys, "hold-time": 40})
        time.sleep(0.065)

    def type_confirmation(self, text):
        if not re.fullmatch(r"[A-Za-z0-9 -]{1,200}", text):
            raise ValueError("Unexpected fixture challenge characters")
        for char in text:
            self.key({" ": "spc", "-": "minus"}.get(char, char.lower()), char.isupper())
        self.key("ret")

    def type_fixture_command(self, text):
        # Only hard-coded fixture diagnostic commands call this method. The test
        # answer file selects en-US keyboard; no arbitrary command is accepted.
        symbols={" ":("spc",False),"-":("minus",False),"/":("slash",False),"\\":("backslash",False),":":("semicolon",True),"%":("5",True),"(":("9",True),")":("0",True),"@":("2",True),".":("dot",False)}
        for char in text:
            if char.isascii() and char.isalnum(): self.key(char.lower(),char.isupper())
            elif char in symbols: self.key(*symbols[char])
            else: raise ValueError("Unexpected fixed diagnostic character")
        self.key("ret")

    def screenshot(self, path):
        try:
            self.command("screendump", {"filename": str(path), "format": "png"})
        except RuntimeError:
            ppm = path.with_suffix(".ppm")
            self.command("screendump", {"filename": str(ppm)})
            from PIL import Image
            Image.open(ppm).save(path)

    def close(self):
        self.file.close()
        self.socket.close()


def wait_for(check, description, deadline, processes=()):
    last = None
    while time.monotonic() < deadline:
        for process in processes:
            if process.poll() is not None:
                raise RuntimeError(f"Fixture process exited ({process.returncode}): {description}")
        try:
            value = check()
            if value:
                return value
        except (OSError, ValueError, KeyError, http.client.HTTPException) as exc:
            last = exc
        time.sleep(1)
    raise TimeoutError(f"{description}: {last}")


def write_text(path, text):
    path.write_bytes(text.replace("\r\n", "\n").replace("\n", "\r\n").encode("ascii"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--qemu-dir", required=True)
    parser.add_argument("--iso", required=True)
    parser.add_argument("--agent", default=str(ROOT / "artifacts/slt-winpe-agent.exe"))
    parser.add_argument("--controller", default=str(ROOT / "src/server/SLT.Controller.Host/bin/Debug/net10.0/SLT.Controller.Host.dll"))
    parser.add_argument("--timeout-minutes", type=int, default=30)
    parser.add_argument("--deploy", action="store_true", help="Apply Windows only to the fresh synthetic VM target")
    parser.add_argument("--first-boot-minutes", type=int, default=10)
    args = parser.parse_args()
    if os.name != "nt" or not 2 <= args.timeout_minutes <= 180 or not 0 <= args.first_boot_minutes <= 60:
        parser.error("Windows host and bounded timeouts required")
    qemu = safe_local_file(Path(args.qemu_dir) / "qemu-system-x86_64.exe", ".exe")
    image_tool = safe_local_file(Path(args.qemu_dir) / "qemu-img.exe", ".exe")
    iso = safe_local_file(args.iso, ".iso")
    agent = safe_local_file(args.agent, ".exe")
    controller_dll = safe_local_file(args.controller, ".dll")
    runtime_parent = (ROOT / "runtime").resolve()
    if not runtime_parent.is_relative_to(ROOT.resolve()):
        raise ValueError("Runtime root must stay within the repository")
    runtime_parent.mkdir(exist_ok=True)
    workspace = runtime_parent / ("winpe-vm-" + uuid.uuid4().hex)
    workspace.mkdir()
    if not workspace.resolve().is_relative_to(runtime_parent):
        raise ValueError("Fixture escaped private runtime root")
    seed = workspace / "seed"
    seed.mkdir()
    target, scratch = workspace / "target.qcow2", workspace / "scratch.qcow2"
    for path, size in ((target, "64G"), (scratch, "12G")):
        if path.exists() or path.parent != workspace or path.suffix != ".qcow2":
            raise ValueError("Writable VM disks must be NEW owned qcow2 files")
        subprocess.run([str(image_tool), "create", "-f", "qcow2", str(path), size], check=True, creationflags=CREATE_HIDDEN)
    report = {"startedAtUtc": datetime.now(timezone.utc).isoformat(), "workspace": str(workspace),
              "acceleration": "TCG", "ramMiB": 3072, "cpus": 2, "isoReadOnly": True,
              "hostDevicesAttached": False, "networkMode":"user NAT; guest default route removed in fixture; IPv6 backend disabled", "peBoot": False, "mtlsInventory": False,
              "deploymentCompleted": False, "firstBootVerified": False,
              "writableDisks": [str(target), str(scratch)]}
    (workspace / "evidence.json").write_text(json.dumps(report, indent=2))
    print("Private VM evidence:", workspace, flush=True)
    logs, processes, qmp = [], [], None
    deadline = time.monotonic() + args.timeout_minutes * 60

    def save():
        (workspace / "evidence.json").write_text(json.dumps(report, indent=2))

    try:
        private_controller=workspace / "controller-bin"
        shutil.copytree(controller_dll.parent,private_controller,ignore=shutil.ignore_patterns("state","runtime","logs","*.db","*.pfx","*.pem","*.key","connection.json"))
        controller_dll=private_controller / controller_dll.name
        report["agentSha256"]=hashlib.sha256(agent.read_bytes()).hexdigest()
        report["controllerDllSha256"]=hashlib.sha256(controller_dll.read_bytes()).hexdigest()
        save()
        port, qmp_port = free_port(), free_port()
        server_log = open(workspace / "controller.log", "wb")
        logs.append(server_log)
        controller = subprocess.Popen(["dotnet", str(controller_dll), "--state-dir", str(workspace / "server"), "--port", str(port)], stdout=server_log, stderr=server_log, creationflags=CREATE_HIDDEN)
        processes.append(controller)
        connection = wait_for(lambda: json.loads((workspace / "server/connection.json").read_text()), "controller identity", min(deadline, time.monotonic()+60), processes)

        def request(method, path, body=None):
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE  # Mandatory exact DER pin BEFORE sending credentials.
            conn = http.client.HTTPSConnection("127.0.0.1", port, context=context, timeout=15)
            conn.connect()
            if hashlib.sha256(conn.sock.getpeercert(binary_form=True)).hexdigest() != connection["serverPin"]:
                conn.close()
                raise RuntimeError("Controller DER pin mismatch")
            conn.request(method, path, json.dumps(body).encode() if body is not None else None,
                         {"Authorization": "Bearer " + connection["token"], "Content-Type": "application/json"})
            response = conn.getresponse()
            data = response.read()
            conn.close()
            if response.status != 200:
                raise RuntimeError(f"HTTP {response.status}: {data[:500]!r}")
            return json.loads(data) if data else None

        wait_for(lambda: request("GET", "/health"), "HTTPS ready", min(deadline, time.monotonic()+60), processes)
        media = request("POST", "/api/v1/media/import", {"path": str(iso), "name": "PRIVATE WinPE VM operator media"}) if args.deploy else None
        kit = seed / "kit"
        subprocess.run(["pwsh", "-NoProfile", "-File", str(ROOT / "rescue/windows-pe/New-WinPEKit.ps1"),
                        "-OutputDirectory", str(kit), "-AgentPath", str(agent), "-Server", f"https://10.0.2.2:{port}",
                        "-ServerPin", connection["serverPin"], "-ScratchDirectory", "R:\\SLT-Scratch"], check=True, creationflags=CREATE_HIDDEN)
        for item in kit.iterdir():
            shutil.move(str(item), str(seed / item.name))
        kit.rmdir()  # Only the now-empty NEW fixture directory.
        marker = json.loads((seed / "SLT/kit-manifest.json").read_text(encoding="utf-8-sig"))["locatorMarker"]
        fixture_start=seed / "SLT/Start-SLT.cmd"
        start_text=fixture_start.read_text()
        network_step='"%SystemRoot%\\System32\\wpeutil.exe" InitializeNetwork\nif errorlevel 1 goto blocked\n'
        if start_text.count(network_step)!=1: raise ValueError("Unexpected kit network initialization")
        isolated_network=network_step+'"%SystemRoot%\\System32\\wpeutil.exe" WaitForNetwork\nif errorlevel 1 goto blocked\nif not exist "%SystemRoot%\\System32\\route.exe" goto blocked\n"%SystemRoot%\\System32\\route.exe" delete 0.0.0.0\n"%SystemRoot%\\System32\\route.exe" print -4\n'
        write_text(fixture_start,start_text.replace(network_step,isolated_network))
        # Fixture-only scratch initialization. This script is never copied into a product kit.
        # Disk 1 is exclusively our new 12GiB scratch.qcow2; no physical host disk exists in this VM.
        write_text(seed / "SLT/scratch-fixture.txt", "select disk 1\nclean\nconvert mbr\ncreate partition primary\nformat quick fs=ntfs label=SLT-VM-Scratch\nassign letter=R\nexit\n")
        # A missing serial device must not prevent the actual fixture commands.
        write_text(seed / "SLT/Boot-VM.cmd", '@echo off\nreg query HKLM\\SYSTEM\\CurrentControlSet\\Control\\MiniNT >nul 2>&1\nif errorlevel 1 exit /b 99\necho SLT_VM_PE_BOOT\ndiskpart /s "%~dp0scratch-fixture.txt"\nif errorlevel 1 goto hold\ncall "%~dp0Start-SLT.cmd"\n:hold\npause >nul\ngoto hold\n')
        # The VM seed is a read-only virtual FAT USB, not an installer USB. Match its unique
        # marker, invoke the fixture scratch preparer and always hold Setup if not found.
        xml = ET.parse(seed / "Autounattend.xml")
        xml.find(".//{urn:schemas-microsoft-com:unattend}Path").text = 'cmd.exe /d /c "for %d in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\\SLT\\' + marker + ' call %d:\\SLT\\Boot-VM.cmd & cmd.exe /d /k echo SLT VM stopped; no implicit Setup."'
        settings=xml.find("{urn:schemas-microsoft-com:unattend}settings")
        keyboard=ET.SubElement(settings,"{urn:schemas-microsoft-com:unattend}component",{"name":"Microsoft-Windows-International-Core-WinPE","processorArchitecture":"amd64","publicKeyToken":"31bf3856ad364e35","language":"neutral","versionScope":"nonSxS"})
        ET.SubElement(keyboard,"{urn:schemas-microsoft-com:unattend}InputLocale").text="0409:00000409"
        ET.register_namespace("", "urn:schemas-microsoft-com:unattend")
        ET.register_namespace("wcm", "http://schemas.microsoft.com/WMIConfig/2002/State")
        xml.write(seed / "Autounattend.xml", encoding="utf-8", xml_declaration=True)
        boot = seed / "boot"
        boot.mkdir()
        seven_zip = safe_local_file(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "7-Zip/7z.exe", ".exe")
        subprocess.run([str(seven_zip), "e", str(iso), "boot/bootsect.exe", "-o"+str(boot), "-y", "-bso0", "-bsp0"], check=True, creationflags=CREATE_HIDDEN)
        safe_local_file(boot / "bootsect.exe", ".exe")
        qemu_log = open(workspace / "qemu.log", "wb")
        logs.append(qemu_log)
        # No bridge/TAP/passthrough and no inbound forwarding. The test-only PE
        # bootstrap removes its default route before starting the unmodified agent.
        # Windows builds of GLib may omit the helper needed by guestfwd-cmd.
        forward = 'user,id=lan,ipv6=off'
        command = [str(qemu), "-L", str(qemu.parent / "share"), "-name", "SLT SYNTHETIC WinPE VM",
                   "-machine", "pc,accel=tcg", "-cpu", "max", "-smp", "2", "-m", "3072", "-display", "none",
                   "-drive", f"file={target},format=qcow2,if=none,id=target", "-device", "ide-hd,drive=target,bus=ide.0,unit=0,serial=SLT-VM-TARGET",
                   "-drive", f"file={scratch},format=qcow2,if=none,id=scratch", "-device", "ide-hd,drive=scratch,bus=ide.0,unit=1,serial=SLT-VM-SCRATCH",
                   "-drive", f"file={iso},format=raw,readonly=on,media=cdrom,if=none,id=windows", "-device", "ide-cd,drive=windows,bus=ide.1,unit=0",
                   "-drive", f"file=fat:{seed},format=raw,readonly=on,if=none,id=seed", "-device", "piix3-usb-uhci", "-device", "usb-storage,drive=seed,removable=on,serial=SLT-VM-KIT",
                   "-netdev", forward, "-device", "e1000,id=fixture-nic,netdev=lan", "-serial", f"file:{workspace / 'serial.log'}",
                   "-qmp", f"tcp:127.0.0.1:{qmp_port},server=on,wait=off", "-boot", "order=c,once=d,menu=off", "-rtc", "base=localtime", "-no-shutdown"]
        # This is deliberately a fixed allowlist: no host devices, passthrough, writable
        # directory export, network bridge, acceleration driver or inherited user options.
        (workspace / "qemu-arguments.json").write_text(json.dumps(command, indent=2))
        vm = subprocess.Popen(command, stdout=qemu_log, stderr=qemu_log, creationflags=CREATE_HIDDEN)
        processes.append(vm)
        qmp = wait_for(lambda: QMP(qmp_port), "QMP listener", min(deadline, time.monotonic()+60), processes)
        for _ in range(30):  # Accept the ISO's boot prompt only during the initial boot window.
            qmp.key("ret")
            time.sleep(0.4)
        qmp.screenshot(workspace / "boot-in-progress.png")
        print("VM running with TCG; waiting for Windows PE enrollment...", flush=True)
        last_capture = 0

        def fixture_control(diagnostics=False):
            path=workspace / "control.json"
            if not path.is_file(): return
            action=json.loads(path.read_text()).get("action")
            path.unlink()
            if action=="stop": raise RuntimeError("Operator stopped synthetic VM test")
            if action=="capture": qmp.screenshot(workspace / "requested-screen.png")
            elif diagnostics and action=="open-console": qmp.key("f10",True)
            elif diagnostics and action in ("show-network","show-clock","start-fixture"):
                commands={"show-network":"route print -4","show-clock":"time /t","start-fixture":'for %d in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\\SLT\\'+marker+' call %d:\\SLT\\Boot-VM.cmd'}
                qmp.type_fixture_command(commands[action])
            elif action!="capture": raise ValueError("Unknown or disallowed fixture action")
            report.setdefault("fixtureActions",[]).append({"action":action,"atUtc":datetime.now(timezone.utc).isoformat()})
            save()

        def enrollment_ready():
            nonlocal last_capture
            fixture_control(True)
            if time.monotonic()-last_capture > 60:
                qmp.screenshot(workspace / "latest-screen.png")
                last_capture = time.monotonic()
            return next((e for e in request("GET", "/api/v1/enrollments") if e["mode"] == "WINPE" and e["status"] == "PENDING"), None)

        enrollment = wait_for(enrollment_ready, "real WinPE enrollment", deadline, processes)
        report["peBoot"] = True
        request("POST", "/api/v1/enrollments/"+enrollment["id"]+"/approve", {"code": enrollment["code"]})
        device = wait_for(lambda: next((d for d in request("GET", "/api/v1/devices") if d["mode"] == "WINPE" and d.get("inventory")), None), "WinPE mTLS inventory", deadline, processes)
        inventory = device["inventory"]
        target_disk = next(d for d in inventory["disks"] if d["serial"] == "SLT-VM-TARGET")
        scratch_disk = next(d for d in inventory["disks"] if d["serial"] == "SLT-VM-SCRATCH")
        assert target_disk["sizeBytes"] == 64*1024**3 and not target_disk["isSystem"] and scratch_disk["isSystem"]
        report["mtlsInventory"] = True
        report["inventory"] = inventory
        qmp.screenshot(workspace / "winpe-agent.png")
        save()
        print("PASS real WinPE boot, CSR/mTLS inventory, target identity and scratch protection", flush=True)
        if not args.deploy:
            return
        media = wait_for(lambda: next((m for m in request("GET", "/api/v1/media") if m["id"] == media["id"] and m["state"] in ("AVAILABLE", "FAILED")), None), "operator media import", deadline, processes)
        if media["state"] != "AVAILABLE":
            raise RuntimeError(str(media))
        image = next(i for i in media["images"] if i["architecture"] == "AMD64")
        job = request("POST", "/api/v1/jobs/plan", {"kind": "DEPLOY_WINDOWS", "deviceId": device["id"], "diskFingerprint": target_disk["fingerprint"], "consent": True, "deployment": {"mediaId": media["id"], "imageIndex": image["index"], "firmware": "MBR_BIOS", "rebootAfterInstall": True}})
        request("POST", "/api/v1/jobs/"+job["id"]+"/approve", {"challenge": job["plan"]["challenge"]})
        report["jobId"] = job["id"]
        save()
        def local_prompt_ready():
            nonlocal last_capture
            fixture_control()
            if time.monotonic()-last_capture > 30:
                qmp.screenshot(workspace / "latest-screen.png")
                last_capture = time.monotonic()
            # For PE without serial, review the screenshot and create the VM-only marker.
            marker_path=workspace / "confirm-vm-only.txt"
            reviewed=marker_path.is_file() and marker_path.read_text().strip()=="SLT-VM-TARGET"
            return reviewed or 'Confirme LOCALMENTE' in (workspace / "serial.log").read_text(errors="replace")

        wait_for(local_prompt_ready, "verified media and reviewed local VM keyboard prompt", deadline, processes)
        qmp.screenshot(workspace / "before-local-confirmation.png")
        qmp.type_confirmation(job["plan"]["challenge"])
        print("Synthetic VM keyboard confirmation entered; applying operator image...", flush=True)

        def deployment_done():
            nonlocal last_capture
            fixture_control()
            value = request("GET", "/api/v1/jobs/"+job["id"])
            report["job"] = value
            save()
            if time.monotonic()-last_capture > 60:
                qmp.screenshot(workspace / "latest-screen.png")
                print("VM progress:", value.get("progress"), flush=True)
                last_capture = time.monotonic()
            return value if value["state"] in ("COMPLETED", "FAILED", "CANCELLED") else None

        result = wait_for(deployment_done, "DISM/BCDBoot completed", deadline, processes)
        report["deploymentCompleted"] = result["state"] == "COMPLETED"
        if report["deploymentCompleted"]:
            qmp.command("set_link",{"name":"fixture-nic","up":False})
            report["networkDisconnectedAfterDeployment"]=True
        save()
        if not report["deploymentCompleted"]:
            raise RuntimeError(str(result.get("result")))
        print("PASS real DISM and BCDBoot in synthetic VM; first boot still unverified", flush=True)
        end = min(deadline, time.monotonic()+args.first_boot_minutes*60)
        while time.monotonic() < end:
            qmp.screenshot(workspace / "first-boot-latest.png")
            time.sleep(5)
        # OOBE/boot is visual evidence requiring review, never inferred from a successful
        # DISM result or elapsed time. Keep firstBootVerified false until reviewed.
        print("First-boot screenshots retained for review; no automatic boot claim.", flush=True)
    except BaseException as exc:
        report["error"] = f"{type(exc).__name__}: {exc}"
        if qmp:
            try:
                qmp.screenshot(workspace / "failure-screen.png")
            except Exception:
                pass
        raise
    finally:
        report["endedAtUtc"] = datetime.now(timezone.utc).isoformat()
        save()
        if qmp:
            try:
                qmp.command("quit")
            except Exception:
                pass
            qmp.close()
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=15)
        for log in logs:
            log.close()
        print("VM evidence retained locally:", workspace, flush=True)


if __name__ == "__main__":
    main()
