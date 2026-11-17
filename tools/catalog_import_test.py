"""Exercise Excel replacement/addition through the real authenticated HTTP API."""
import io
import json
import secrets
import shutil
import socket
import subprocess
import tempfile
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape
from integration_test import Client, ROOT, free_port, packet, seal, wait, load_replay, seed_send_monitor
from decoder_integration_test import upload


def workbook(tables):
    ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
    rel = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
    sheets = [('说明', [])] + tables
    headers = ['序号', '位置', '参数代号', '参数名称', '起始字节', '起始位', '字节个数', '数据类型', '单位', '物理意义', '缺省显示类型', '精度', '公式', '系数']
    def row(number, values):
        return '<row r="%d">%s</row>' % (number, ''.join('<c r="%s%d" t="inlineStr"><is><t>%s</t></is></c>' % (chr(65+i), number, escape(str(v))) for i, v in enumerate(values)))
    memory = io.BytesIO()
    with zipfile.ZipFile(memory, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('xl/workbook.xml', '<workbook xmlns="%s" xmlns:r="%s"><sheets>%s</sheets></workbook>' % (ns, rel, ''.join('<sheet name="%s" sheetId="%d" r:id="r%d"/>' % (escape(name, {'"':'&quot;'}), i, i) for i, (name, _) in enumerate(sheets, 1))))
        z.writestr('xl/_rels/workbook.xml.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">%s</Relationships>' % ''.join('<Relationship Id="r%d" Type="%s/worksheet" Target="worksheets/sheet%d.xml"/>' % (i, rel, i) for i in range(1, len(sheets)+1)))
        z.writestr('_rels/.rels', '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="root" Type="%s/officeDocument" Target="xl/workbook.xml"/></Relationships>' % rel)
        z.writestr('[Content_Types].xml', '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>%s</Types>' % ''.join('<Override PartName="/xl/worksheets/sheet%d.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' % i for i in range(1, len(sheets)+1)))
        for i, (_, parameters) in enumerate(sheets, 1):
            rows = row(1, headers)
            for j, p in enumerate(parameters, 2):
                code, name, offset, length, kind, scale = p
                rows += row(j, [j-1, '', code, name, offset, '', length, kind, '', '', '十进制', 6, '', scale])
            z.writestr('xl/worksheets/sheet%d.xml' % i, '<worksheet xmlns="%s"><sheetData>%s</sheetData></worksheet>' % (ns, rows))
    return memory.getvalue()


def main():
    checks = []
    def check(condition, label):
        assert condition, label
        checks.append(label); print('PASS', label, flush=True)
    a1 = ('A1', '时间', 0, 5, '无符号整数', .0001)
    a4 = ('A4', '浮点量', 13, 4, '单精度浮点', 1)
    new = ('NEW', '新状态', 5, 1, '无符号整数', 1)
    other = ('B1', '另一表状态', 6, 1, '无符号整数', 1)
    first = workbook([('主表', [a1, a4])])
    addition = workbook([('新表', [other]), ('主表', [new, a1])])
    with tempfile.TemporaryDirectory(prefix='telemetry-import-') as scratch:
        folder = Path(scratch); shutil.copytree(ROOT/'config', folder/'config'); seed_send_monitor(folder); shutil.copytree(ROOT/'web', folder/'web'); (folder/'config/decoder.json').unlink(missing_ok=True)
        port = free_port(); process = subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'), '--local', '--root', str(folder), '--web-port', str(port)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        try:
            client = Client(port)
            def ready():
                try: return client.call('/api/auth/me')['setupRequired']
                except OSError: return False
            wait(ready, 'import test server')
            password = secrets.token_urlsafe(18); client.call('/api/auth/setup', dict(username='import_qa', name='导入测试', password=password)); client.csrf = client.call('/api/auth/login', dict(username='import_qa', password=password))['csrf']
            initial = client.call('/api/decoders')
            preview = upload(client, 'first.xlsx', first)
            check(all(p['Reference']=='TM.'+p['Code'] and p['ValueReference']==p['Reference']+'.value' for p in preview['configuration']['Catalog']['Parameters']), 'Excel preview generates TM object and engineering-value references')
            unchanged = client.call('/api/decoders')
            check(unchanged['native']==initial['native'] and unchanged['revision']==initial['revision'] and unchanged['configuration']['Catalog']['Parameters']==initial['configuration']['Catalog']['Parameters'] and unchanged['configuration']['Packets']==initial['configuration']['Packets'] and not (folder/'config/decoder.json').exists(), 'reading both import choices never changes active configuration')
            client.call('/api/decoders/apply', dict(token=preview['token'], revision=preview['revision'], mode='replace'))
            current = client.call('/api/decoders'); definition = current['configuration']['Packets'][0]
            check(len(current['configuration']['Catalog']['Parameters']) == 2 and len(current['configuration']['Packets']) == len(initial['configuration']['Packets']) and not definition['Enabled'], 'replace removes old parameter tables but retains SPP definitions and disables missing bindings')
            definition.update(Groups=['主表'], Enabled=True, MappingConfirmed=True)
            client.call('/api/decoders/packet', dict(revision=current['revision'], packet=definition))
            client.call('/api/selection', dict(ids=['legacy::主表/A1','legacy::主表/A4']))
            before = client.call('/api/decoders'); saved = folder/'config/decoder.json'; original_bytes = saved.read_bytes()
            preview = upload(client, 'addition.xlsx', addition); info = preview['addition']
            check(info['CanApply'] and info['AddedParameters'] == 2 and info['DuplicateParameters'] == 1 and info['ConflictCount'] == 0 and info['AddedSheets'] == ['新表'] and info['TotalParameters'] == 4, 'addition preview reports new sheets, new parameters, skipped duplicates and merged count')
            check(saved.read_bytes() == original_bytes and client.call('/api/decoders') == before, 'merged preview remains read-only and does not replace existing tables')
            merged = client.call('/api/decoders/preview/export?token='+preview['token']+'&mode=add')
            replacement = client.call('/api/decoders/preview/export?token='+preview['token']+'&mode=replace')
            check(len(merged['Catalog']['Parameters']) == 4 and len(replacement['Catalog']['Parameters']) == 3 and merged['Packets'] == before['configuration']['Packets'], 'both JSON downloads show their distinct final configurations with existing SPP bindings preserved for addition')
            client.call('/api/decoders/apply', dict(token=preview['token'], revision=preview['revision'], mode='invalid'), expected=400)
            client.call('/api/decoders/apply', dict(token=preview['token'], revision=preview['revision'], mode='add'), csrf=False, expected=403)
            Client(port).call('/api/decoders/preview/export?token='+preview['token']+'&mode=add', expected=401)
            check(saved.read_bytes() == original_bytes, 'invalid modes and unauthenticated or CSRF-free requests cannot mutate the configuration')
            client.call('/api/decoders/apply', dict(token=preview['token'], revision=preview['revision'], mode='add'))
            current = client.call('/api/decoders'); catalog = current['configuration']['Catalog']
            check(all(p['ValueReference']=='TM.'+p['Code']+'.value' for p in catalog['Parameters']), 'addition preserves reference generation for both retained and new parameters')
            check(len(catalog['Parameters']) == 4 and current['configuration']['Packets'] == before['configuration']['Packets'] and client.call('/api/selection') == ['legacy::主表/A1','legacy::主表/A4'], 'addition saves the union while preserving packet settings, enabled state, confirmation and user selection')
            check(catalog['SourceFiles'] == ['first.xlsx', 'addition.xlsx'] and {s['Name']:s['Parameters'] for s in catalog['Sheets']} == {'主表':3,'新表':1} and saved.with_suffix('.json.bak').read_bytes() == original_bytes, 'merged file metadata and sheet counts are saved with an atomic backup of the previous configuration')
            client.call('/api/selection', dict(ids=['legacy::主表/A1','legacy::主表/NEW']))
            udp_port = free_port(socket.SOCK_DGRAM); settings = client.call('/api/config'); settings.update(UdpPort=udp_port, SourceIPs=['127.0.0.1'], BindAddress='127.0.0.1'); client.call('/api/config', settings)
            client.call('/api/control', dict(action='start'))
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as sender: sender.sendto(packet(1),('127.0.0.1',udp_port))
            wait(lambda:client.call('/api/state')['accepted']==1, 'merged table UDP decoding')
            rows = {r['id']:r for r in client.call('/api/state')['rows']}
            check(rows['legacy::主表/A1']['value']=='12.3456' and rows['legacy::主表/NEW']['value']=='2', 'a newly added parameter in a bound table decodes alongside the retained parameter from real UDP bytes')
            client.call('/api/control', dict(action='stop'))
            duplicate = upload(client, 'addition.xlsx', addition); stable = saved.read_bytes()
            check(not duplicate['addition']['CanApply'] and duplicate['addition']['DuplicateParameters']==3 and duplicate['addition']['AddedParameters']==0, 're-importing an identical workbook is idempotent and cannot create duplicate definitions')
            client.call('/api/decoders/apply', dict(token=duplicate['token'],revision=duplicate['revision'],mode='add'),expected=400)
            check(saved.read_bytes()==stable, 'a duplicate-only addition leaves the saved revision and backup unchanged')
            conflict = upload(client, 'conflict.xlsx', workbook([('主表', [('A1','时间',0,5,'无符号整数',2)]), ('其他表',[other])]))
            check(conflict['addition']['ConflictCount']==1 and not conflict['addition']['CanApply'] and 'A1' in conflict['addition']['Conflicts'][0], 'different definitions for the same table and code are shown as explicit conflicts')
            client.call('/api/decoders/apply',dict(token=conflict['token'],revision=conflict['revision'],mode='add'),expected=400)
            client.call('/api/decoders/preview/export?token='+conflict['token']+'&mode=add',expected=400)
            check(saved.read_bytes()==stable, 'conflicting addition is atomic and cannot partially add the non-conflicting new table')
            client.call('/api/decoders/apply',dict(token=conflict['token'],revision=conflict['revision'],mode='replace'))
            replaced=client.call('/api/decoders')
            check(len(replaced['configuration']['Catalog']['Parameters'])==2 and replaced['configuration']['Catalog']['Parameters'][0]['Scale']==2 and replaced['configuration']['Packets'][0]['Enabled'], 'replacement explicitly updates conflicting parameter definitions and removes parameters absent from the new workbook')
            stale=upload(client,'stale.xlsx',workbook([('额外表',[new])]))
            changed_packet=replaced['configuration']['Packets'][0];changed_packet['Name']='另一次修改'
            client.call('/api/decoders/packet',dict(revision=replaced['revision'],packet=changed_packet));stable=saved.read_bytes()
            client.call('/api/decoders/apply',dict(token=stale['token'],revision=stale['revision'],mode='add'),expected=409)
            check(saved.read_bytes()==stable, 'stale addition preview cannot overwrite configuration saved after the preview was created')
            locked=upload(client,'running.xlsx',workbook([('额外表',[new])]))
            load_replay(client);client.call('/api/control',dict(action='demo'))
            client.call('/api/decoders/apply',dict(token=locked['token'],revision=locked['revision'],mode='add'),expected=409)
            client.call('/api/control',dict(action='stop'))
            client.call('/api/decoders/apply',dict(token=locked['token'],revision=locked['revision'],mode='add'))
            check(len(client.call('/api/decoders')['configuration']['Catalog']['Parameters'])==3, 'addition is blocked during reception and the same preview can be applied after stopping')
            complete=client.call('/api/decoders')['configuration'];json_preview=upload(client,'complete.json',json.dumps(complete).encode())
            client.call('/api/decoders/apply',dict(token=json_preview['token'],revision=json_preview['revision'],mode='add'),expected=400)
            check(not json_preview['addition']['CanApply'] and 'JSON' in json_preview['addition']['Errors'][0], 'complete JSON imports explicitly retain replacement-only semantics instead of silently dropping packet definitions')
            expected=client.call('/api/decoders')['configuration'];process.terminate();process.wait(timeout=6)
            process=subprocess.Popen([str(ROOT/'TelemetryMonitor.exe'),'--local','--root',str(folder),'--web-port',str(port)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
            fresh=Client(port)
            def reopened():
                try:return not fresh.call('/api/auth/me')['setupRequired']
                except OSError:return False
            wait(reopened,'restart');fresh.csrf=fresh.call('/api/auth/login',dict(username='import_qa',password=password))['csrf']
            check(fresh.call('/api/decoders')['configuration']==expected,'merged parameters, source files and retained SPP mappings survive server restart')
            duplicates=upload(fresh,'duplicate-codes.xlsx',workbook([('one',[('a1','one',0,1,'无符号整数',1)]),('two',[('a1','two',1,1,'无符号整数',1)])]))
            check(len(duplicates['configuration']['Catalog']['Parameters'])==2 and len(duplicates['configuration']['Catalog']['ReferenceIssues'])==2, 'cross-sheet duplicate codes are retained with explicit TM reference warnings')
        finally:
            if process.poll() is None:process.terminate();process.wait(timeout=6)
    print('All',len(checks),'catalog import checks passed.')


if __name__ == '__main__': main()
