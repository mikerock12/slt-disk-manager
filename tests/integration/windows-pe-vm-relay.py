"""Private VM fixture TCP relay. QEMU guestfwd invokes one copy per connection.

No configurable host, shell, credentials or remote commands: only host loopback.
This is a test utility, never part of a deployed SLT agent.
"""
import os
import socket
import sys
import threading


def main():
    if len(sys.argv) != 2 or not sys.argv[1].isdigit() or not 1 <= int(sys.argv[1]) <= 65535:
        raise SystemExit("Expected a loopback TCP port")
    if os.name == "nt":
        import msvcrt
        msvcrt.setmode(sys.stdin.fileno(), os.O_BINARY)
        msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)
    with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=15) as connection:
        connection.settimeout(None)

        def upload():
            try:
                while data := os.read(sys.stdin.fileno(), 65536):
                    connection.sendall(data)
                connection.shutdown(socket.SHUT_WR)
            except OSError:
                pass

        threading.Thread(target=upload, daemon=True).start()
        try:
            while data := connection.recv(65536):
                sys.stdout.buffer.write(data)
                sys.stdout.buffer.flush()
        except (OSError, BrokenPipeError):
            pass


if __name__ == "__main__":
    main()
