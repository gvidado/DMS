"""Integration checks against a separate real HTTP/UDP server; never uses a device IP."""
import http.cookiejar
import concurrent.futures
import json
import secrets
import shutil
import socket
import struct
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def seed_send_monitor(directory):
    """Configure only an isolated fixture for the now-unified live start; never edits deployment config."""
    path = Path(directory) / 'config' / 'send-monitor.json'
    path.write_text(json.dumps(dict(SourceIP='127.0.0.1', DestinationIP='127.0.0.2', InterfaceId='', DestinationPort=0)), encoding='utf-8')


def free_port(kind=socket.SOCK_STREAM):
    with socket.socket(socket.AF_INET, kind) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class Client:
    def __init__(self, port):
        self.base = f"http://127.0.0.1:{port}"
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.csrf = ""

    def call(self, path, body=None, expected=200, csrf=True):
        data = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers = {"Content-Type": "application/json"}
        if csrf:
            headers["X-CSRF-Token"] = self.csrf
        request = urllib.request.Request(self.base + path, data=data, headers=headers)
        try:
            response = self.opener.open(request, timeout=6)
        except urllib.error.HTTPError as error:
            response = error
        payload = response.read()
        assert response.status == expected, (path, response.status, payload.decode("utf-8"))
        return json.loads(payload)


def seal(data, start=6):
    a=b=0
    for value in data[start:-2]:
        a=(a+value)%255
        b=(b+a)%255
    first=(255-(a+b)%255)%255
    data[-2:]=bytes((first,(255-(a+first)%255)%255))
    return data


def packet(seq, apid=8):
    data = bytearray(1030)
    struct.pack_into(">HHH", data, 6, 0x0800 | apid, 0xC000 | seq, 1017)
    data[12:16] = bytes([0x30, 3, 0x19, 0])
    struct.pack_into(">H", data, 18, 0x1004)
    data[22:27] = (123456).to_bytes(5, "big")
    data[27:30] = bytes([2, 2, 1])
    struct.pack_into(">f", data, 35, -2.5)
    return seal(data)


def wait(predicate, label, seconds=5):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.04)
    raise AssertionError("timeout: " + label)


def replay_fixture(count=400, apids=(8,), interval_us=160000):
    def block(kind,body):
        body+=b'\0'*(-len(body)%4); size=len(body)+12
        return struct.pack('<II',kind,size)+body+struct.pack('<I',size)
    parts=[block(0x0a0d0d0a,struct.pack('<IHHq',0x1a2b3c4d,1,0,-1)),block(1,struct.pack('<HHI',101,0,65535))]
    for i in range(count):
        payload=packet(i//len(apids)+1,apids[i%len(apids)])
        udp=struct.pack('>HHHH',6000,7000,len(payload)+8,0)+payload
        ip=struct.pack('>BBHHHBBH4s4s',0x45,0,len(udp)+20,0,0,64,17,0,socket.inet_aton('127.0.0.1'),socket.inet_aton('127.0.0.1'))+udp
        ticks=1_700_000_000_000000+i*interval_us
        parts.append(block(6,struct.pack('<IIIII',0,ticks>>32,ticks&0xffffffff,len(ip),len(ip))+ip))
    return b''.join(parts)


def load_replay(client, data=None):
    request=urllib.request.Request(client.base+'/api/replay/upload?filename=demo.pcapng',data=replay_fixture() if data is None else data,headers={'Content-Type':'application/octet-stream','X-CSRF-Token':client.csrf})
    with client.opener.open(request,timeout=15) as response:return json.loads(response.read())


def main():
    checks = []
    def check(test, name):
        assert test, name
        checks.append(name)
        print("PASS", name, flush=True)

    with tempfile.TemporaryDirectory(prefix="telemetry-integration-") as scratch:
        directory = Path(scratch)
        shutil.copytree(ROOT / "config", directory / "config"); seed_send_monitor(directory)
        (directory / "config/decoder.json").unlink(missing_ok=True)
        # The isolated fixture must not inherit the deployment's configured device port.
        settings_path = directory / "config/server.json"
        settings = json.loads(settings_path.read_text(encoding="utf-8-sig"))
        settings['UdpPort'] = 0
        settings_path.write_text(json.dumps(settings), encoding="utf-8")
        shutil.copytree(ROOT / "web", directory / "web")
        web_port, udp_port = free_port(), free_port(socket.SOCK_DGRAM)
        process = subprocess.Popen([str(ROOT / "TelemetryMonitor.exe"), "--local", "--root", str(directory), "--web-port", str(web_port)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        try:
            admin, anon = Client(web_port), Client(web_port)
            def available():
                try:
                    return admin.call("/api/auth/me")["setupRequired"]
                except (OSError, AssertionError):
                    return False
            wait(available, "HTTP startup")
            anon.call("/api/state", expected=401)
            check(True, "unauthenticated parameter endpoint rejected")
            admin_password = secrets.token_urlsafe(16)
            admin.call("/api/auth/setup", {"username": "qa_admin", "name": "测试操作员", "password": admin_password})
            user = admin.call("/api/auth/login", {"username": "qa_admin", "password": admin_password})
            admin.csrf = user["csrf"]
            check(user["admin"] and len(user["selected"]) == 12, "first admin setup and hashed-password login")
            on_disk = (directory / "data" / "accounts.json").read_text(encoding="utf-8")
            check(admin_password not in on_disk and 'PasswordHash' in on_disk, "passwords never stored as plaintext")
            admin.call("/api/control", {"action": "demo"}, expected=403, csrf=False)
            check(True, "missing CSRF rejected")
            admin.call("/api/control", {"action": "start"}, expected=400)
            check(True, "unknown actual UDP port prevents accidental bind")
            config = admin.call("/api/config")
            config.update(UdpPort=udp_port, SourceIPs=["127.0.0.1"], BindAddress="127.0.0.1", ChecksumMode="sum8to16", MappingConfirmed=True, DataTimeoutMs=480)
            admin.call("/api/config", config)
            catalog = admin.call("/api/catalog")
            check(len(catalog) == 709, "full telemetry catalog loaded")
            admin.call("/api/selection", {"ids": ["工仪全程/A1", "工仪全程/A2", "工仪全程/A4"]})
            admin.call("/api/control", {"action": "start"})
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                def send(payload):
                    udp.sendto(payload, ("127.0.0.1", udp_port))
                    time.sleep(0.025)
                send(packet(16382)); send(packet(16383)); send(packet(0)); send(packet(0)); send(packet(3)); send(packet(2))
                invalid = packet(4); invalid[200] ^= 1; send(invalid)
                send(packet(3, apid=9))
                wait(lambda: admin.call("/api/state")["received"] >= 8, "UDP processing")
            state = admin.call("/api/state")
            check(state["accepted"] == 5 and state["duplicates"] == 1 and state["lost"] == 2 and state["outOfOrder"] == 1 and state["invalid"] == 1, "real UDP socket: duplicates, wrap, gaps, late frames and checksum failures")
            packet_counts={p['apid']:p for p in state['packetStatistics']}
            check(packet_counts[8]['received']==7 and packet_counts[8]['duplicates']==1 and packet_counts[8]['invalid']==1 and packet_counts[9]['received']==1 and packet_counts[10]['received']==0,'legacy per-APID statistics include checksum failures and separate duplicate receptions')
            rows = {r["id"]: r for r in state["rows"]}
            check(abs(float(rows["工仪全程/A1"]["value"]) - 12.3456) < 1e-6 and rows["工仪全程/A4"]["value"] == "-2.5", "UDP bytes decoded into scaled integer and signed float values")
            check(rows["工仪全程/A1"]["sequence"] == 3, "other APID does not overwrite selected packet parameters")
            check(rows["工仪全程/A1"]["hexSource"] == "0x000001E240" and rows["工仪全程/A4"]["hexSource"] == "0xC0200000", "live API carries exact hexadecimal source before scaling or floating-point conversion")
            admin.call("/api/config", config, expected=409)
            admin.call("/api/control", {"action": "demo"}, expected=409)
            check(True, "running config changes and simultaneous demo rejected")
            wait(lambda: admin.call("/api/state")["sources"][0]["alarm"], "offline latch")
            admin.call("/api/control", {"action": "ack"})
            check(not admin.call('/api/state')['sources'][0]['alarm'], 'confirmation clears an offline alarm without requiring data recovery')
            time.sleep(.3)
            check(not admin.call('/api/state')['sources'][0]['alarm'], 'the same acknowledged offline condition does not re-latch every health check')
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.sendto(packet(4), ("127.0.0.1", udp_port))
            wait(lambda: admin.call("/api/state")["accepted"] == 6, "recovery")
            wait(lambda:admin.call('/api/state')['sources'][0]['alarm'],'a new offline episode after recovery')
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.sendto(packet(5),('127.0.0.1',udp_port))
            wait(lambda:admin.call('/api/state')['accepted']==7,'second recovery')
            check(admin.call("/api/state")["sources"][0]["alarm"], "recovery does not silently clear latched alarm")
            admin.call("/api/control", {"action": "ack"})
            check(not admin.call("/api/state")["sources"][0]["alarm"], "manual acknowledgement after recovery")
            admin.call("/api/control", {"action": "stop"})
            check(admin.call("/api/state")["mode"] == "stopped", "stop closes UDP listener")
            config.update(MappingConfirmed=False, ChecksumMode="unverified", DataTimeoutMs=16000)
            admin.call("/api/config", config)
            admin.call("/api/control", {"action": "start"})
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.sendto(packet(1), ("127.0.0.1", udp_port))
            wait(lambda: admin.call("/api/state")["accepted"] == 1, "unverified packet header")
            check(all(r["value"] == "—" for r in admin.call("/api/state")["rows"]), "unconfirmed mapping cannot produce real parameter values")
            admin.call("/api/control", {"action": "stop"})
            config["SourceIPs"] = ["192.168.1.111"]
            admin.call("/api/config", config)
            admin.call("/api/control", {"action": "start"})
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.sendto(packet(1), ("127.0.0.1", udp_port))
            wait(lambda: admin.call("/api/state")["filtered"] == 1, "IP whitelist filtering")
            check(admin.call("/api/state")["accepted"] == 0, "non-whitelisted source does not enter parser")
            admin.call("/api/control", {"action": "stop"})
            load_replay(admin)
            admin.call("/api/control", {"action": "demo"})
            wait(lambda: admin.call("/api/state")["accepted"] >= 2, "demo timer")
            check(admin.call("/api/state")["mode"] == "demo", "explicit isolated demo mode")
            admin.call("/api/control", {"action": "stop"})
            other_password = secrets.token_urlsafe(16)
            admin.call("/api/users", {"username": "operator2", "name": "操作员二", "password": other_password})
            operator = Client(web_port)
            operator.csrf = operator.call("/api/auth/login", {"username": "operator2", "password": other_password})["csrf"]
            operator.call("/api/users", expected=403)
            operator.call("/api/selection", {"ids": ["工仪全程/A7"]})
            check(admin.call("/api/auth/me")["user"]["selected"] == ["工仪全程/A1", "工仪全程/A2", "工仪全程/A4"], "per-account selections isolated")
            reset_password = secrets.token_urlsafe(16)
            admin.call("/api/users/reset", {"username": "operator2", "password": reset_password})
            operator.call("/api/state", expected=401)
            check(True, "admin reset revokes existing sessions")
            anon.call("/data/accounts.json", expected=401)
            admin.call("/data/accounts.json", expected=404)
            check(True, "credential files never exposed by static server")
            request = urllib.request.Request(admin.base + "/api/events")
            with admin.opener.open(request, timeout=4) as response:
                line = response.readline().decode("utf-8")
                check(response.headers["Content-Type"].startswith("text/event-stream") and line.startswith("data: ") and json.loads(line[6:])["mode"] == "stopped", "authenticated live event stream")
            def another_viewer(_):
                viewer = Client(web_port)
                viewer.csrf = viewer.call("/api/auth/login", {"username": "qa_admin", "password": admin_password})["csrf"]
                with viewer.opener.open(viewer.base + "/api/events", timeout=6) as response:
                    return json.loads(response.readline().decode("utf-8")[6:])["mode"] == "stopped"
            with concurrent.futures.ThreadPoolExecutor(max_workers=7) as pool:
                check(all(pool.map(another_viewer, range(7))), "seven simultaneous authenticated browser event streams")
            process.terminate(); process.wait(timeout=5)
            process = subprocess.Popen([str(ROOT / "TelemetryMonitor.exe"), "--local", "--root", str(directory), "--web-port", str(web_port)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            fresh = Client(web_port)
            def persisted():
                try:
                    return not fresh.call("/api/auth/me")["setupRequired"]
                except (OSError, AssertionError):
                    return False
            wait(persisted, "restart")
            login = fresh.call("/api/auth/login", {"username": "qa_admin", "password": admin_password})
            check(login["selected"] == ["工仪全程/A1", "工仪全程/A2", "工仪全程/A4"], "accounts and selections survive server restart without database")
            print(f"All {len(checks)} integration checks passed.")
        finally:
            if process.poll() is None:
                process.terminate()
            process.communicate(timeout=5)


if __name__ == "__main__":
    main()
