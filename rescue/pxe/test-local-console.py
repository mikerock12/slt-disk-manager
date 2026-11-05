#!/usr/bin/env python3
"""Interactive console regression using an existing Rescue ISO and QEMU.

No host networking is changed, no NIC is attached, and the only data disk is a
temporary readonly RAW file. This proves the login helper, not PXE boot.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time

spec = importlib.util.spec_from_file_location("netboot_vm", Path(__file__).with_name("test-netboot-vm.py"))
lab = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lab)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qemu", type=Path, required=True)
    parser.add_argument("--iso", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--firmware", choices=("bios", "uefi"), default="bios")
    parser.add_argument("--ovmf-code", type=Path)
    parser.add_argument("--ovmf-vars", type=Path)
    args = parser.parse_args()
    assert args.qemu.is_file() and args.iso.is_file() and not args.iso.is_symlink()
    assert not args.output.exists(), "Choose a new output directory"
    args.output.mkdir(parents=True)
    fixture = args.output / "synthetic-readonly.raw"
    fixture.write_bytes(bytes(8 * 1024 * 1024))
    expected = lab.digest(fixture)
    firmware = []
    if args.firmware == "uefi":
        assert args.ovmf_code and args.ovmf_vars
        variables = args.output / "OVMF_VARS.fd"
        shutil.copyfile(args.ovmf_vars, variables)
        firmware = ["-drive", f"if=pflash,format=raw,unit=0,readonly=on,file={args.ovmf_code.resolve()}",
                    "-drive", f"if=pflash,format=raw,unit=1,file={variables.resolve()}"]
    with socket.socket() as free:
        free.bind(("127.0.0.1", 0))
        port = free.getsockname()[1]
    command = [str(args.qemu.resolve()), "-no-user-config", "-nodefaults", "-device", "VGA",
               "-machine", "q35,accel=tcg", "-cpu", "max", "-m", "2048", "-smp", "2", "-display", "none",
               "-monitor", "none", "-serial", f"tcp:127.0.0.1:{port},server=on,wait=off", "-nic", "none",
               "-boot", "order=d,menu=off", "-cdrom", str(args.iso.resolve()), "-no-reboot",
               "-drive", f"if=none,id=fixture,file={fixture.resolve()},format=raw,readonly=on",
               "-device", "virtio-blk-pci,drive=fixture,serial=SLT-CONSOLE-FIXTURE", *firmware]
    (args.output / "qemu-command.json").write_text(json.dumps(command, indent=2) + "\n")
    receipt = {"scope": "interactive local console only; not PXE", "firmware": args.firmware,
               "isoSha256": lab.digest(args.iso), "networkAttached": False, "physicalDisksAttached": False,
               "syntheticReadonlySha256Before": expected, "success": False}
    console, vm = None, None
    with (args.output / "qemu.log").open("wb") as log:
        try:
            vm = subprocess.Popen(command, stdout=log, stderr=log,
                                  creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            connection = lab.wait_for(lambda: socket.create_connection(("127.0.0.1", port), timeout=1), "QEMU loopback serial", 30)
            console = lab.GuestConsole(None, args.output / "serial.log", connected_socket=connection)
            started = time.monotonic()
            console.expect("SLT_RESCUE_BOOT_READY:" + args.firmware.upper(), 600)
            lab.progress("Actual ISO userspace ready; running the shared interactive login helper")
            console.login("SLT_CONSOLE_LOGIN_OK")
            console.command("id -un; sudo -n true && printf '%s%s\\n' 'SLT_CONSOLE_' 'SUDO_OK'", "SLT_CONSOLE_SUDO_OK")
            # A second login() must recognize a fresh authenticated prompt and
            # must not reuse the getty/password prompts in the previous history.
            console.login("SLT_CONSOLE_RELOGIN_OK")
            receipt.update({"interactiveTechnicianLogin": True, "sudoWithoutPassword": True,
                            "freshAuthenticatedPromptRecheck": True, "elapsedSeconds": round(time.monotonic() - started, 2),
                            "syntheticReadonlySha256After": lab.digest(fixture), "success": True})
            assert receipt["syntheticReadonlySha256After"] == expected
            lab.progress("PASS interactive technician/live login, real shell completion, sudo and stale-history recheck")
            console.send("sudo -n systemctl poweroff\n")
            try:
                vm.wait(timeout=30)
            except subprocess.TimeoutExpired:
                pass
        finally:
            if vm is not None and vm.poll() is None:
                vm.terminate()
                try:
                    vm.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    vm.kill()
                    vm.wait(timeout=5)
            if console:
                console.close()
            receipt["ownedVmStopped"] = vm is None or vm.poll() is not None
            (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
