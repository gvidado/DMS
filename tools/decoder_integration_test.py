"""Read the actual workbook, test config generation/upload/SPP editing and real UDP routing."""
import copy
import io
import json
import secrets
import shutil
import socket
import struct
import subprocess
import tempfile
import urllib.error
import urllib.request
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

from integration_test import Client, ROOT, free_port, packet, seal, wait, load_replay, replay_fixture, seed_send_monitor


def upload(client, filename, data, expected=200, csrf=True):
    request = urllib.request.Request(client.base + "/api/decoders/preview?filename=" + urllib.parse.quote(filename), data=data,
                                    headers={"Content-Type": "application/octet-stream", "X-CSRF-Token": client.csrf if csrf else ""})
    try:
        response = client.opener.open(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    text = response.read().decode("utf-8")
    assert response.status == expected, (response.status, text)
    return json.loads(text)


def main():
    checks = []
    def check(condition, name):
        assert condition, name
        checks.append(name)
        print("PASS", name, flush=True)
    workbook = ROOT.parent / "遥测格式.xlsx"
    original = workbook.read_bytes()
    with tempfile.TemporaryDirectory(prefix="telemetry-decoder-") as scratch:
        directory = Path(scratch)
        shutil.copytree(ROOT / "config", directory / "config"); seed_send_monitor(directory)
        (directory / "config/decoder.json").unlink(missing_ok=True)
        shutil.copytree(ROOT / "web", directory / "web")
        generated = directory / "generated.json"
        subprocess.run([str(ROOT / "TelemetryMonitor.exe"), "--generate-decoder", str(workbook), "--output", str(generated)], check=True, capture_output=True)
        config = json.loads(generated.read_text(encoding="utf-8"))
        source = config["Catalog"]
        check(config["SchemaVersion"] == 2 and len(source["Parameters"]) == 709 and [s["Index"] for s in source["Sheets"]] == [2, 3, 4, 5, 6], "native CLI reads from sheet 2 and emits portable JSON")
        params = {p["Code"]: p for p in source["Parameters"]}
        supported_count=sum(p["Supported"] for p in params.values())
        check(supported_count == (709 if params['c79']['Supported'] else 708) and (params['c79']['Supported'] or '数据类型' in params['c79']['Diagnostic']), "current workbook data types are respected independently of column M")
        check(all('Formula' not in p for p in params.values()), "generated config has no formula identifiers")
        check([(params[k]["Length"], params[k]["BitCount"]) for k in ("A278", "A281", "A282")] == [(2, 12), (2, 5), (2, 6)], "12-bit and unaligned cross-byte fields correctly span two bytes")
        check(params["A1"]["Length"] == 5 and params["A1"]["Scale"] == .0001 and params["d1"]["Type"] == "int" and params["A4"]["Type"] == "float32", "workbook integer/float/scaling definitions preserved")
        check(' / ' in params["A2"]["Enums"][str(0x020207)], "ambiguous enumeration is preserved and reported")
        check(params['c97']['Enums']['5'].startswith('101：') and '101' not in params['c97']['Enums'], "binary notation inside enum description is not mistaken for a new enum key")
        adjacent = [p for p in source["Parameters"] if '[12,12]' in p["Note"] and '变轨[15,15]' in p["Note"]]
        check(adjacent and all('12' in p['Enums'] and '15' in p['Enums'] and '[15,15]' not in p['Enums']['12'] for p in adjacent), "missing semicolon between enum entries does not merge meanings")
        web_port, udp_port = free_port(), free_port(socket.SOCK_DGRAM)
        process = subprocess.Popen([str(ROOT / "TelemetryMonitor.exe"), "--local", "--root", str(directory), "--web-port", str(web_port)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        try:
            client = Client(web_port)
            def available():
                try: return client.call('/api/auth/me')["setupRequired"]
                except OSError: return False
            wait(available, "server ready")
            password = secrets.token_urlsafe(16)
            client.call('/api/auth/setup', {"username": "decoder_admin", "name": "解码测试员", "password": password})
            client.csrf = client.call('/api/auth/login', {"username": "decoder_admin", "password": password})["csrf"]
            upload(client, workbook.name, original, expected=403, csrf=False)
            check(True, "binary Excel upload requires authentication and CSRF")
            before = client.call('/api/decoders')
            preview = upload(client, workbook.name, original)
            check(not client.call('/api/decoders')["native"] and preview["configuration"]["Catalog"]["SourceFile"] == workbook.name, "Excel preview does not change active decoding")
            downloaded = client.call('/api/decoders/preview/export?token=' + preview["token"])
            check(len(downloaded["Catalog"]["Parameters"]) == 709, "generated JSON can be downloaded before application")
            def change_columns(overrides=None, garbage=False, by_sheet=None):
                memory = io.BytesIO()
                ns = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
                with zipfile.ZipFile(io.BytesIO(original)) as z, zipfile.ZipFile(memory, 'w', zipfile.ZIP_DEFLATED) as out:
                    for name in z.namelist():
                        data = z.read(name)
                        if name.startswith('xl/worksheets/') and name.endswith('.xml'):
                            root = ET.fromstring(data)
                            for row in root.iter(ns + 'row'):
                                for cell in list(row):
                                    if cell.tag == ns + 'c' and cell.get('r', '').rstrip('0123456789') == 'M':
                                        if garbage:
                                            cell.clear(); cell.set('r', 'M' + row.get('r')); cell.set('t', 'inlineStr'); inline = ET.SubElement(cell, ns + 'is'); ET.SubElement(inline, ns + 't').text = 'UNKNOWN_FORMULA_IGNORE_ME'
                                        else: row.remove(cell)
                            changes = dict(overrides or {}) if name == 'xl/worksheets/sheet2.xml' else {}
                            changes.update((by_sheet or {}).get(name, {}))
                            if changes:
                                for address, value in changes.items():
                                    row = next(r for r in root.iter(ns + 'row') if r.get('r') == address[1:])
                                    cell = next((c for c in row if c.get('r') == address), None)
                                    if cell is None: cell = ET.SubElement(row, ns + 'c')
                                    cell.clear(); cell.set('r', address)
                                    if value is not None:
                                        cell.set('t', 'inlineStr'); inline = ET.SubElement(cell, ns + 'is'); ET.SubElement(inline, ns + 't').text = value
                            data = ET.tostring(root, encoding='utf-8', xml_declaration=True)
                        out.writestr(name, data)
                return memory.getvalue()
            without_m = upload(client, 'without-m.xlsx', change_columns())
            check(without_m['configuration']['Catalog']['Parameters'] == source['Parameters'], "removing every M cell leaves all 709 parameter definitions unchanged")
            garbage_m = upload(client, 'garbage-m.xlsx', change_columns(garbage=True))
            check(garbage_m['configuration']['Catalog']['Parameters'] == source['Parameters'], "arbitrary M-column strings do not influence supported types or scaling")
            coefficients = upload(client, 'coefficients.xlsx', change_columns({'N5': '0', 'N6': '/', 'N7': '-2e-3', 'N8': '2', 'N9': None}))
            changed = {p['Code']: p for p in coefficients['configuration']['Catalog']['Parameters']}
            check([changed[k]['Scale'] for k in ['A1','A2','A3','A4','A5']] == [0, 1, -.002, 2, 1], "N-column zero, slash, negative exponent, float scaling and blank are respected")
            assert changed['A4']['Supported'] and changed['A4']['Type'] == 'float32'
            client.call('/api/decoders/apply', {'token': coefficients['token'], 'revision': coefficients['revision']})
            scaled_trial = client.call('/api/decoders/try', {'packetId': 'legacy', 'hex': packet(1).hex()})
            scaled_rows = {r['code']: r for r in scaled_trial['rows']}
            check(scaled_rows['A4']['value'] == '-5' and scaled_rows['A1']['value'] == '0', "float decoding multiplies N coefficient and a zero coefficient produces zero")
            invalid_coefficient = upload(client, 'invalid-coefficient.xlsx', change_columns({'N5': 'not-a-number'}))
            invalid_parameter = next(p for p in invalid_coefficient['configuration']['Catalog']['Parameters'] if p['Code'] == 'A1')
            check(not invalid_parameter['Supported'] and 'N列系数' in invalid_parameter['Diagnostic'], "invalid non-empty N coefficient produces an explicit field diagnostic")
            missing_type=upload(client,'missing-type.xlsx',change_columns(by_sheet={'xl/worksheets/sheet4.xml':{'H83':None}}))
            missing_c79=next(p for p in missing_type['configuration']['Catalog']['Parameters'] if p['Code']=='c79')
            check(not missing_c79['Supported'] and '数据类型' in missing_c79['Diagnostic'],'a missing data type is invalid even when the current source workbook has been completed')
            reserved = upload(client, 'reserved.xlsx', change_columns({'D5': '  预留\t', 'C5': None, 'H5': None, 'N5': 'bad-coefficient', 'D6': '预留1', 'D7': '预留参数'}))
            reserved_source = reserved['configuration']['Catalog']
            reserved_rows = {p['Code']: p for p in reserved_source['Parameters']}
            check(len(reserved_rows) == 708 and 'A1' not in reserved_rows and reserved_rows['A2']['Offset'] == 5, "trimmed exact reserved name skips row before validation and preserves later offsets")
            check(reserved_rows['A2']['Name'] == '预留1' and reserved_rows['A3']['Name'] == '预留参数' and reserved_source['Sheets'][0]['ReservedSkipped'] == 1, "reserved filter is exact match and is counted per worksheet")
            reserved_invalid = upload(client, 'reserved-invalid.xlsx', change_columns(by_sheet={'xl/worksheets/sheet4.xml': {'D83': '预留'}}))
            check(len(reserved_invalid['configuration']['Catalog']['Parameters']) == 708 and not any(i['Code'] == 'c79' for i in reserved_invalid['configuration']['Catalog']['Issues']), "reserved row with missing type is omitted without invalid-field warnings")
            preview = upload(client, workbook.name, original)
            client.call('/api/decoders/apply', {"token": preview["token"], "revision": preview["revision"]})
            current = client.call('/api/decoders')
            check(current["native"] and (directory / 'config' / 'decoder.json').exists(), "preview application persists one versioned decoder.json")
            check(all('::' in i for i in client.call('/api/selection')), "existing display selection migrates to packet-scoped parameters")
            first = current['configuration']['Packets'][0]
            check(first['BitOrder'] == 'msb0' and first['CrossByteOrder'] == 'firstMsb' and current['configuration']['Catalog']['CrossByteOrder'] == 'firstMsb', "Excel import configures highest-first result order for packet mappings")
            first.update(Name='第一包', ExpectedLengthField=1017, ChecksumMode='sum8to16', MappingConfirmed=True, CheckPusHeader=True, CheckFixedFields=True)
            client.call('/api/decoders/packet', {"revision": current['revision'], "packet": first})
            second = copy.deepcopy(first); second.update(Id='packet2', Name='第二包', Apid=9)
            latest = client.call('/api/decoders')
            client.call('/api/decoders/packet', {"revision": latest['revision'], "packet": second})
            check(len(client.call('/api/catalog')) == 642, "two APIDs may bind identical sheets without colliding parameter IDs")
            persisted = client.call('/api/decoders')['configuration']['Packets'][0]
            check('CheckPusHeader' not in persisted and 'CheckFixedFields' not in persisted, "legacy secondary-header check flags are ignored and omitted on save")
            client.call('/api/decoders/packet', {"revision": before['revision'], "packet": first}, expected=409)
            duplicate = copy.deepcopy(first); duplicate['Id'] = 'duplicate'
            latest = client.call('/api/decoders')
            client.call('/api/decoders/packet', {"revision": latest['revision'], "packet": duplicate}, expected=400)
            check(True, "stale edits and overlapping APID routes rejected")
            sample = packet(7, 9); sample[22+261] = 0x01; sample[22+262] = 0x90
            seal(sample)
            trial = client.call('/api/decoders/try', {"packetId": "packet2", "hex": sample.hex(' ')})
            decoded = {r['code']: r for r in trial['rows']}
            check(trial['packet']['identifier'] == '0x0809' and decoded['A281']['raw'] == '25' and decoded['A4']['value'] == '-2.5', "manual SPP hex decodes cross-byte bits from highest to lowest significance")
            check(client.call('/api/state')['received'] == 0, "manual decode does not inject synthetic data into live receiver")
            client.call('/api/decoders/try', {"packetId": "packet2", "hex": '08 ZZ'}, expected=400)
            damaged = bytearray(sample); damaged[18] ^= 1
            client.call('/api/decoders/try', {"packetId": "packet2", "hex": damaged.hex()}, expected=400)
            check(True, "manual decode validates hex and checksum integrity")
            for offset in (12,15,16,17,20,21):
                changed_header = bytearray(sample); changed_header[offset] ^= 0xFF
                seal(changed_header)
                accepted_header = client.call('/api/decoders/try', {"packetId":"packet2", "hex":changed_header.hex()})
                assert accepted_header['packet']['checksum'] == '通过'
            check(True, "PUS flags, packet counter, reserved bits, destination APID and destination address remain informational")
            for offset,field in ((13,'业务类型'),(14,'业务子类型'),(18,'源地址'),(19,'源地址')):
                mismatched=bytearray(sample);mismatched[offset]^=1;seal(mismatched)
                result=client.call('/api/decoders/try',{'packetId':'packet2','hex':mismatched.hex()},expected=400)
                assert field in result['error'],result
            check(True,'manual decoding rejects each routing field mismatch even when Fletcher checksum passes')
            settings = client.call('/api/config'); settings.update(UdpPort=udp_port, BindAddress='127.0.0.1', SourceIPs=['127.0.0.1'])
            client.call('/api/config', settings)
            client.call('/api/selection', {"ids": ['legacy::工仪全程/A1', 'packet2::工仪全程/A1']})
            client.call('/api/control', {"action": 'start'})
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                udp.sendto(packet(1, 8), ('127.0.0.1', udp_port))
                data = packet(1, 9)
                for offset in (12,15,16,17,20,21):data[offset]=0xE0+offset-12
                data[22:27] = (987654).to_bytes(5, 'big'); seal(data)
                udp.sendto(data, ('127.0.0.1', udp_port))
            wait(lambda: client.call('/api/state')['accepted'] == 2, 'both APIDs')
            values = {r['id']: r for r in client.call('/api/state')['rows']}
            check(values['legacy::工仪全程/A1']['value'] == '12.3456' and values['packet2::工仪全程/A1']['value'] == '98.7654', "real UDP routes by manual SPP definitions and preserves values per packet")
            latest = client.call('/api/decoders')
            client.call('/api/decoders/packet', {"revision": latest['revision'], "packet": first}, expected=409)
            check(True, "active receive forbids mapping changes")
            client.call('/api/control', {"action": 'stop'})
            load_replay(client,replay_fixture(apids=(8,9)))
            client.call('/api/control', {"action": 'demo'})
            wait(lambda: client.call('/api/state')['accepted'] >= 4, 'multi-packet demo')
            check(client.call('/api/state')['mode'] == 'demo', "demo supports multiple configured SPP packets")
            client.call('/api/control', {"action": 'stop'})
            exported = client.call('/api/decoders/export')
            roundtrip = upload(client, 'decoder.json', json.dumps(exported, ensure_ascii=False).encode('utf-8'))
            check(roundtrip['configuration']['Packets'][1]['Id'] == 'packet2', "exported JSON restores packet bindings and import provenance")
            upload(client, 'bad.xlsx', b'not a zip', expected=400)
            upload(client, 'bad.json', b'{"SchemaVersion":999}', expected=400)
            check(True, "malformed XLSX and unknown config versions rejected")
            # Rename sheet inside a memory-only fixture: importer must not depend on '工仪' names.
            memory = io.BytesIO()
            with zipfile.ZipFile(io.BytesIO(original)) as z, zipfile.ZipFile(memory, 'w', zipfile.ZIP_DEFLATED) as out:
                for name in z.namelist():
                    data = z.read(name)
                    if name == 'xl/workbook.xml': data = data.replace('工仪全程'.encode(), '自定义遥测'.encode())
                    out.writestr(name, data)
            renamed = upload(client, 'renamed.xlsx', memory.getvalue())
            check(renamed['configuration']['Catalog']['Sheets'][0]['Name'] == '自定义遥测' and len(renamed['configuration']['Catalog']['Parameters']) == 709 and not renamed['configuration']['Packets'][0]['Enabled'], "arbitrary sheet names read; missing old bindings explicitly disabled in preview")
            process.terminate(); process.wait(timeout=5)
            # Simulate a deployed older configuration without changing packet bindings.
            old_path=directory/'config/decoder.json'; old_config=json.loads(old_path.read_text(encoding='utf-8-sig'))
            old_bindings=[(p['Id'],p['Groups'],p['Enabled'],p['MappingConfirmed']) for p in old_config['Packets']]
            for index,p in enumerate(old_config['Packets']):p.update(ChecksumMode=('sum16be' if index==0 else 'unverified'),ChecksumStart=16)
            old_path.write_text(json.dumps(old_config,ensure_ascii=False),encoding='utf-8')
            process = subprocess.Popen([str(ROOT / "TelemetryMonitor.exe"), "--local", "--root", str(directory), "--web-port", str(web_port)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            fresh = Client(web_port)
            def reopened():
                try: return not fresh.call('/api/auth/me')['setupRequired']
                except OSError: return False
            wait(reopened, 'restart')
            fresh.csrf = fresh.call('/api/auth/login', {"username": 'decoder_admin', "password": password})['csrf']
            check(len(fresh.call('/api/decoders')['configuration']['Packets']) == 2 and len(fresh.call('/api/catalog')) == 642, "native decoder configuration survives restart")
            migrated=json.loads(old_path.read_text(encoding='utf-8-sig'))
            check(all(p['ChecksumMode']=='fletcher16' and p['ChecksumStart']==0 for p in migrated['Packets']) and
                  [(p['Id'],p['Groups'],p['Enabled'],p['MappingConfirmed']) for p in migrated['Packets']]==old_bindings and
                  migrated['Revision']!=old_config['Revision'] and old_path.with_suffix('.json.bak').exists(),
                  'startup migrates every legacy packet to fixed Fletcher-16, persists a new revision and backup, and preserves mappings and enable state')
            check(workbook.read_bytes() == original, "source Excel left unchanged")
            print(f'All {len(checks)} decoder checks passed.')
        finally:
            if process.poll() is None: process.terminate()
            process.communicate(timeout=5)


if __name__ == '__main__':
    main()
