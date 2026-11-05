#!/usr/bin/env python3
"""Synthetic exporter validation; no real disks, networking or external tools."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("export_netboot", Path(__file__).with_name("export-rescue-netboot.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ExportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="slt-pxe-export-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.live = self.root / "live"
        self.live.mkdir()
        self.populate(self.live)

    @staticmethod
    def populate(directory):
        kernel = bytearray(2048)
        kernel[0x202:0x206] = b"HdrS"
        (directory / "vmlinuz-6.12-fixture").write_bytes(kernel)
        (directory / "initrd.img-6.12-fixture").write_bytes(b"070701" + bytes(2048))
        (directory / "filesystem.squashfs").write_bytes(b"hsqs" + bytes(2048))

    def test_matching_versioned_artifacts(self):
        self.assertEqual(set(module.select_artifacts(self.live)), {"kernel", "initrd", "rootfs"})

    def test_mixed_kernel_versions(self):
        (self.live / "initrd.img-6.12-fixture").rename(self.live / "initrd.img-6.13-fixture")
        with self.assertRaisesRegex(ValueError, "versions differ"):
            module.select_artifacts(self.live)

    def test_ambiguous_kernel(self):
        (self.live / "vmlinuz-other").write_bytes(b"other")
        with self.assertRaises(ValueError):
            module.select_artifacts(self.live)

    def test_bad_kernel(self):
        (self.live / "vmlinuz-6.12-fixture").write_bytes(b"not Linux")
        with self.assertRaisesRegex(ValueError, "kernel"):
            module.select_artifacts(self.live)

    def test_bad_rootfs(self):
        (self.live / "filesystem.squashfs").write_bytes(b"not SquashFS")
        with self.assertRaisesRegex(ValueError, "SquashFS"):
            module.select_artifacts(self.live)

    def test_bad_initrd(self):
        (self.live / "initrd.img-6.12-fixture").write_bytes(b"not initrd")
        with self.assertRaisesRegex(ValueError, "initramfs"):
            module.select_artifacts(self.live)

    def test_different_alias(self):
        (self.live / "vmlinuz").write_bytes(b"different payload")
        with self.assertRaisesRegex(ValueError, "alias differs"):
            module.select_artifacts(self.live)

    def test_manifest_and_existing_output(self):
        iso = self.root / "fixture.iso"
        iso.write_bytes(b"synthetic ISO; extractor mocked")
        target = self.root / "published"
        def fake_extractor(_, output, __):
            self.populate(output)
        with patch.object(module, "extract_live", fake_extractor):
            manifest = module.export(iso, target, "0.1.0-beta.6", "a" * 40, "fixture")
            self.assertEqual(manifest["id"], "rescue-0-1-0-beta-6")
            self.assertEqual(manifest["validationStatus"], "NETWORK_BOOT_DEV")
            self.assertEqual(manifest["sourceIsoSha256"], module.sha256(iso))
            for artifact in manifest["artifacts"]:
                payload = target / artifact["relativePath"]
                self.assertEqual(artifact["sha256"], module.sha256(payload))
                self.assertEqual(artifact["sizeBytes"], payload.stat().st_size)
            with self.assertRaisesRegex(ValueError, "already exists"):
                module.export(iso, target, "0.1.0-beta.6", "a" * 40, "fixture")

    def test_invalid_commit_and_version(self):
        iso = self.root / "fixture.iso"
        iso.write_bytes(b"fixture")
        with self.assertRaisesRegex(ValueError, "commit"):
            module.export(iso, self.root / "profile", "0.1", "not a commit", "fixture")
        with self.assertRaisesRegex(ValueError, "version"):
            module.export(iso, self.root / "profile", "evil;command", "a" * 40, "fixture")

    def test_failed_export_does_not_publish(self):
        iso = self.root / "fixture.iso"
        iso.write_bytes(b"fixture")
        target = self.root / "published"
        with patch.object(module, "extract_live", side_effect=ValueError("corrupt input")):
            with self.assertRaisesRegex(ValueError, "corrupt input"):
                module.export(iso, target, "0.1", "a" * 40, "fixture")
        self.assertFalse(target.exists())
        self.assertFalse(list(self.root.glob(".slt-netboot-*")))


if __name__ == "__main__":
    unittest.main()
