"""Exercise the actual SPP workbook through an isolated HTTP server, including invalid input."""
import io
import json
import secrets
import shutil
import subprocess
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

from integration_test import Client, ROOT, free_port, wait, load_replay, seed_send_monitor


def main():
    checks = []
    def check(ok, name):
        assert ok, name
        checks.append(name)
        print('PASS', name, flush=True)

    original = (ROOT.parent / 'SPP.xlsx').read_bytes()
    ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
    def modified(cells=None, sheet=None, formula=False, target='xl/worksheets/sheet1.xml'):
        memory = io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(original)) as source, zipfile.ZipFile(memory, 'w') as output:
            for name in source.namelist():
                data = source.read(name)
                if name == target and cells:
                    xml = ET.fromstring(data)
                    for address, value in cells.items():
                        cell = next(c for c in xml.iter(ns + 'c') if c.get('r') == address)
                        cell.clear(); cell.set('r', address)
                        if value is not None:
                            cell.set('t', 'inlineStr'); inline = ET.SubElement(cell, ns + 'is'); ET.SubElement(inline, ns + 't').text = value
                        elif formula:
                            ET.SubElement(cell, ns + 'f').text = '1000+17'
                    data = ET.tostring(xml, encoding='utf-8', xml_declaration=True)
                if name == 'xl/workbook.xml' and sheet:
                    xml = ET.fromstring(data); next(xml.iter(ns + 'sheet')).set('name', sheet)
                    data = ET.tostring(xml, encoding='utf-8', xml_declaration=True)
                output.writestr(name, data)
        return memory.getvalue()

    with tempfile.TemporaryDirectory(prefix='telemetry-spp-') as scratch:
        directory = Path(scratch)
        shutil.copytree(ROOT / 'config', directory / 'config'); seed_send_monitor(directory); shutil.copytree(ROOT / 'web', directory / 'web')
        (directory / 'config/decoder.json').unlink(missing_ok=True)
        port = free_port()
        process = subprocess.Popen([str(ROOT / 'TelemetryMonitor.exe'), '--local', '--root', str(directory), '--web-port', str(port)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        try:
            client, anonymous = Client(port), Client(port)
            def ready():
                try: return client.call('/api/auth/me')['setupRequired']
                except (OSError, AssertionError): return False
            wait(ready, 'SPP fixture startup')
            password = secrets.token_urlsafe(18)
            client.call('/api/auth/setup', {'username': 'spp_admin', 'name': 'SPP tester', 'password': password})
            client.csrf = client.call('/api/auth/login', {'username': 'spp_admin', 'password': password})['csrf']
            def upload(data=original, filename='SPP.xlsx', sheet='', expected=200, actor=client, csrf=True, route='spp/preview'):
                url = actor.base + '/api/decoders/' + route + '?filename=' + urllib.parse.quote(filename) + '&sheet=' + urllib.parse.quote(sheet)
                request = urllib.request.Request(url, data=data, headers={'Content-Type': 'application/octet-stream', 'X-CSRF-Token': actor.csrf if csrf else ''})
                try: response = actor.opener.open(request, timeout=10)
                except urllib.error.HTTPError as error: response = error
                payload = json.loads(response.read().decode('utf-8'))
                assert response.status == expected, (response.status, payload)
                return payload

            before = client.call('/api/decoders')
            preview = upload()
            expected_fields = dict(Version=0, PacketType=0, SecondaryHeader=1, Apid=8, ExpectedLengthField=1017,
                                   ServiceType=3, ServiceSubtype=25, SourceAddress=4100, DestinationAddress=0)
            check(preview['Fields'] == expected_fields and [r['Row'] for r in preview['Rows']] == [2,3,4,5,8,11,12,16,17], 'actual workbook extracts exactly the nine requested rows')
            check(preview['IdentifierHex'] == '0x0808' and preview['IdentifierBits'] == '0000100000001000', '3+1+1+11 bits compose a 16-bit MSB-first identifier')
            legacy = upload(modified({'C5': '第一包：0b 00000001000（源APID），主导头包标识：0x0808'}))
            check(legacy['Fields']['Apid'] == 8, 'C5 binary APID takes precedence over full hexadecimal identifier in prose')
            check(upload(modified({'C5': '00000001001'}))['Fields']['Apid'] == 9, 'unprefixed full-width APID is read as binary')
            check(preview['Fields']['ExpectedLengthField'] + 7 == 1024, 'C8 selects annotated decimal length instead of explanatory 1 and 7')
            after = client.call('/api/decoders')
            after['configuration']['Catalog']['GeneratedAt'] = before['configuration']['Catalog']['GeneratedAt']
            check(after == before and client.call('/api/state')['received'] == 0 and not (directory / 'config/decoder.json').exists(), 'preview changes neither saved config, revision nor live telemetry')
            upload(actor=anonymous, expected=401); upload(csrf=False, expected=403)
            check(True, 'SPP import enforces login and CSRF')
            renamed = upload(modified(sheet='第一包定义'), filename='主导头配置.xlsx', sheet='第一包定义')
            check(renamed['SourceFile'] == '主导头配置.xlsx' and renamed['Sheet'] == '第一包定义', 'UTF-8 filenames and explicit worksheet names survive HTTP decoding')
            upload(sheet='missing', expected=400); upload(filename='bad.xls', expected=400); upload(b'not an xlsx', expected=400)
            check(True, 'missing sheet, unsupported extension and corrupt file produce readable errors')
            for row, bits in [(2,3),(3,1),(4,1),(5,11),(8,16),(11,8),(12,8),(16,16),(17,16)]:
                error = upload(modified({'B'+str(row): str(bits+1)+' bits'}), expected=400)
                assert 'B'+str(row) in error['error']
                error = upload(modified({'C'+str(row): str(1 << bits)}), expected=400)
                assert 'C'+str(row) in error['error']
            check(True, 'each of nine fields rejects wrong B-column widths and out-of-range values with exact cell addresses')
            maxima = upload(modified({'C2':'0b111','C3':'1','C4':'1','C5':'0x7FF', 'C8':'65535', 'C11':'255','C12':'0xFF','C16':'0xFFFF','C17':'65535'}))
            check(maxima['IdentifierHex'] == '0xFFFF' and maxima['IdentifierBits'] == '1'*16 and maxima['Fields']['SourceAddress'] == 65535, 'all standard bit-width maxima are accepted without truncation')
            for value in ['0b002', '-1', '8.5', 'APID待定', '0b00000001000 或 0b00000001001']:
                error = upload(modified({'C5': value}), expected=400); assert 'C5' in error['error']
            check(True, 'malformed, negative, fractional, absent and ambiguous APIDs are rejected')
            error = upload(modified({'C8': '总包长减7，包数据域减1'}), expected=400)
            check('C8' in error['error'], 'length prose without an explicit number is never guessed')
            error = upload(modified({'C8': None}, formula=True), expected=400)
            check('C8' in error['error'] and '公式' in error['error'], 'formula without cached value is rejected at the source cell')
            packet = before['configuration']['Packets'][0]
            groups, checksum = packet['Groups'][:], packet['ChecksumMode']
            packet.update(preview['Fields'])
            client.call('/api/decoders/packet', {'packet': packet, 'revision': before['revision']})
            saved = client.call('/api/decoders')['configuration']['Packets'][0]
            check(all(saved[k] == v for k,v in expected_fields.items()) and saved['Groups'] == groups and saved['ChecksumMode'] == checksum, 'existing save path persists imported fields while preserving parameter groups and checksum choice')
            stored = json.loads((directory / 'config/decoder.json').read_text(encoding='utf-8'))
            check(stored['Packets'][0]['SourceAddress'] == 4100 and stored['Packets'][0]['Apid'] == 8, 'saved offline JSON contains the imported packet values')
            baseline = client.call('/api/decoders')
            batch = upload(route='spp/batch/preview')
            check([r['sheet']['Sheet'] for r in batch['rows']] == ['Sheet1','Sheet2','Sheet3'] and [r['sheet']['Fields']['Apid'] for r in batch['rows']] == [8,9,10], 'updated workbook reads one SPP packet per sheet, including sheet one')
            check([r['sheet']['IdentifierHex'] for r in batch['rows']] == ['0x0808','0x0809','0x080A'], 'all three 16-bit identifiers are computed separately')
            check(client.call('/api/decoders') == baseline, 'multi-sheet preview leaves live configuration unchanged')
            failed = upload(modified({'B5':'12 bit'}, target='xl/worksheets/sheet2.xml'), route='spp/batch/preview', expected=400)
            check('Sheet2!B5' in failed['errors'][0] and client.call('/api/decoders') == baseline, 'bad second worksheet rejects the whole import with its exact source cell')
            upload(route='spp/batch/preview', actor=anonymous, expected=401); upload(route='spp/batch/preview', csrf=False, expected=403)
            check(True, 'batch import also enforces authentication and CSRF')
            candidate = client.call('/api/decoders/preview/export?token=' + batch['token'])
            check(len(candidate['Packets']) == 4 and all(not p['Enabled'] and not p['MappingConfirmed'] for p in candidate['Packets'] if p['SppSourceFile']), 'downloadable preview preserves existing packet and adds three stopped drafts')
            load_replay(client)
            client.call('/api/control', {'action':'demo'})
            client.call('/api/decoders/apply', {'token':batch['token'], 'revision':batch['revision']}, expected=409)
            client.call('/api/control', {'action':'stop'})
            client.call('/api/decoders/apply', {'token':batch['token'], 'revision':batch['revision']})
            check(len(client.call('/api/decoders')['configuration']['Packets']) == 4, 'batch persists all packets atomically and refuses changes while reception is active')
            after_batch = client.call('/api/decoders')
            imported = next(p for p in after_batch['configuration']['Packets'] if p['SppSourceSheet'] == 'Sheet3')
            imported.update(Groups=['工仪全程'], ChecksumMode='sum8to16', SourceIP='127.0.0.1', Enabled=True, MappingConfirmed=True, Name='第三包自定义名称')
            client.call('/api/decoders/packet', {'packet':imported, 'revision':after_batch['revision']})
            repeated = upload(route='spp/batch/preview')
            recovered = client.call('/api/decoders/preview/export?token=' + repeated['token'])
            revised = next(p for p in recovered['Packets'] if p['SppSourceSheet'] == 'Sheet3')
            check(all(r['action'] == '更新' for r in repeated['rows']) and len(recovered['Packets']) == 4 and revised['Id'] == imported['Id'], 'same filename and sheet update stable packet IDs without adding duplicates')
            check(revised['Name'] == imported['Name'] and revised['Groups'] == imported['Groups'] and revised['ChecksumMode'] == 'fletcher16' and revised['ChecksumStart'] == 0 and revised['SourceIP'] == '127.0.0.1' and not revised['Enabled'] and not revised['MappingConfirmed'], 'reimport preserves mappings, normalizes checksum and requires renewed enable and mapping confirmation')
            current = client.call('/api/decoders'); another = current['configuration']['Packets'][0]; another['Name'] = 'Changed concurrently'
            client.call('/api/decoders/packet', {'packet':another, 'revision':current['revision']})
            client.call('/api/decoders/apply', {'token':repeated['token'], 'revision':repeated['revision']}, expected=409)
            check(True, 'a stale batch cannot overwrite another user edit')
            duplicates = upload(modified({'C5':'00000001001'}), route='spp/batch/preview')
            check(any('APID 9' in message for message in duplicates['warnings']), 'duplicate APIDs are reported while stopped drafts remain importable')
            current_batch = upload(route='spp/batch/preview')
            client.call('/api/decoders/apply', {'token':current_batch['token'], 'revision':current_batch['revision']})
            final_config = client.call('/api/decoders')['configuration']
            process.terminate(); process.wait(timeout=6)
            process = subprocess.Popen([str(ROOT / 'TelemetryMonitor.exe'), '--local', '--root', str(directory), '--web-port', str(port)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            def restarted():
                try: return client.call('/api/auth/me')['setupRequired'] is False
                except (OSError, AssertionError): return False
            wait(restarted, 'batch restart')
            client.csrf = client.call('/api/auth/login', {'username':'spp_admin','password':password})['csrf']
            check(client.call('/api/decoders')['configuration'] == final_config, 'all imported packet identities and settings survive a real process restart')
            check((ROOT.parent / 'SPP.xlsx').read_bytes() == original, 'source SPP workbook remains unchanged')
        finally:
            process.terminate(); process.wait(timeout=6)
    print('All', len(checks), 'SPP integration checks passed.')


if __name__ == '__main__':
    main()
