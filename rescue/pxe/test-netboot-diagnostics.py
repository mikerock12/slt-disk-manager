#!/usr/bin/env python3
"""Exercise bounded timeout evidence without a VM, sockets, DHCP or credentials."""
import contextlib
import importlib.util
import io
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("netboot_vm", Path(__file__).with_name("test-netboot-vm.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DiagnosticsTests(unittest.TestCase):
    def test_bounded_tail_and_secret_redaction(self):
        text = "old console history\n" * 1000
        text += '\x1b[31mboot error\x1b[0m\n{"token":"sensitive-token","secret":"sensitive-secret"}\n'
        text += "Authorization: Bearer sensitive-bearer\nPareamento RESCUE: código 12345678.\n"
        text += "-----BEGIN RSA PRIVATE KEY-----\nprivate-key-material\n-----END RSA PRIVATE KEY-----\n"
        text += "public TLS pin remains a public identifier\n"
        result = module.serial_tail(text)
        self.assertLessEqual(len(result), 2400)
        self.assertLessEqual(len(result.splitlines()), 24)
        self.assertIn("boot error", result)
        self.assertIn("public TLS pin", result)
        for value in ("sensitive-token", "sensitive-secret", "sensitive-bearer", "12345678", "private-key-material", "\x1b"):
            self.assertNotIn(value, result)

    def fake_console(self):
        console = object.__new__(module.GuestConsole)
        console.evidence = Path("/synthetic-evidence/bios.serial.log")
        console.timeout_count = 0
        console.pump = lambda: "kernel boot diagnostic\nlast initramfs error\n"
        console.qmp = Mock()
        return console

    def test_timeout_prints_tail_and_requests_screenshot_without_masking_failure(self):
        console = self.fake_console()
        output = io.StringIO()
        with patch.object(module, "wait_for", side_effect=AssertionError("original timeout")), contextlib.redirect_stdout(output):
            with self.assertRaisesRegex(AssertionError, "original timeout"):
                console.expect("READY", label="Linux readiness")
        self.assertIn("last initramfs error", output.getvalue())
        self.assertIn("Linux readiness", output.getvalue())
        command, arguments = console.qmp.execute.call_args.args
        self.assertEqual(command, "screendump")
        self.assertTrue(arguments["filename"].endswith("bios.serial.timeout-1.ppm"))

    def test_broken_qmp_does_not_hide_original_timeout(self):
        console = self.fake_console()
        console.qmp.execute.side_effect = RuntimeError("QMP disconnected")
        with patch.object(module, "wait_for", side_effect=AssertionError("original timeout")), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(AssertionError, "original timeout"):
                console.expect("READY")

    def test_real_bash_bracketed_paste_prefix_preserves_output_line(self):
        captured = '\r\n\x1b[?2004l\rSLT_CONSOLE_LOGIN_OK\r\n\r\nSLT_CMD_fixture_RC=0\r\n\x1b[?2004htechnician@slt-rescue:~$ '
        rendered = module.terminal_text(captured)
        self.assertIsNotNone(module.re.search(r"(?m)^SLT_CONSOLE_LOGIN_OK$", rendered))
        self.assertIsNotNone(module.re.search(r"(?m)^SLT_CMD_fixture_RC=0$", rendered))


class LoginTranscriptTests(unittest.TestCase):
    def console(self, authenticated=False, reject=False, echo_only=False):
        console = object.__new__(module.GuestConsole)
        # These historical prompts reproduced the previous heuristic's risk:
        # a username in MOTD could suppress login, or a stale password prompt
        # could submit the Live password to an already authenticated shell.
        console.text = "old login: technician\r\nPassword: \r\nMOTD example technician@slt-rescue:~$ \r\n"
        console.evidence = Path("/synthetic-evidence/login.serial.log")
        console.timeout_count = 0
        console.qmp = None
        console.pump = lambda: console.text
        console.sent = []
        phase = "shell" if authenticated else "initial"

        def receive(value):
            nonlocal phase
            console.sent.append(value)
            self.assertNotIn(b"\n", value, "Serial Enter must be CR")
            if value == b"\r":
                console.text += "\r\n" + ("technician@slt-rescue:~$ " if authenticated else "slt-rescue login: ")
            elif value == b"technician\r" and phase == "initial":
                console.text += "technician\r\nPassword: "
                phase = "password"
            elif value == b"live\r" and phase == "password":
                console.text += "\r\nLogin incorrect\r\n" if reject else "\r\nDebian Live MOTD\r\ntechnician@slt-rescue:~$ "
                phase = "shell"
            elif value.startswith(b"sudo -n true") and phase == "shell":
                command = value.decode("ascii").rstrip("\r")
                console.text += command + "\r\n"
                if not echo_only:
                    parts = module.re.search(r"printf '%s%s\\n' '([^']+)' '([^']+)'", command)
                    completion = module.re.search(r"'(SLT_CMD_[a-f0-9]+_RC)'", command).group(1)
                    console.text += "\x1b[?2004l\r" + parts.group(1) + parts.group(2) + "\r\n" + completion + "=0\r\n\x1b[?2004htechnician@slt-rescue:~$ "
            else:
                self.fail("Unexpected serial input/state")
        console.connection = Mock()
        console.connection.sendall.side_effect = receive
        return console

    def test_fresh_username_password_shell_states_ignore_history(self):
        console = self.console()
        with contextlib.redirect_stdout(io.StringIO()):
            console.login("SLT_TEST_LOGIN_OK")
        self.assertEqual(console.sent[:3], [b"\r", b"technician\r", b"live\r"])
        self.assertEqual(len(console.sent), 4)

    def test_authenticated_prompt_does_not_resubmit_username_or_password(self):
        console = self.console(authenticated=True)
        with contextlib.redirect_stdout(io.StringIO()):
            console.login("SLT_TEST_LOGIN_OK")
        self.assertEqual(len(console.sent), 2)
        self.assertNotIn(b"live\r", console.sent)

    def test_rejected_login_fails_without_guessing_other_credentials(self):
        console = self.console(reject=True)
        with contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(AssertionError, "login rejected"):
                console.login("SLT_TEST_LOGIN_OK")
        self.assertEqual(console.sent, [b"\r", b"technician\r", b"live\r"])

    def test_command_echo_is_not_shell_completion(self):
        console = self.console(authenticated=True, echo_only=True)
        def check_once(check, _description, _timeout):
            value = check()
            if not value:
                raise AssertionError("No actual shell completion")
            return value
        with patch.object(module, "wait_for", side_effect=check_once), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(AssertionError, "No actual shell completion"):
                console.login("SLT_TEST_LOGIN_OK")


if __name__ == "__main__":
    unittest.main()
