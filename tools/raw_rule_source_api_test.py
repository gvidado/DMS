"""Isolated HTTP regression for APID-only workbook imports and automatic rule sources.

The server never starts a UDP listener, command monitor, or database connection.
"""
import copy
import io
import json
import secrets
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape

from integration_test import ROOT, Client, free_port, wait
from runtime_benchmark import seed


def workbook(sheets):
    """Each (name, outer source, APID) uses the supported old/new row layout."""
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('[Content_Types].xml', '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>')
        names = ''.join(f'<sheet name="{escape(name)}" sheetId="{i}" r:id="rId{i}"/>' for i, (name, _, _) in enumerate(sheets, 1))
        archive.writestr('xl/workbook.xml', '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>' + names + '</sheets></workbook>')
        links = ''.join(f'<Relationship Id="rId{i}" Target="worksheets/sheet{i}.xml" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>' for i in range(1, len(sheets) + 1))
        archive.writestr('xl/_rels/workbook.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + links + '</Relationships>')
        for i, (_, outer, apid) in enumerate(sheets, 1):
            cells = {}
            shift = 0 if outer is None else 1
            if outer is not None:
                cells.update(A2='包来源', B2='2字节', C2=str(outer))
            for row, bits, value in [(2, 3, 0), (3, 1, 0), (4, 1, 1), (5, 11, apid), (8, 16, 13), (11, 8, 3), (12, 8, 25), (16, 16, 4100), (17, 16, 0)]:
                cells['B' + str(row + shift)] = str(bits) + ' bit'
                cells['C' + str(row + shift)] = str(value)
            values = ''.join(f'<c r="{address}" t="inlineStr"><is><t>{escape(value)}</t></is></c>' for address, value in cells.items())
            archive.writestr(f'xl/worksheets/sheet{i}.xml', '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row>' + values + '</row></sheetData></worksheet>')
    return output.getvalue()


def main():
    executable = Path(sys.argv[1]).resolve()
    checks = []
    def check(value, label):
        assert value, label
        checks.append(label)
        print('PASS', label, flush=True)
    with tempfile.TemporaryDirectory(prefix='raw-rule-api-', dir=ROOT/'test-output') as scratch:
        root = Path(scratch)
        seed(root, per_packet=1, packets=1)
        shutil.copytree(ROOT/'web', root/'web')
        config = json.loads((root/'config/server.json').read_text(encoding='utf-8'))
        config['SourceIPs'] = ['127.0.0.1', '127.0.0.2']
        (root/'config/server.json').write_text(json.dumps(config), encoding='utf-8')
        client = Client(free_port())
        server_log = (root/'server-start.log').open('w', encoding='utf-8')
        proc = subprocess.Popen([str(executable), '--local', '--root', str(root), '--web-port', client.base.rsplit(':', 1)[1]], stdout=server_log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            def ready():
                try:
                    client.call('/api/auth/me')
                    return True
                except OSError:
                    return False
            try:
                wait(ready, 'isolated source-binding server')
            except AssertionError:
                server_log.flush()
                print((root/'server-start.log').read_text(encoding='utf-8', errors='replace'), flush=True)
                raise
            password = secrets.token_urlsafe(20)
            client.call('/api/auth/setup', dict(username='sourceqa', name='来源测试', password=password))
            client.csrf = client.call('/api/auth/login', dict(username='sourceqa', password=password))['csrf']
            field = client.call('/api/continuous-rules/parameters')[0]
            path = '/api/continuous-rules/source?parameterId=' + urllib.parse.quote(field['Id'])
            source = client.call(path)
            check(source['canAuto'] and source['sourceIP'] == '127.0.0.1', 'automatic rule source follows its SPP despite multiple listener IPs')
            Client(client.base.rsplit(':', 1)[1]).call(path, expected=401)
            client.call('/api/continuous-rules/source?parameterId=missing', expected=400)
            check(True, 'automatic source lookup requires login and rejects unknown parameters')
            rules = client.call('/api/continuous-rules')
            rule = rules['rows'][0]['rule']
            rule['SourceIP'] = ''
            client.call('/api/continuous-rules/save', dict(rule=rule, revision=rules['revision']))
            saved = client.call('/api/continuous-rules')['rows'][0]
            check(saved['rule']['SourceIP'] == '' and saved['sourceIP'] == '127.0.0.1' and saved['canExecute'], 'saving without choosing an IP persists automatic policy and an executable binding')
            original = client.call('/api/decoders')['configuration']['Packets'][0]

            def upload(data, path='/api/decoders/spp/batch/preview?filename=source-example.xlsx'):
                request = urllib.request.Request(client.base + path, data=data, headers={'Content-Type': 'application/octet-stream', 'X-CSRF-Token': client.csrf})
                return json.loads(client.opener.open(request, timeout=8).read())
            def apply(preview):
                return client.call('/api/decoders/apply', dict(token=preview['token'], revision=preview['revision'], mode='replace'))
            def snapshot():
                return client.call('/api/decoders')['configuration']['Packets']
            def imported(name):
                return next(p for p in snapshot() if p.get('SppSourceSheet') == name)
            def save_packet(packet, status=200):
                return client.call('/api/decoders/packet', dict(packet=packet, revision=client.call('/api/decoders')['revision']), expected=status)

            data = workbook([('斜杠来源', '/', 10), ('固定来源', '0202', 11)])
            preview = upload(data)
            slash, concrete = [row['sheet'] for row in preview['rows']]
            check(slash['OuterSourceUnrestricted'] and slash['Fields']['OuterSourceId'] == -1 and concrete['Fields']['OuterSourceId'] == 0x0202 and not concrete['OuterSourceUnrestricted'], 'multi-sheet workbook distinguishes explicit slash from a concrete outer source')
            single = upload(data, '/api/decoders/spp/preview?filename=source-example.xlsx&sheet=' + urllib.parse.quote('斜杠来源'))
            check(single['OuterSourceUnrestricted'] and single['Fields']['Apid'] == 10, 'single-sheet preview also preserves explicit slash and shifted field rows')
            apply(preview)
            packet = imported('斜杠来源')
            check(packet['OuterSourceId'] == -1 and not packet['Enabled'] and not packet['MonitorRaw'], 'new slash imports remain disabled until explicitly enabled for raw monitoring')
            check(next(p for p in snapshot() if p['Id'] == original['Id']) == original, 'workbook import preserves unrelated SPP definitions and existing mappings')
            packet['MonitorRaw'] = True
            save_packet(packet)
            check(imported('斜杠来源')['MonitorRaw'], 'imported slash source can be enabled as an APID-only raw monitor')
            invalid = copy.deepcopy(packet)
            invalid.update(Enabled=True, MappingConfirmed=True, Groups=['表0'])
            check('具体外层数据源' in save_packet(invalid, 400)['error'], 'slash cannot enable telemetry decoding or weaken the decoder source requirement')

            preview = upload(workbook([('固定来源', None, 11)]))
            legacy = preview['rows'][0]['sheet']
            check(not legacy['OuterSourceUnrestricted'] and legacy['Fields']['OuterSourceId'] == -1, 'legacy workbook missing the source row is distinct from explicit slash')
            apply(preview)
            check(imported('固定来源')['OuterSourceId'] == 0x0202, 'reimporting a legacy sheet retains the existing concrete source')
            apply(upload(workbook([('固定来源', '/', 11)])))
            check(imported('固定来源')['OuterSourceId'] == -1, 'explicit slash intentionally clears a prior concrete source when updating the same sheet')
            check(len(snapshot()) == 3 and next(p for p in snapshot() if p['Id'] == original['Id']) == original, 'partial workbook updates preserve other sheets and the original decoder mappings')
            check(client.call('/api/state')['mode'] == 'stopped', 'all HTTP source/import checks run with reception and sending stopped')
            report = ROOT/'test-output/raw-rule-source-api-report.json'
            report.write_text(json.dumps(dict(checks=len(checks), details=checks), ensure_ascii=False, indent=2), encoding='utf-8')
            print('All', len(checks), 'source/import API checks passed.')
        finally:
            proc.terminate()
            proc.wait(timeout=10)
            server_log.close()


if __name__ == '__main__':
    main()
