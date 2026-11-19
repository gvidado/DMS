"""Test flight judgment conversion and standard Excel round-trip on an isolated server."""
import argparse
import io
import json
import os
import secrets
import shutil
import socket
import subprocess
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

from integration_test import Client, ROOT, free_port, wait
from runtime_benchmark import seed
from sequence_test import HEADERS, xlsx

FLIGHT_HEADERS = ['飞行阶段', '飞行动作', '参数代号', '参考值', '范围值', '实际值', '关键字']
NS = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'


def workbook():
    rows = [
        ['第一阶段', '组合动作', 'TME.A0', '9.8000000000000007', '1', '忽略此列'],
        [None, None, 'A0', '9.8', None],
        [None, '上界判读', 'A2', '＞10', '/'],
        [None, '零范围', 'TMB.A0', '0x020202', '0'],
        [None, '负参考值', 'TME.A0', '-90', '5'],
        [None, None, 'TME.A2', '小于 -1', None],
        ['下一阶段', '说明', None, None, None, None, 'TEXT']
    ]
    raw = xlsx([('飞行试验', rows), ('复核', [['单独阶段', '动作', 'TME.A2.value', '<=1']]), ('空白', [])])
    with zipfile.ZipFile(io.BytesIO(raw)) as original:
        output = io.BytesIO()
        with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as saved:
            for item in original.namelist():
                value = original.read(item)
                if item.startswith('xl/worksheets/'):
                    for old, new in zip(HEADERS, FLIGHT_HEADERS):
                        value = value.replace(('>' + old + '<').encode(), ('>' + new + '<').encode())
                if item.endswith('/sheet1.xml'):
                    value = value.replace(b'<c r="D2" t="inlineStr"><is><t>9.8000000000000007</t></is></c>',
                                          b'<c r="D2"><v>9.8000000000000007</v></c>')
                    value = value.replace(b'</worksheet>',
                                          b'<mergeCells count="2"><mergeCell ref="A2:A6"/><mergeCell ref="B2:B3"/></mergeCells></worksheet>')
                saved.writestr(item, value)
    return output.getvalue()


def post(client, path, body, content='application/json', status=200):
    if isinstance(body, dict):
        body = json.dumps(body, ensure_ascii=False).encode()
    request = urllib.request.Request(client.base + path, data=body,
                                     headers={'Content-Type': content, 'X-CSRF-Token': client.csrf})
    try:
        response = client.opener.open(request, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    payload = response.read()
    assert response.status == status, (path, response.status, payload[:500])
    return payload if payload.startswith(b'PK') else json.loads(payload)


def fixture(root):
    seed(root, per_packet=2, packets=2)
    path = root / 'config' / 'decoder.json'
    decoder = json.loads(path.read_text('utf-8'))
    for packet in decoder['Packets']:
        packet['OuterSourceId'] = 0x0802
    packet = dict(decoder['Packets'][0], Id='backup-p0', Name='备份流', OuterSourceId=0x0202)
    decoder['Packets'].append(packet)
    path.write_text(json.dumps(decoder, ensure_ascii=False), encoding='utf-8')
    path = root / 'config' / 'server.json'
    settings = json.loads(path.read_text('utf-8'))
    settings['UdpPort'] = free_port(socket.SOCK_DGRAM)
    path.write_text(json.dumps(settings), encoding='utf-8')
    shutil.copytree(ROOT / 'web', root / 'web')


def check_conversion(client, source):
    prefix = '/api/sequence/flight/'
    url = prefix + 'preview?filename=' + urllib.parse.quote(source.name)
    data = source.read_bytes()
    before = client.call('/api/sequence/config')['revision']
    preview = post(client, url, data, 'application/octet-stream')
    token = preview['token']
    first, second = preview['sheets']
    assert first['Name'] == '飞行试验' and not second['Errors']
    assert second['Rows'][0]['Condition'] == 'TME.A2 <= 1'
    r2, r3, r4, r5, r6, r7, r8 = first['Rows']
    assert r2['Input']['Reference'] == '9.8'
    assert r2['Description'] == '第一阶段：组合动作（测试量0）'
    assert r2['Condition'] == 'abs(TME.A0 - 9.8) < 1'
    assert r3['Description'] == '第一阶段：组合动作（A0）'
    assert set(r3['Candidates']) == {'TME.A0', 'TMB.A0'}
    assert '不能为空' in r3['Errors']['范围值'] and '参数代号' in r3['Errors']
    assert r4['Condition'] == 'TME.A2 > 10'
    assert r5['Condition'] == 'TMB.A0 == 0x020202'
    assert r6['Condition'] == 'abs(TME.A0 - (-90)) < 5'
    assert r7['Input']['Phase'] == '' and r7['Condition'] == 'TME.A2 < -1'
    assert r8['Input']['Keyword'] == 'TEXT' and r8['Timeout'] == ''
    assert r2['Timeout'] == r4['Timeout'] == r5['Timeout'] == second['Rows'][0]['Timeout'] == '1500'
    assert first['Errors'][0].startswith('第3行')
    post(client, prefix + 'export', {'token': token}, status=400)

    def recheck(**fields):
        edits = [dict(r3['Input'], **fields)]
        return post(client, prefix + 'recheck', dict(token=token, sheetId=first['Id'], edits=edits))['sheet']
    assert '范围值' in recheck(Parameter='TMB.A0', Range='-1')['Rows'][1]['Errors']
    checked = recheck(Parameter='ＴＭＢ。Ａ０', Range='0')
    assert not checked['Errors'], checked['Errors']
    assert checked['Rows'][1]['Condition'] == 'TMB.A0 == 9.8' and checked['Rows'][1]['Timeout'] == '1500'
    export = post(client, prefix + 'export', {'token': token})
    with zipfile.ZipFile(io.BytesIO(export)) as saved:
        xml = ET.fromstring(saved.read('xl/worksheets/sheet1.xml'))
        header, first_step = xml.findall(f'.//{NS}row')[:2]
        assert [c.text for c in header.iter(NS + 't')] == HEADERS[:2] + ['指令或参数'] + HEADERS[3:]
        columns = {'B2': '第一阶段：组合动作（测试量0）', 'D2': 'abs(TME.A0 - 9.8) < 1', 'G2': '1500'}
        assert all((c.findtext(f'{NS}is/{NS}t') or '') == columns.get(c.attrib['r'], '') for c in first_step)
    round_trip = post(client, '/api/sequence/preview?filename=converted.xlsx&mode=judge&timeout=30',
                      export, 'application/octet-stream')
    assert len(round_trip['definitions']) == 2
    assert all(not definition['errors'] for definition in round_trip['definitions']), round_trip
    rows = [row for item in round_trip['definitions'] for row in item['definition']['Rows']]
    assert all(row['Timeout'] == ('1500' if row['Condition'] else '') for row in rows)
    assert client.call('/api/sequence/config')['revision'] == before and source.read_bytes() == data
    client.call(prefix + 'cancel', {'token': token})
    post(client, prefix + 'export', {'token': token}, status=400)
    print('PASS merged-cell scope, parameter resolution, numeric rules, correction, Excel round-trip and cancellation')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--browser', action='store_true')
    parser.add_argument('--sample', type=Path, help='Optional original workbook for a read-only structure check')
    args = parser.parse_args()
    executable = args.executable.resolve()
    with tempfile.TemporaryDirectory(prefix='flight-sequence-', dir=ROOT / 'test-output') as temp:
        root = Path(temp).resolve()
        fixture(root)
        source = root / '飞行测试.xlsx'
        source.write_bytes(workbook())
        client = Client(free_port())
        process = subprocess.Popen([str(executable), '--local', '--root', str(root), '--web-port',
                                    client.base.rsplit(':', 1)[1]], stdout=subprocess.DEVNULL,
                                   stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)

        def ready():
            try:
                client.call('/api/auth/me')
                return True
            except OSError:
                return False

        try:
            wait(ready, 'flight conversion server')
            password = secrets.token_urlsafe(18)
            client.call('/api/auth/setup', dict(username='flightqa', name='飞行表测试', password=password))
            client.csrf = client.call('/api/auth/login', dict(username='flightqa', password=password))['csrf']
            check_conversion(client, source)
            if args.sample:
                sample = args.sample.read_bytes()
                result = post(client, '/api/sequence/flight/preview?filename=sample.xlsx',
                              sample, 'application/octet-stream')
                assert len(result['sheets']) == 1 and len(result['sheets'][0]['Rows']) == 16
                rows = result['sheets'][0]['Rows']
                assert rows[0]['Input']['Phase'] == rows[5]['Input']['Phase'] and rows[0]['Input']['Phase']
                assert rows[3]['Input']['Action'] == rows[5]['Input']['Action'] and rows[3]['Input']['Action']
                assert args.sample.read_bytes() == sample
                print('PASS supplied workbook, 16 rows, combined headers read as provided')
            if args.browser:
                settings = root / 'browser-auth.json'
                settings.write_text(json.dumps(dict(base=client.base, password=password, source=str(source),
                                                     out=str(executable.parent))), encoding='utf-8')
                subprocess.run([os.environ.get('DMS_BROWSER_NODE', 'node'),
                                str(ROOT / 'tools' / 'flight_sequence_browser.mjs'), str(settings)],
                               check=True, timeout=65)
        finally:
            process.terminate()
            process.wait(10)


if __name__ == '__main__':
    main()
