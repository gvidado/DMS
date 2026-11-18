"""Verify group persistence, browser saves and metadata-only hot application on loopback."""
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
from pathlib import Path

from integration_test import Client, ROOT, free_port, wait
from runtime_benchmark import seed
from sequence_test import xlsx


def main():
    host = Path(sys.argv[1]).resolve()
    old_id = 'a' * 32
    with tempfile.TemporaryDirectory(dir=ROOT / 'test-output', prefix='sequence-groups-') as scratch:
        root = Path(scratch)
        seed(root, per_packet=2, packets=1)
        shutil.copytree(ROOT / 'web', root / 'web')
        settings_path = root / 'config/server.json'
        settings = json.loads(settings_path.read_text(encoding='utf-8-sig'))
        settings['UdpPort'] = free_port(socket.SOCK_DGRAM)
        settings_path.write_text(json.dumps(settings), encoding='utf-8')
        legacy = dict(Id=old_id, Code='XLP0001', Name='旧版序列', Mode='judge',
                      Rows=[dict(Row=2, Keyword='TEXT', Description='等待时间', Expected='F:10000')])
        config_path = root / 'config/sequences.json'
        config_path.write_text(json.dumps(dict(Revision='legacy', NextJudgeNumber=2, Sequences=[legacy])), encoding='utf-8')
        workbook = root / '分组.xlsx'
        workbook.write_bytes(xlsx([(sheet, [['TEXT', sheet, '', '', '', '', '']]) for sheet in ('甲', '乙', '丙')]))
        broken = root / '无法读取.xlsx'
        broken.write_bytes(b'not an xlsx')
        first_batch = root / '批量甲.xlsx'
        first_batch.write_bytes(xlsx([('同名页', [['TEXT', '第一份工作簿', '', '', '', '', '']])]))
        second_batch = root / '批量乙.xlsx'
        second_batch.write_bytes(xlsx([('需修正', [['UNKNOWN', '第二份工作簿', '', '', '', '', '']]),
                                       ('同名页', [['TEXT', '取消勾选的工作表', '', '', '', '', '']])]))
        conversion_file = root / '指令转换.xlsx'
        conversion_file.write_bytes(xlsx([('待核对', [['', '缺少注入指令', 'YKM9999', '', '', '', '']])]))
        password = secrets.token_urlsafe(20)
        process = None

        def start(first=False):
            nonlocal process
            port = free_port()
            client = Client(port)
            process = subprocess.Popen([str(host), str(root), str(port)], stdin=subprocess.PIPE,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, text=True,
                                       creationflags=subprocess.CREATE_NO_WINDOW)

            def ready():
                try:
                    return client.call('/api/auth/me')
                except OSError:
                    return False

            wait(ready, 'sequence groups host')
            if first:
                client.call('/api/auth/setup', dict(username='groupqa', name='分组检查', password=password))
            client.csrf = client.call('/api/auth/login', dict(username='groupqa', password=password))['csrf']
            return client

        def stop():
            if process and process.poll() is None:
                process.stdin.write('quit\n')
                process.stdin.flush()
                process.wait(timeout=10)

        try:
            client = start(first=True)
            info = client.call('/api/sequence/config')
            assert info['definitions'][0]['Group'] == '' and info['definitions'][0]['Code'] == 'XLP0001'
            other = Client(port=int(client.base.rsplit(':',1)[1]))
            other.csrf = other.call('/api/auth/login', dict(username='groupqa', password=password))['csrf']
            request = dict(id=old_id, revision=info['revision'])
            first = client.call('/api/sequence/edit-preview', request)
            second = other.call('/api/sequence/edit-preview', request)
            other.call('/api/sequence/preview/cancel', dict(token=first['token']))
            definition = first['definitions'][0]['definition']
            checked = client.call('/api/sequence/recheck?scope=sheet', dict(token=first['token'], id=old_id, rows=definition['Rows']))
            assert len(checked['definitions']) == 1
            client.call('/api/sequence/save-edits', dict(token=checked['token'], group=''))
            conflict = other.call('/api/sequence/save-edits', dict(token=second['token'], group='不应覆盖'), expected=409)
            assert '预览仍保留' in conflict['error']
            other.call('/api/sequence/recheck?scope=sheet', dict(token=second['token'], id=old_id, rows=definition['Rows']))
            other.call('/api/sequence/preview/cancel', dict(token=second['token']))
            print('PASS concurrent shared-account sessions keep separate previews and conflicting saves preserve the draft')
            auth = root / 'browser-input.json'
            auth.write_text(json.dumps(dict(base=client.base, password=password, workbook=str(workbook), oldId=old_id,
                                            batch=[str(broken), str(first_batch), str(second_batch)], conversion=str(conversion_file))), encoding='utf-8')
            browser = subprocess.run(
                [os.environ.get('DMS_BROWSER_NODE', 'node'), str(ROOT / 'tools/sequence_groups_browser.mjs'), str(auth)],
                capture_output=True, encoding='utf-8', timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
            assert browser.returncode == 0, browser.stdout + browser.stderr
            print(browser.stdout, end='')
            saved = config_path.read_bytes()
            stop()
            client = start()
            assert config_path.read_bytes() == saved
            before = client.call('/api/sequence/config')
            assert {row['Name']: row['Group'] for row in before['definitions']} == {
                '旧版序列': '', '分组 / 甲': '批次一', '乙序列': '专项二',
                '批量甲 / 同名页': '批量默认', '批量乙 / 需修正': '批量单项'}
            old = client.call('/api/sequence/definition?id=' + old_id)['definition']
            client.call('/api/control', dict(action='start'))
            run = client.call('/api/sequence/control', dict(action='start', id=old_id, revision=before['revision'],
                                                             runName='分组应用期间保持运行', requestId=secrets.token_hex(16)))
            draft = client.call('/api/sequence/config?draft=1')
            preview = client.call('/api/sequence/edit-preview', dict(id=old_id, revision=draft['revision']))
            client.call('/api/sequence/save-edits', dict(token=preview['token'], group='x' * 81), expected=400)
            client.call('/api/sequence/save-edits', dict(token=preview['token'], group='  热更新  '))
            assert client.call('/api/configuration/schedule')['pending']
            assert client.call('/api/sequence/definition?id=' + old_id)['definition']['Group'] == ''
            process.stdin.write('tick\n')
            process.stdin.flush()
            wait(lambda: not client.call('/api/configuration/schedule')['pending'], 'committed group')
            definition = client.call('/api/sequence/definition?id=' + old_id)['definition']
            assert definition == dict(old, Group='热更新')
            current = client.call('/api/sequence/state?runId=' + run['runId'])['runtime']
            assert current['running'] and current['runId'] == run['runId'] and current['currentRow'] == 2
            history = client.call('/api/sequence/history?runId=' + run['runId'])
            assert all(row['Event'] != '配置切换' for row in history['rows'])
            client.call('/api/sequence/control', dict(action='stop', runId=run['runId']))
            client.call('/api/control', dict(action='stop'))
            print('PASS restart persistence, field validation and metadata-only hot application with an active run')
        finally:
            stop()


if __name__ == '__main__':
    main()
