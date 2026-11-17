"""Per-listening-run warning identity using real UDP on loopback only."""
import copy, json, secrets, shutil, socket, struct, subprocess, sys, tempfile
from pathlib import Path
from integration_test import ROOT, Client, free_port, wait, packet, seal, seed_send_monitor
from decoder_integration_test import upload


def main():
    exe = Path(sys.argv[1]).resolve()
    checks = []
    def check(ok, label):
        assert ok, label
        checks.append(label); print('PASS', label, flush=True)
    with tempfile.TemporaryDirectory(prefix='alarm-run-') as temp:
        root = Path(temp); shutil.copytree(ROOT/'config', root/'config'); seed_send_monitor(root)
        for name in ['decoder.json', 'continuous-rules.json', 'tc-bindings.json', 'sequences.json', 'observed-commands.json']:
            (root/'config'/name).unlink(missing_ok=True)
        port = free_port(); udp = free_port(socket.SOCK_DGRAM)
        process = subprocess.Popen([str(exe), '--local', '--root', str(root), '--web-port', str(port)], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        try:
            c = Client(port)
            def ready():
                try: c.call('/api/auth/me'); return True
                except OSError: return False
            wait(ready, 'run alarm server'); password = secrets.token_urlsafe(18)
            c.call('/api/auth/setup', dict(username='runqa', name='Run QA', password=password))
            c.csrf = c.call('/api/auth/login', dict(username='runqa', password=password))['csrf']
            config = c.call('/api/decoders')['configuration']
            config['Catalog']['Parameters'] = [p for p in config['Catalog']['Parameters'] if p['Group']=='工仪全程' and p['Code']=='A1']
            base = copy.deepcopy(config['Packets'][0]); config['Packets'] = []
            for n in range(2):
                definition = copy.deepcopy(base)
                definition.update(Id='same-name-'+str(n), Name='同名包', Apid=8, ServiceSubtype=25+n, Enabled=True, MappingConfirmed=True, ExpectedLengthField=-1, ExpectedDatagramLength=0, SppOffset=-1)
                config['Packets'].append(definition)
            p = upload(c, 'alarm-targets.json', json.dumps(config).encode())
            c.call('/api/decoders/apply', dict(token=p['token'], revision=p['revision']))
            cfg = c.call('/api/config'); cfg.update(BindAddress='127.0.0.1', UdpPort=udp, SourceIPs=['127.0.0.1','127.0.0.2'], MulticastGroup='', MulticastInterface='', DataTimeoutMs=60000, EnablePacketTimeoutAlarm=False)
            c.call('/api/config', cfg); c.call('/api/control', dict(action='start'))
            def records(): return c.call('/api/alarms/list?pageSize=100')['records']
            def send(data, source='127.0.0.1'):
                received = c.call('/api/state')['received']
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                    sender.bind((source, 0)); sender.sendto(data, ('127.0.0.1', udp))
                wait(lambda: c.call('/api/state')['received'] == received+1, 'local packet')
            def sample(seq, subtype=25, bad=False, length=None):
                data = packet(seq); data[14] = subtype
                if length is not None: struct.pack_into('>H', data, 10, length)
                seal(data)
                if bad: data[-1] ^= 1
                return data
            send(sample(1, bad=True)); send(sample(2, bad=True))
            check(len(records())==1, 'changing frame sequence and UDP source port does not repeat a checksum warning')
            send(sample(1, bad=True), '127.0.0.2'); send(sample(1, subtype=26, bad=True))
            check(len(records())==3, 'different devices and distinct SPP IDs retain separate warnings even with identical names and APIDs')
            send(sample(3, length=1020)); send(sample(4, length=1021))
            check(len(records())==4 and sum('长度不符' in r['message'] for r in records())==1, 'length warnings ignore changing measured lengths and remain distinct from checksum warnings')
            c.call('/api/control', dict(action='ack')); c.call('/api/live-abnormal/clear', {})
            send(sample(5, bad=True)); send(sample(6, length=1022))
            check(len(records())==4 and all(r['confirmed'] for r in records()) and c.call('/api/live-abnormal/list')['count']==2, 'confirmation and clearing raw diagnostics preserve run deduplication while raw packets keep updating')
            for subtype in (25, 26):
                for seq in (10, 10, 11, 11): send(sample(seq, subtype))
            check(sum(r['category']=='重复帧' for r in records())==2 and c.call('/api/state')['duplicates']==4, 'different SPP IDs have separate duplicate warnings and every repeated frame is still counted')
            old = copy.deepcopy(records()); c.call('/api/control', dict(action='stop')); c.call('/api/control', dict(action='start'))
            send(sample(1, bad=True)); send(sample(1, bad=True), '127.0.0.2'); send(sample(1, subtype=26, bad=True))
            check(len(records())==len(old)+3, 'successfully restarting listening permits each warning again')
            now = {r['id']:r for r in records()}
            check(all(now[r['id']]==r for r in old), 'new listening runs retain all previous alarm contents and acknowledgement metadata')
            c.call('/api/control', dict(action='stop'))
        finally:
            process.terminate(); process.wait(timeout=6)
    print('All', len(checks), 'listening-run alarm checks passed.')


if __name__=='__main__': main()
