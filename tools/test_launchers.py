"""Exercise the real Windows CMD launchers from a Chinese directory containing spaces."""
import json
import os
import re
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    for name in ("本机预览.cmd", "启动服务器.cmd"):
        contents = (ROOT / name).read_bytes()
        assert all(c < 128 for c in contents), "Non-ASCII launcher content"
        assert b"\r\n" in contents and not re.search(rb"(?<!\r)\n", contents), "Expected Windows CRLF line endings"
        print("PASS ASCII and CRLF:", name, flush=True)
        with tempfile.TemporaryDirectory(prefix="启动检查 with spaces ") as temporary:
            directory = Path(temporary)
            for filename in (name, "TelemetryMonitor.exe", "TelemetryMonitor.exe.config"):
                shutil.copy2(ROOT / filename, directory / filename)
            shutil.copytree(ROOT / "config", directory / "config")
            shutil.copytree(ROOT / "web", directory / "web")
            with socket.socket() as available:
                available.bind(("127.0.0.1", 0))
                port = available.getsockname()[1]
            # CMD strips the outside quotes after /s /c; keep the path's own quotes inside.
            extra = " --local" if name == "启动服务器.cmd" else ""
            command = f'"{os.environ["COMSPEC"]}" /d /s /c ""{directory / name}"{extra} --web-port {port}"'
            process = subprocess.Popen(command, cwd=directory, stdin=subprocess.DEVNULL,
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
            success = False
            try:
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    if process.poll() is not None:
                        break
                    try:
                        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/auth/me", timeout=1) as response:
                            success = response.status == 200 and "setupRequired" in json.load(response)
                        if success:
                            break
                    except OSError:
                        time.sleep(.1)
                assert success, "Launcher failed to start the real HTTP server"
                print("PASS actual CMD start and HTTP response in Chinese/space path:", name, flush=True)
            finally:
                # Only the exact temporary process and its children created by this test are stopped.
                if process.poll() is None:
                    subprocess.run(["taskkill.exe", "/PID", str(process.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                output = process.communicate(timeout=5)[0].decode("utf-8", errors="replace")
                if not success:
                    print(output)
    print("All launcher checks passed. Existing server/config/accounts untouched.")


if __name__ == "__main__":
    main()
