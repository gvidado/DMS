"""Timeout engineering-value snapshots through real loopback UDP and isolated storage."""
import csv, io, json, secrets, shutil, socket, struct, subprocess, sys, tempfile, time
from pathlib import Path
from urllib.parse import quote
from integration_test import ROOT, Client, free_port, wait, packet, seal, seed_send_monitor


def main():
    exe = Path(sys.argv[1]).resolve()
    checks = []

    def check(ok, label):
        assert ok, label
        checks.append(label)
        print('PASS', label, flush=True)

    with tempfile.TemporaryDirectory(prefix='sequence-timeout-values-', dir=ROOT/'test-output') as scratch:
        root = Path(scratch)
        shutil.copytree(ROOT/'config', root/'config')
        shutil.copytree(ROOT/'web', root/'web')
        for name in ['decoder.json', 'continuous-rules.json', 'tc-bindings.json', 'command-send.json', 'sequences.json', 'observed-commands.json']:
            (root/'config'/name).unlink(missing_ok=True)
        seed_send_monitor(root)
        udp, web = free_port(socket.SOCK_DGRAM), free_port()

        def save(name, value):
            (root/'config'/name).write_text(json.dumps(value), encoding='utf-8')

        settings = json.loads((root/'config/server.json').read_text(encoding='utf-8-sig'))
        settings.update(BindAddress='127.0.0.1', UdpPort=udp, SourceIPs=['127.0.0.1'], MappingConfirmed=True,
                        MulticastGroup='', MulticastInterface='', DataTimeoutMs=800)
        save('server.json', settings)
        fields = json.loads((root/'config/parameters.json').read_text(encoding='utf-8-sig'))
        exact = dict(fields[0], Id='timeout/QA_TIMEOUT_64', Code='QA_TIMEOUT_64', Name='exact integer', Offset=40, Length=8, Scale=1, Bias=0, Type='uint')
        fields.append(exact)
        save('parameters.json', fields)
        save('observed-commands.json', dict(SchemaVersion=1, SourceIP='127.0.0.1', NextNumber=2,
             Commands=[dict(Number=1, Name='loopback only', DestinationIP='127.0.0.2', DestinationIPs=['127.0.0.2'], PayloadHex='AA BB 01')]))
        save('tc-bindings.json', dict(SchemaVersion=1, Revision='fixture', Bindings=[dict(Code='K001', Number=1)]))
        definitions = []

        def row(condition='', command='', keyword='', timeout='.45'):
            return dict(Keyword=keyword, Description='timeout snapshot', Command=command, Condition=condition,
                        Mark='', Expected='.2' if keyword == '' else '', Timeout=timeout)

        def definition(mode, rows):
            n = len(definitions)+1
            d = dict(Id=f'{n:032x}', Code=('XLZ' if mode == 'execute' else 'XLP')+f'{n:04}',
                     Name=f'timeout-{mode}-{n}', Mode=mode, SourceIP='127.0.0.1', Rows=[dict(Row=i+2, **r) for i, r in enumerate(rows)])
            definitions.append(d)
            return d

        simple = definition('judge', [row('TM.A1.value == 99 && TM.A2.value == 99')])
        multiple = definition('judge', [row('TM.A1.value == 99 && TM.A4.value > 0 && TM.QA_TIMEOUT_64.value == 1')])
        command = definition('judge', [row('TM.A1.value == 99', command='K001')])
        expired = definition('judge', [row('TM.A1.value == 99', timeout='1')])
        execute = definition('execute', [row('TM.A1.value == 99')])
        hold = definition('judge', [row(keyword='PAR'), row('TM.A1.value > 0', keyword='HOLD'),
                                    row('TM.A2.value == 99', timeout='1'), row(keyword='CHANGE')])
        save('sequences.json', dict(SchemaVersion=1, Revision='fixture', DefaultTimeoutSeconds=5,
             NextJudgeNumber=10, NextExecuteNumber=10, Sequences=definitions))
        c = Client(web)
        proc = subprocess.Popen([str(exe), '--local', '--root', str(root), '--web-port', str(web)],
                                stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sender.bind(('127.0.0.1', 0))
        seq = 0

        def ready():
            try:
                c.call('/api/auth/me')
                return True
            except OSError:
                return False

        def state(d):
            return c.call('/api/sequence/state?id='+d['Id'])

        def start(d):
            c.call('/api/sequence/control', dict(action='start', id=d['Id'], runName=d['Name'],
                   requestId=secrets.token_hex(16), revision=c.call('/api/sequence/config')['revision']))
            wait(lambda: any(r['actual'] is not None for r in state(d)['rows']), 'step begins')
            return state(d)['runtime']['runId']

        def data(value, enum=0, invalid=False):
            nonlocal seq
            seq += 1
            raw = packet(seq)
            raw[22:27] = round(value*10000).to_bytes(5, 'big')
            raw[27:30] = enum.to_bytes(3, 'big')
            struct.pack_into('>f', raw, 35, float('nan') if invalid else -2.5)
            raw[62:70] = (2**64-1).to_bytes(8, 'big')
            sender.sendto(seal(raw), ('127.0.0.1', udp))
            wait(lambda: (c.call('/api/telemetry/history?reference=TM.A1&sourceIP=127.0.0.1')['current'] or {}).get('sequence') == seq, 'telemetry decoded')

        def finish(d, run):
            wait(lambda: not state(d)['runtime']['running'], 'sequence finishes')
            records = c.call('/api/sequence/history?runId='+run)['rows']
            return next(e for e in records if e['Event'] == '异常' and e['Status'] == '超时' and (d != hold or e['Input']['Keyword'] == 'HOLD'))

        def csv_values(path, run):
            rows = list(csv.DictReader(io.StringIO(c.opener.open(c.base+path).read().decode('utf-8-sig'))))
            return [json.loads(e['参数实际值']) for e in rows if e['事件'] == '异常' and e['状态'] == '超时']

        try:
            wait(ready, 'isolated timeout server')
            password = secrets.token_urlsafe(18)
            c.call('/api/auth/setup', dict(username='timeoutqa', name='Timeout QA', password=password))
            c.csrf = c.call('/api/auth/login', dict(username='timeoutqa', password=password))['csrf']
            c.call('/api/control', dict(action='start'))
            run = start(simple)
            empty = json.loads(finish(simple, run)['Values'])
            check(empty == {'TM.A1.value': '未接收', 'TM.A2.value': '未接收'}, 'no received data is explicitly recorded for every referenced parameter')
            data(12.3456, 65536)
            run = start(simple)
            previous = json.loads(finish(simple, run)['Values'])
            check('本步骤开始后无新数据' in previous['TM.A1.value'] and abs(float(previous['TM.A1.value'].split('（')[0])-12.3456)<1e-10 and previous['TM.A2.value'].startswith('65536'),
                  'values received before activation are recorded with a no-new-data annotation: '+str(previous))
            run = start(multiple)
            data(20.1234)
            time.sleep(.12)
            data(23.4567, invalid=True)
            event = finish(multiple, run)
            frozen = json.loads(event['Values'])
            check(abs(float(frozen['TM.A1.value'])-23.4567)<1e-10 and '数据无效' in frozen['TM.A4.value'],
                  'timeout resamples all values even when an invalid companion prevented predicate evaluation')
            check(frozen['TM.QA_TIMEOUT_64.value'] == str(2**64-1), '64-bit engineering integer remains exact in the timeout snapshot')
            check(next(r for r in state(multiple)['rows'] if r['input']['Row'] == 2)['values'] == frozen,
                  'live sequence results expose the same captured values')
            check(event['Abnormal']['value'] == event['Values'], 'unified sequence incident uses the timeout snapshot')
            check(csv_values('/api/sequence/export.csv?runId='+run, run) == [frozen], 'sequence CSV exports all timeout parameter values')
            archive = c.call('/api/judgment-archives?type=sequence-judge')['history']['rows']
            archive_id = next(a['id'] for a in archive if a['runId'] == run)
            check(csv_values('/api/judgment-archives/export.csv?id='+quote(archive_id), run) == [frozen], 'per-run archive CSV retains timeout values')
            original = event['Values']
            data(77)
            after = c.call('/api/sequence/history?runId='+run)['rows']
            check(next(e for e in after if e['Event'] == '异常')['Values'] == original,
                  'later telemetry cannot overwrite the persisted timeout snapshot')
            run = start(command)
            data(33.4567)
            check(abs(float(json.loads(finish(command, run)['Values'])['TM.A1.value'])-33.4567)<1e-10,
                  'waiting for a command still records the current condition parameter on timeout')
            run = start(expired)
            data(45.6789)
            check(json.loads(finish(expired, run)['Values'])['TM.A1.value'] == '45.6789（数据过期）',
                  'expired values retain their last actual engineering value and an explicit quality label')
            run = start(execute)
            data(56.789)
            check(abs(float(json.loads(finish(execute, run)['Values'])['TM.A1.value'])-56.789)<1e-10,
                  'execution termination persists the same timeout snapshot as judgment mode')
            run = start(hold)
            wait(lambda: any(r['input']['Keyword'] == 'HOLD' and r['actual'] is not None for r in state(hold)['rows']), 'HOLD begins')
            data(67.8901)
            hold_event = finish(hold, run)
            check(hold_event['Input']['Keyword'] == 'HOLD' and abs(float(json.loads(hold_event['Values'])['TM.A1.value'])-67.8901)<1e-10,
                  'HOLD no-new-data timeout also persists its actual value')
            events = [json.loads(s) for s in (root/'data/sequence-history.jsonl').read_text(encoding='utf-8-sig').splitlines() if s]
            check(any(e['Values'] == original and e['Event'] == '异常' for e in events), 'actual values are durable in the append-only history file')
            report = ROOT/'test-output/sequence-timeout-values/integration-report.json'
            report.write_text(json.dumps(dict(checks=len(checks), details=checks), ensure_ascii=False, indent=2), encoding='utf-8')
            print('All', len(checks), 'timeout snapshot checks passed.')
        finally:
            sender.close()
            proc.terminate()
            proc.wait(timeout=8)


if __name__ == '__main__':
    main()
