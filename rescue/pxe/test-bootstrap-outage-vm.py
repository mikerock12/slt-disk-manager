#!/usr/bin/env python3
"""Short, isolated bootstrap failure diagnostic; this never approves a boot profile.

Uses real SLT HTTP/TFTP and pinned iPXE loaders, but only tiny non-executable
Rescue artifact fixtures. No Controller, Linux userspace, disk, ISO or USB is
attached. Requires a disposable Linux network namespace, root, QEMU and OVMF.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time

spec = importlib.util.spec_from_file_location("slt_netboot_vm", Path(__file__).with_name("test-netboot-vm.py"))
lab = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lab)


def fixture_profile(private, store):
    source = private / "synthetic-rescue"
    source.mkdir()
    artifacts = []
    for artifact_id in ("kernel", "initrd", "rootfs"):
        file = source / artifact_id
        file.write_bytes(("SLT SYNTHETIC " + artifact_id + " - NOT AN EXECUTABLE IMAGE\n").encode("ascii"))
        artifacts.append({"id": artifact_id, "relativePath": artifact_id, "sizeBytes": file.stat().st_size,
                          "sha256": lab.digest(file), "source": "synthetic-bootstrap-outage-diagnostic"})
    profile = {"schemaVersion": 1, "id": "rescue-bootstrap-outage-fixture", "displayName": "Synthetic bootstrap outage fixture",
               "kind": "RESCUE_LINUX", "architecture": "X64", "firmwareSupport": ["BIOS", "UEFI"],
               "secureBootStatus": "UNSUPPORTED", "validationStatus": "NETWORK_BOOT_DEV", "version": "fixture-v1",
               "artifacts": artifacts}
    (source / "netboot-manifest.json").write_text(json.dumps(profile), encoding="utf-8")
    return lab.import_profile(source, store)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service-dll", type=Path, required=True)
    parser.add_argument("--bootstrap-profile", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--firmware", choices=("bios", "uefi", "both"), default="both")
    parser.add_argument("--inside-netns", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--parent-netns", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        parser.error("Run as root on a disposable Linux runner; never on the host LAN")
    for name in ("service_dll", "bootstrap_profile", "output"):
        setattr(args, name, getattr(args, name).resolve())
    assert args.service_dll.is_file() and args.bootstrap_profile.is_dir()
    if not args.inside_netns:
        if args.output.exists():
            parser.error("Evidence directory already exists; choose a new directory")
        os.execvp("unshare", ["unshare", "--net", sys.executable, str(Path(__file__).resolve()), *sys.argv[1:],
                             "--inside-netns", "--parent-netns", os.readlink("/proc/self/ns/net")])

    # These checks precede every address, TAP, DHCP process and SLT listener.
    assert args.parent_netns and os.readlink("/proc/self/ns/net") != args.parent_netns
    assert [item["ifname"] for item in json.loads(lab.run("ip", "-j", "link"))] == ["lo"]
    assert not json.loads(lab.run("ip", "-j", "route", "show", "default"))
    args.output.mkdir(parents=True, exist_ok=False)
    os.chmod(args.output, 0o700)
    modes = ("bios", "uefi") if args.firmware == "both" else (args.firmware,)
    results = {"scope": "BOOTSTRAP_OUTAGE_DIAGNOSTIC_ONLY", "networkBootLabApproved": False,
               "namespaceIsolated": True, "hostNetworkChanged": False, "isoAttached": False,
               "usbAttached": False, "disksAttached": False, "controllerStarted": False,
               "linuxUserspaceExpected": False, "secureBoot": False, "physicalHardwareTested": False,
               "firmware": [], "success": False}
    processes, logs = [], []
    private_context = None

    def start(command, name, **kwargs):
        log = (args.output / (name + ".log")).open("wb")
        logs.append(log)
        process = subprocess.Popen(command, stdout=log, stderr=log, start_new_session=True, **kwargs)
        processes.append(process)
        return process

    def on_termination(signum, _frame):
        results["cancelledBySignal"] = signum
        results["success"] = False
        raise SystemExit(128 + signum)

    try:
        signal.signal(signal.SIGTERM, on_termination)
        lab.progress("Bootstrap-only diagnostic: configuring a fresh isolated namespace")
        for command in [("ip", "link", "set", "lo", "up"), ("ip", "link", "add", "sltbr0", "type", "bridge"),
                        ("ip", "address", "add", "10.77.0.1/24", "dev", "sltbr0"), ("ip", "link", "set", "sltbr0", "up"),
                        ("ip", "tuntap", "add", "dev", "slttap0", "mode", "tap"),
                        ("ip", "link", "set", "slttap0", "master", "sltbr0"), ("ip", "link", "set", "slttap0", "up")]:
            lab.run(*command)
        assert not json.loads(lab.run("ip", "-j", "route", "show", "default"))
        private_context = tempfile.TemporaryDirectory(prefix="slt-bootstrap-private-")
        private = Path(private_context.name)
        store = private / "boot-store"
        store.mkdir()
        profiles = [fixture_profile(private, store), lab.import_profile(args.bootstrap_profile, store)]
        results["profiles"] = [{"id": p["id"], "kind": p["kind"], "version": p["version"],
                                "artifacts": [{k: artifact[k] for k in ("id", "sizeBytes", "sha256")} for artifact in p["artifacts"]]}
                               for p in profiles]
        config = {"schemaVersion": 1, "interfaceId": "sltbr0", "bindIPv4": "10.77.0.1", "allowedCidr": "10.77.0.0/24",
                  "mode": "EXTERNAL_DHCP", "httpPort": 8088, "maxConcurrentTransfers": 8}
        launch = {"config": config, "storePath": str(store), "profiles": profiles, "laboratory": True}
        dnsconfig = private / "dnsmasq.conf"
        dnsconfig.write_text("\n".join(["port=0", "interface=sltbr0", "bind-interfaces", "except-interface=lo", "log-dhcp",
            "dhcp-authoritative", "dhcp-range=10.77.0.20,10.77.0.40,255.255.255.0,5m", "dhcp-option=option:router",
            "dhcp-option=option:dns-server", "dhcp-no-override", "dhcp-match=set:efi64,option:client-arch,7",
            "dhcp-match=set:efi64,option:client-arch,9", "dhcp-boot=tag:!efi64,undionly.kpxe,,10.77.0.1",
            "dhcp-boot=tag:efi64,ipxe.efi,,10.77.0.1", f"dhcp-leasefile={private / 'leases'}", "user=root", ""]), encoding="utf-8")
        dhcp = start(["dnsmasq", "--keep-in-foreground", "--conf-file=" + str(dnsconfig)], "external-dhcp")
        time.sleep(0.3)
        assert dhcp.poll() is None
        for index, mode in enumerate(modes):
            entry = {"firmware": mode, "passed": False, "explicitBootFailure": False, "userspaceReached": False}
            results["firmware"].append(entry)
            console, qmp, service, vm = None, None, None, None
            try:
                service_name = mode + "-boot-service"
                service_log = args.output / (service_name + ".log")
                service = start(["dotnet", str(args.service_dll), "--lab"], service_name, stdin=subprocess.PIPE)
                service.stdin.write((json.dumps(launch) + "\n").encode())
                service.stdin.flush()
                lab.wait_for(lambda: '"state":"RUNNING"' in service_log.read_text(errors="replace"), "bootstrap service RUNNING", 90)
                qmp_path, serial_path = private / (mode + "-qmp.sock"), private / (mode + "-serial.sock")
                firmware = []
                if mode == "uefi":
                    code, template = lab.firmware_files()
                    variables = private / "OVMF_VARS.fd"
                    shutil.copyfile(template, variables)
                    firmware = ["-drive", f"if=pflash,format=raw,unit=0,readonly=on,file={code}",
                                "-drive", f"if=pflash,format=raw,unit=1,file={variables}"]
                    entry["firmwareCodeSha256"] = lab.digest(code)
                command = ["qemu-system-x86_64", "-no-user-config", "-nodefaults", "-device", "VGA", "-machine", "q35,accel=tcg",
                           "-cpu", "max", "-m", "1024", "-smp", "1", "-display", "none", "-no-reboot",
                           "-qmp", f"unix:{qmp_path},server=on,wait=off", "-serial", f"unix:{serial_path},server=on,wait=off",
                           "-boot", "order=n,menu=off", "-netdev", "tap,id=net0,ifname=slttap0,script=no,downscript=no",
                           "-device", f"e1000,netdev=net0,mac=52:54:00:77:01:{index + 1:02x}",
                           "-smbios", "type=1,manufacturer=SLT_QEMU_ONLY,product=SLT_BOOTSTRAP_DIAGNOSTIC", *firmware]
                started = time.monotonic()
                vm = start(command, mode + "-qemu")
                lab.wait_for(lambda: serial_path.exists() and qmp_path.exists(), "bootstrap QEMU console")
                console = lab.GuestConsole(serial_path, args.output / (mode + ".serial.log"))
                qmp = lab.Qmp(qmp_path)
                console.qmp = qmp
                console.expect("SLT Network Boot", 180)
                entry["menuSeconds"] = round(time.monotonic() - started, 2)
                qmp.execute("screendump", {"filename": str(args.output / (mode + "-menu.ppm"))})
                lab.progress(mode.upper() + " diagnostic menu reached; stopping real SLT HTTP/TFTP")
                service.stdin.write(b"STOP\n")
                service.stdin.flush()
                service.wait(timeout=15)
                assert service.returncode == 0
                with socket.socket() as probe:
                    probe.settimeout(2)
                    assert probe.connect_ex(("10.77.0.1", 8088)) != 0, "HTTP remained after stop"
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                    probe.bind(("10.77.0.1", 69))
                entry["serviceStopped"] = entry["httpAndTftpReleased"] = True
                failed_at = time.monotonic()
                qmp.key("down")
                time.sleep(0.2)
                qmp.key("ret")
                time.sleep(0.3)
                qmp.execute("screendump", {"filename": str(args.output / (mode + "-after-selection.ppm"))})
                console.expect(r"(?m)^Falha de boot\.", 180, label="explicit SLT boot failure output (not a debug command trace)")
                entry["explicitBootFailure"] = True
                qmp.execute("screendump", {"filename": str(args.output / (mode + "-failure-message.ppm"))})
                until = time.monotonic() + 10
                while time.monotonic() < until:
                    entry["userspaceReached"] = bool(re.search(r"SLT_RESCUE_BOOT_READY:|Linux version |BOOT_READY", console.pump()))
                    assert not entry["userspaceReached"], "Unexpected Linux userspace"
                    assert vm.poll() is None, "Diagnostic VM terminated unexpectedly"
                    time.sleep(0.2)
                qmp.execute("screendump", {"filename": str(args.output / (mode + "-after-fallback.ppm"))})
                entry["observedSeconds"] = round(time.monotonic() - failed_at, 2)
                entry["passed"] = True
                lab.progress("PASS " + mode + ": explicit SLT failure after bootstrap outage, no disks or Linux attached")
            except Exception as error:
                entry["error"] = type(error).__name__ + ": " + str(error)[:500]
                if console:
                    console.diagnostics("bootstrap-only diagnostic failed", kind="FAILURE")
                lab.progress("FAIL " + mode + ": " + entry["error"])
            finally:
                if qmp:
                    try:
                        qmp.execute("quit")
                    except (OSError, RuntimeError):
                        pass
                    qmp.close()
                if console:
                    console.close()
                for process in (vm, service):
                    if process and process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=2)
        results["success"] = len(results["firmware"]) == len(modes) and all(entry["passed"] for entry in results["firmware"])
    finally:
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        remaining = [process for process in reversed(processes) if process.poll() is None]
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
        if not results["allOwnedProcessesStopped"]:
            results["success"] = False
        if private_context:
            private_context.cleanup()
        (args.output / "receipt.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    if not results["success"]:
        raise SystemExit("Bootstrap outage diagnostic failed; inspect serial/screenshots/receipt. No LAB approval was produced.")


if __name__ == "__main__":
    main()
