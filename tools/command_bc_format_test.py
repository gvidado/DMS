"""B-format test release: real preview, UDP, sequence, capture and CSV on loopback only."""
import csv
import io
import json
import secrets
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

from integration_test import ROOT, Client, free_port, wait
from runtime_benchmark import seed


def seal(packet):
    packet = bytearray(packet)
    struct.pack_into('<I', packet, 4, len(packet) - 8)
    struct.pack_into('<I', packet, len(packet) - 4, sum(packet[:-4]))
    return bytes(packet)


def main():
    executable = Path(sys.argv[1]).resolve()
    checks = []

    def check(ok, label):
        assert ok, label
        checks.append(label)
        print('PASS', label, flush=True)

    b = bytes.fromhex('EB 90 EB 90 30 00 00 00 AA 00 01 00 00 00 01 00 00 00 00 00 53 15 20 00 00 00 11 01 1A 08 C0 00 00 11 30 02 0A 00 00 00 00 00 01 08 00 01 0D 37 0D 37 07 36 6A 06 00 00')
    broadcast = bytes.fromhex('EB 90 EB 90 30 00 00 00 07 00 00 00 01 00 00 00 01 00 00 00 53 15 20 00 00 00 1F 01 1A 70 C0 00 00 11 30 02 0A 00 00 00 00 00 01 01 00 01 00 05 00 05 DB 7E D4 06 00 00')
    changed = bytearray(b)
    changed[8:18] = bytes.fromhex('FE FF 05 00 00 00 07 00 00 00')
    changed = seal(changed)
    injection = seal(bytes.fromhex('EB90EB90000000003412010000000100000000005315200000001104') + bytes(range(64)) + b'\0' * 4)
    bad_sum = bytearray(broadcast)
    bad_sum[-1] ^= 1
    bad_length = bytearray(broadcast)
    bad_length[4] ^= 1
    samples = [b, broadcast, changed, injection, b'\xDE\xAD\x10\x10', bytes(bad_sum), bytes(bad_length), b'']
    expected = [
        b,
        seal(broadcast[:10] + bytes.fromhex('0100000001000000') + broadcast[18:]),
        seal(changed[:10] + bytes.fromhex('0100000001000000') + changed[18:]),
        injection, *samples[4:],
    ]

    with tempfile.TemporaryDirectory(dir=ROOT / 'test-output', prefix='bc-format-test-') as temporary:
        root = Path(temporary)
        seed(root, per_packet=2, packets=1)
        for directory in ['web', 'native', 'database']:
            shutil.copytree(ROOT / directory, root / directory)

        def save(name, value):
            (root / 'config' / name).write_text(json.dumps(value), encoding='utf-8')

        settings = json.loads((root / 'config/server.json').read_text(encoding='utf-8-sig'))
        settings['UdpPort'] = free_port(socket.SOCK_DGRAM)
        save('server.json', settings)
        receivers = []
        for ip in ['127.0.0.2', '127.0.0.3']:
            receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            receiver.bind((ip, 0))
            receiver.settimeout(.3)
            receivers.append(receiver)
        source_ports = [free_port(socket.SOCK_DGRAM), free_port(socket.SOCK_DGRAM)]
        save('command-send.json', dict(SourceIP='127.0.0.1', Routes=[dict(
            DestinationIP=s.getsockname()[0], SourcePort=source_ports[i], DestinationPort=s.getsockname()[1])
            for i, s in enumerate(receivers)]))
        save('send-monitor.json', dict(SourceIPs=['127.0.0.1'], DestinationIPs=['127.0.0.2', '127.0.0.3'], InterfaceId='', DestinationPort=0))
        save('observed-commands.json', dict(SchemaVersion=1, SourceIP='127.0.0.1', NextNumber=9, Commands=[dict(
            Number=i, Name=f'格式测试{i}', DestinationIP='127.0.0.2', DestinationIPs=['127.0.0.2', '127.0.0.3'],
            PayloadHex=sample.hex(' '), Categories=[]) for i, sample in enumerate(samples, 1)]))
        save('tc-bindings.json', dict(SchemaVersion=1, Revision='format-test', Bindings=[dict(Number=i, Code=f'K{i:03}') for i in range(1, 9)]))
        sid = 'b' * 32
        save('sequences.json', dict(SchemaVersion=1, Revision='format-test', DefaultTimeoutSeconds=2, NextExecuteNumber=2, NextJudgeNumber=1, Sequences=[dict(
            Id=sid, Code='XLZ0001', Name='广播令格式测试', Mode='execute', SourceIP='127.0.0.1', Rows=[dict(
                Row=2, Keyword='', Description='发送统一格式广播令', Command='K002', Condition='', Mark='', Expected='.2', Timeout='2')])]))
        c = Client(free_port())
        port = c.base.rsplit(':', 1)[1]
        log = (root / 'server.log').open('wb')
        process = subprocess.Popen([str(executable), '--local', '--root', str(root), '--web-port', port],
            stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)

        def ready():
            try:
                c.call('/api/auth/me')
                return True
            except OSError:
                return False

        def no_data():
            for receiver in receivers:
                try:
                    receiver.recvfrom(65535)
                    return False
                except socket.timeout:
                    pass
            return True

        try:
            wait(ready, 'B-format test server')
            password = secrets.token_urlsafe(18)
            c.call('/api/auth/setup', dict(username='formatqa', name='格式测试', password=password))
            c.csrf = c.call('/api/auth/login', dict(username='formatqa', password=password))['csrf']
            catalog = root / 'config/observed-commands.json'
            original = catalog.read_bytes()
            if '--browser' in sys.argv:
                runtime = Path.home() / '.cache/codex-runtimes/codex-primary-runtime/dependencies/node'
                browser_input = dict(base=c.base, password=password, screenshot=str(executable.parent / 'command-bc-format-preview.png'), samples=[dict(
                    number=i, hex=expected[i-1].hex(' ').upper(), dataHex=samples[i-1][24:-4].hex(' ').upper() if i <= 4 else '') for i in [1, 2, 4, 5]])
                result = subprocess.run([str(runtime / 'bin/node.exe'), str(ROOT / 'tools/command_bc_format_browser.mjs'), str(runtime)],
                    input=json.dumps(browser_input), text=True, encoding='utf-8', capture_output=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
                if result.returncode:
                    raise AssertionError(result.stdout + result.stderr)
                check('PASS browser' in result.stdout and no_data(), 'real browser previews transformed broadcast plus unchanged B/injection/raw bytes without transmitting')
            for number, (sample, sent) in enumerate(zip(samples, expected), 1):
                preview = c.call('/api/command-send/preview', dict(number=number))
                check(bytes.fromhex(preview['hex']) == sent and preview['length'] == len(sent), f'preview {number} returns actual prepared bytes and length')
                result = c.call('/api/command-send/send', dict(token=preview['token']))
                received = [receiver.recvfrom(65535) for receiver in receivers]
                check(result['ok'] and result['succeeded'] == 2 and all(data == sent and origin == ('127.0.0.1', source_ports[i])
                    for i, (data, origin) in enumerate(received)), f'manual {number} sends the exact preview once to both destinations')
                if number <= 4:
                    check(preview['dataContentRecognized'] and bytes.fromhex(preview['dataContentHex']) == sample[24:-4]
                          and sent[:4] == sample[:4] and sent[8:10] == sample[8:10] and sent[18:-4] == sample[18:-4],
                          f'frame {number} preserves counter, interface, routing and every inner byte')
                    check(sent[10:18] == bytes.fromhex('0100000001000000') and struct.unpack_from('<I', sent, 4)[0] == len(sent) - 8
                          and struct.unpack_from('<I', sent, len(sent) - 4)[0] == sum(sent[:-4]), f'frame {number} has B packet counts and valid outer length/checksum')
                else:
                    check(not preview['dataContentRecognized'] and sent == sample, f'raw/invalid frame {number} remains byte-exact')
                c.call('/api/command-send/send', dict(token=preview['token']), expected=400)
            check(no_data(), 'single-use send tokens never cause extra UDP datagrams')
            check(expected[1][-4:] == broadcast[-4:] and expected[2][-4:] != changed[-4:], 'permuted counts retain byte-sum while changed count values recalculate it')
            check(catalog.read_bytes() == original, 'manual previews and sends never rewrite original catalog payloads')
            exported = list(csv.reader(io.StringIO(c.opener.open(c.base + '/api/command-send/export.csv').read().decode('utf-8-sig'))))
            actual = [bytes.fromhex(row[-1]) for row in exported[1:]]
            check(actual == [sample for sample in reversed(expected) for _ in receivers], 'send CSV records prepared bytes including empty and invalid raw payloads')
            defaults = c.call('/api/send-monitor/interfaces')['interfaces']
            adapter = next(item for item in defaults if item['loopback'])
            c.call('/api/send-monitor/config', dict(SourceIPs=['127.0.0.1'], DestinationIPs=['127.0.0.2', '127.0.0.3'], InterfaceId=adapter['id'], DestinationPort=0))
            c.call('/api/control', dict(action='start'))
            config = c.call('/api/sequence/config')
            run = c.call('/api/sequence/control', dict(action='start', id=sid, revision=config['revision'], requestId=secrets.token_hex(16), runName='格式回归'))['runId']
            received = [receiver.recvfrom(65535) for receiver in receivers]
            check(all(data == expected[1] and origin[1] == source_ports[i] for i, (data, origin) in enumerate(received)), 'sequence execution uses the same prepared broadcast bytes and source ports')
            wait(lambda: not c.call('/api/sequence/state?runId=' + run)['runtime']['running'], 'sequence send completes')
            check(c.call('/api/sequence/state?runId=' + run)['runtime']['abnormal'] == 0 and no_data(), 'sequence sends once per destination and completes normally')
            wait(lambda: c.call('/api/send-monitor/state')['total'] == 2, 'both real loopback captures')
            records = c.call('/api/send-monitor/list')['records']
            check(len(records) == 2 and all(row['commandMatched'] for row in records) and catalog.read_bytes() == original,
                  'passive capture matches normalized broadcast to its existing library entry without duplicates')
            c.call('/api/control', dict(action='stop'))
            exported = list(csv.reader(io.StringIO(c.opener.open(c.base + '/api/command-send/export.csv').read().decode('utf-8-sig'))))
            check(all(bytes.fromhex(row[-1]) == expected[1] for row in exported[1:3]), 'sequence send CSV uses the transmitted normalized source')
            (executable.parent / 'command-bc-format-report.json').write_text(json.dumps(dict(checks=len(checks), details=checks), ensure_ascii=False, indent=2), encoding='utf-8')
        except Exception:
            log.flush()
            print((root / 'server.log').read_text(encoding='utf-8', errors='replace')[-3000:])
            raise
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(10)
            log.close()
            for receiver in receivers:
                receiver.close()
    print(f'All {len(checks)} B-format checks passed. Loopback only; no device packets sent.')


if __name__ == '__main__':
    main()
