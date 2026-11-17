import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { resolve, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { cp, rm, mkdtemp } from 'node:fs/promises';
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import net from 'node:net';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(resolve(process.argv[2]), 'package.json')), { chromium } = require('playwright');
const temporary = await mkdtemp(join(root, 'test-output', 'matching-ui-'));
await cp(join(root, 'config'), join(temporary, 'config'), { recursive: true });
await cp(join(root, 'web'), join(temporary, 'web'), { recursive: true });
for (const name of ['decoder.json', 'continuous-rules.json', 'tc-bindings.json', 'sequences.json', 'observed-commands.json']) await rm(join(temporary, 'config', name), { force: true });
execFileSync(resolve(process.argv[4]), ['-c', `from catalog_import_test import workbook
from pathlib import Path
import sys
p=Path(sys.argv[1])
field=lambda c,n,s=1:(c,n,0,1,'无符号整数',s)
(p/'first.xlsx').write_bytes(workbook([('主表',[field('A1','原参数')]),('保留表',[field('B1','保留参数')])]))
(p/'updated.xlsx').write_bytes(workbook([('主表',[field('A1','更新参数',2)]),('新表',[field('C1','跳过参数')])]))
(p/'other.xlsx').write_bytes(workbook([('不同名',[field('X1','其他')])]))`, temporary], { cwd: join(root, 'tools'), windowsHide: true });
const socket = net.createServer(); await new Promise(r => socket.listen(0, '127.0.0.1', r)); const port = socket.address().port; await new Promise(r => socket.close(r));
const server = spawn(resolve(process.argv[3]), ['--local', '--root', temporary, '--web-port', String(port)], { windowsHide: true, stdio: 'ignore' });
const base = 'http://127.0.0.1:' + port; let browser;
async function until(check) { for (let i = 0; i < 100; i++) { if (await check()) return; await new Promise(r => setTimeout(r, 50)); } throw Error('Timed out'); }
try {
  await until(async () => { try { return (await fetch(base + '/api/auth/me')).ok; } catch { return false; } });
  browser = await chromium.launch({ headless: true, channel: 'msedge' });
  const page = await browser.newPage({ viewport: { width: 1600, height: 1100 } }), errors = [];
  page.on('pageerror', e => errors.push(e.message)); await page.goto(base);
  await page.locator('#login-username').fill('matching_ui'); await page.locator('#login-name').fill('导入测试');
  await page.locator('#login-password').fill(randomBytes(16).toString('hex')); await page.locator('#login-submit').click();
  await page.locator('#workspace').waitFor({ state: 'visible' });
  await page.locator('[data-open-view="decoders"]').click();
  await until(() => page.locator('#current-tables-source').textContent().then(t => !t.includes('正在读取')));
  let latestPreview;
  await page.route('**/api/decoders/preview?*', async route => {
    const response = await route.fetch(); latestPreview = await response.json(); await route.fulfill({ response });
  });
  async function read(name) {
    await page.locator('#decoder-file').setInputFiles(join(temporary, name));
    latestPreview = null;
    await page.locator('#decoder-preview-button').click();
    await page.locator('#decoder-preview').waitFor({ state: 'visible' }); return latestPreview;
  }
  await read('first.xlsx'); await page.locator('#apply-preview').click();
  await page.locator('#decoder-preview').waitFor({ state: 'hidden' });
  const before = await page.evaluate(() => api('decoders'));
  let preview = await read('updated.xlsx');
  assert.equal(await page.locator('#replace-matching-preview').isEnabled(), true);
  assert.match(await page.locator('#import-plan').textContent(), /将替换：主表/);
  assert.match(await page.locator('#import-plan').textContent(), /保留已有表：保留表/);
  assert.match(await page.locator('#import-plan').textContent(), /跳过新文件中的不同名表：新表/);
  await page.locator('.decoder-import').screenshot({ path: join(root, 'test-output', 'matching-import-web.png') });
  await page.locator('#cancel-decoder-preview').click();
  await page.locator('#decoder-preview').waitFor({ state: 'hidden' });
  assert.equal(await page.locator('#decoder-file').inputValue(), '');
  assert.deepEqual(await page.evaluate(() => api('decoders')), before);
  await until(async () => (await page.request.get(base + '/api/decoders/preview/export?token=' + preview.token)).status() === 400);
  await read('updated.xlsx'); await page.locator('#replace-matching-preview').click();
  await page.locator('#decoder-preview').waitFor({ state: 'hidden' });
  const after = await page.evaluate(() => api('decoders'));
  assert.deepEqual(after.configuration.Catalog.Parameters.map(p => p.Code).sort(), ['A1', 'B1']);
  assert.equal(after.configuration.Catalog.Parameters.find(p => p.Code === 'A1').Scale, 2);
  assert.match(await page.locator('#current-table-rows').textContent(), /保留表/);
  await read('other.xlsx'); assert.equal(await page.locator('#replace-matching-preview').isDisabled(), true);
  await page.locator('#cancel-decoder-import').click();
  let release, late;
  const held = new Promise(r => release = r);
  await page.route('**/api/decoders/preview?*', async route => {
    const response = await route.fetch(); late = await response.json(); await held; await route.fulfill({ response });
  });
  await page.locator('#decoder-file').setInputFiles(join(temporary, 'updated.xlsx'));
  await page.locator('#decoder-preview-button').click(); await until(() => !!late);
  assert.equal(await page.locator('#decoder-preview-button').isDisabled(), true);
  await page.locator('#cancel-decoder-import').click();
  assert.equal(await page.locator('#decoder-preview-button').isEnabled(), true);
  release();
  await until(async () => (await page.request.get(base + '/api/decoders/preview/export?token=' + late.token)).status() === 400);
  assert.equal(await page.locator('#decoder-preview').isHidden(), true);
  assert.deepEqual(await page.evaluate(() => api('decoders')), after);
  assert.deepEqual(errors, []);
  console.log('PASS Edge: matching/retained/skipped table preview, selective save, cancel buttons, cancelled token invalidation, no-match disabled, cancellation during pending read, no stale preview revival or page errors');
} finally {
  await browser?.close(); server.kill();
  await new Promise(r => server.exitCode !== null ? r() : server.once('exit', r));
  await rm(temporary, { recursive: true, force: true });
}
