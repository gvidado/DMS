"""Focused import checks in an isolated server; never starts listening or sends UDP."""
import copy, json, secrets, shutil, subprocess, sys, tempfile
from pathlib import Path
from integration_test import ROOT, Client, free_port, wait
from catalog_import_test import workbook
from decoder_integration_test import upload


def main():
    exe = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / 'TelemetryMonitor.exe'
    checks = []
    def check(ok, label):
        assert ok, label
        checks.append(label)
        print('PASS', label, flush=True)
    field = lambda code, name, offset=0, scale=1: (code, name, offset, 1, '无符号整数', scale)
    first = workbook([('主表', [field('A1', '原参数'), field('A2', '将移除', 1)]), ('保留表', [field('B1', '不变', 2)])])
    updated = workbook([('主表', [field('A1', '更新名称', 0, 3), field('A3', '新增参数', 3)]), ('新增表', [field('C1', '应跳过', 4)])])
    with tempfile.TemporaryDirectory(prefix='matching-import-') as scratch:
        folder = Path(scratch)
        shutil.copytree(ROOT/'config', folder/'config')
        for name in ['decoder.json', 'continuous-rules.json', 'tc-bindings.json', 'sequences.json', 'observed-commands.json']:
            (folder/'config'/name).unlink(missing_ok=True)
        port = free_port()
        process = subprocess.Popen([str(exe), '--local', '--root', str(folder), '--web-port', str(port)], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        try:
            c = Client(port)
            def ready():
                try: c.call('/api/auth/me'); return True
                except OSError: return False
            wait(ready, 'matching import server')
            password = secrets.token_urlsafe(18)
            c.call('/api/auth/setup', dict(username='matchingqa', name='Import QA', password=password))
            c.csrf = c.call('/api/auth/login', dict(username='matchingqa', password=password))['csrf']
            def apply(p, mode='replace', expected=200):
                return c.call('/api/decoders/apply', dict(token=p['token'], revision=p['revision'], mode=mode), expected=expected)
            apply(upload(c, 'first.xlsx', first))
            config = c.call('/api/decoders')['configuration']
            config['Catalog']['Parameters'][0].update(Id='stable-custom-id', SourceId='stable-custom-id')
            packet = config['Packets'][0]
            packet.update(Groups=['主表'], Enabled=True, MappingConfirmed=True)
            config['Catalog']['Issues'] = [dict(Level='warning', Sheet='主表', Row=2, Code='A1', Message='旧提示'), dict(Level='warning', Sheet='保留表', Row=2, Code='B1', Message='保留提示')]
            apply(upload(c, 'stable.json', json.dumps(config, ensure_ascii=False).encode()))
            rules = c.call('/api/continuous-rules')
            rule = next(copy.deepcopy(row['rule']) for row in rules['rows'] if row['rule']['ParameterId']=='legacy::stable-custom-id')
            rule.update(Script='RANGE=[0 20];', Name='自定义规则')
            c.call('/api/continuous-rules/save', dict(rule=rule, revision=rules['revision']))
            c.call('/api/selection', dict(ids=['legacy::stable-custom-id']))
            current = c.call('/api/decoders')['configuration']
            retained = next(p for p in current['Catalog']['Parameters'] if p['Group']=='保留表')
            def snapshot():
                return {name: (folder/'config'/name).read_bytes() for name in ['decoder.json', 'continuous-rules.json']}
            before = snapshot()
            p = upload(c, 'updated.xlsx', updated)
            info = p['matching']
            check(info['CanApply'] and info['ReplacedSheets']==['主表'] and info['RetainedSheets']==['保留表'] and info['SkippedSheets']==['新增表'] and info['TotalParameters']==3, 'preview lists exactly matched, retained and skipped sheets')
            check(not p['addition']['CanApply'] and p['matching']['CanApply'], 'conflicting add does not prevent explicit matching replacement')
            merged = c.call('/api/decoders/preview/export?mode=replace-matching&token='+p['token'])
            parameters = merged['Catalog']['Parameters']
            check(next(x for x in parameters if x['Code']=='A1')['Id']=='stable-custom-id' and next(x for x in parameters if x['Code']=='A1')['Scale']==3, 'same-code parameters update their definition and retain custom stable IDs')
            check(next(x for x in parameters if x['Code']=='B1')==retained and {x['Code'] for x in parameters}=={'A1','A3','B1'} and merged['Packets']==current['Packets'], 'whole matched table updates while unrelated definitions and SPP associations remain exact')
            check([x['Message'] for x in merged['Catalog']['Issues']]==['保留提示'] and snapshot()==before, 'preview removes outdated matched diagnostics and never writes active files')
            c.call('/api/decoders/preview/cancel', dict(token=p['token']), csrf=False, expected=403)
            c.call('/api/decoders/preview/export?mode=replace-matching&token='+p['token'])
            c.call('/api/decoders/preview/cancel', dict(token=p['token']))
            c.call('/api/decoders/preview/cancel', dict(token=p['token']))
            apply(p, 'replace-matching', 400)
            c.call('/api/decoders/preview/export?token='+p['token'], expected=400)
            check(snapshot()==before, 'cancel is CSRF-protected and idempotent, invalidates apply/export and changes no configuration')
            p = upload(c, 'updated.xlsx', updated)
            changed = copy.deepcopy(current['Packets'][0]); changed['Name']='新的包名称'
            c.call('/api/decoders/packet', dict(packet=changed, revision=p['revision']))
            before = snapshot(); apply(p, 'replace-matching', 409)
            check(snapshot()==before, 'a stale matching preview cannot overwrite concurrent changes')
            p = upload(c, 'updated.xlsx', updated); apply(p, 'replace-matching')
            saved = c.call('/api/decoders')['configuration']
            check({x['Code'] for x in saved['Catalog']['Parameters']}=={'A1','A3','B1'} and saved['Packets'][0]==changed, 'matching apply persists only the chosen tables and preserves current packet settings')
            rows = c.call('/api/continuous-rules')['rows']
            kept = next(x['rule'] for x in rows if x['rule']['Id']==rule['Id'])
            new = next(x['rule'] for x in rows if x['rule']['Code']=='A3')
            check(kept['Script']==rule['Script'] and kept['ParameterId']==rule['ParameterId'] and new['Level']=='一般' and not new['Enabled'] and c.call('/api/selection')==['legacy::stable-custom-id'], 'existing custom rules and selection stay bound; new parameters receive disabled default rules')
            before = snapshot(); p = upload(c, 'no-match.xlsx', workbook([('其他表', [field('X1', '其他')])]))
            check(not p['matching']['CanApply'] and p['matching']['ReplacedSheets']==[], 'no matching sheets disables replacement')
            apply(p, 'replace-matching', 400)
            check(snapshot()==before, 'no-match apply is rejected without changing revision or rules')
            p = upload(c, 'same.json', json.dumps(saved, ensure_ascii=False).encode())
            apply(p, 'replace-matching', 400)
            check(not p['matching']['CanApply'], 'full JSON remains replacement-only')
            p = upload(c, 'empty-table.xlsx', workbook([('主表', []), ('其他表', [field('X1', '其他')])]))
            apply(p, 'replace-matching')
            empty = c.call('/api/decoders')['configuration']
            check([x['Code'] for x in empty['Catalog']['Parameters']]==['B1'] and not empty['Packets'][0]['Enabled'], 'a matched empty table removes its parameters, retains other tables, and disables its dependent packet')
        finally:
            process.terminate(); process.wait(timeout=6)
    print('All', len(checks), 'matching import checks passed.')


if __name__=='__main__': main()
