"""Local harness guards/transport tests; do not launch a VM or access disks."""
import importlib.util
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import unittest

HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location("winpe_vm",HERE / "windows-pe-vm.py")
vm=importlib.util.module_from_spec(spec)
spec.loader.exec_module(vm)


class VMHarnessTests(unittest.TestCase):
    def test_rejects_host_devices_and_unc_before_access(self):
        for value in (r"\\.\PhysicalDrive0",r"\\?\GLOBALROOT\Device\Harddisk0",r"\\server\share\file.iso"):
            with self.assertRaises(ValueError): vm.safe_local_file(value)
        with tempfile.TemporaryDirectory(prefix="slt-vm-path-fixture-") as directory:
            path=Path(directory) / "fixture.iso"
            path.write_bytes(b"synthetic fixture only")
            self.assertEqual(path.resolve(),vm.safe_local_file(path,".iso"))

    def test_loopback_relay_preserves_binary_streams(self):
        payload=bytes(range(256))*8192
        errors=[]
        with socket.socket() as listener:
            listener.bind(("127.0.0.1",0));listener.listen(1);listener.settimeout(15)
            port=listener.getsockname()[1]
            def echo():
                try:
                    connection,_=listener.accept()
                    with connection:
                        connection.settimeout(15)
                        while data:=connection.recv(65536): connection.sendall(data)
                except BaseException as exc: errors.append(exc)
            worker=threading.Thread(target=echo,daemon=True);worker.start()
            process=subprocess.Popen([sys.executable,str(HERE / "windows-pe-vm-relay.py"),str(port)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW if os.name=="nt" else 0)
            try: output,error=process.communicate(payload,timeout=20)
            finally:
                if process.poll() is None: process.kill();process.wait(timeout=5)
            worker.join(timeout=5)
        self.assertFalse(worker.is_alive())
        self.assertFalse(errors,errors)
        self.assertEqual(0,process.returncode,error.decode(errors="replace"))
        self.assertEqual(payload,output)


if __name__=="__main__": unittest.main(verbosity=2)
