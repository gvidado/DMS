import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { join } from 'node:path';

let input = '';
for await (const chunk of process.stdin) input += chunk;
const test = JSON.parse(input);
const { chromium } = createRequire(import.meta.url)(join(process.argv[2], 'playwright'));
const browser = await chromium.launch({ headless: true, channel: 'msedge' });
try {
  const page = await browser.newPage();
  await page.goto(test.base + '/?section=data&view=raw-spp');
  await page.locator('#login-username').fill('rawqa');
  await page.locator('#login-password').fill(test.password);
  await page.locator('#login-submit').click();

  await page.locator('#raw-spp-filter option[value="raw-a"]').waitFor({ state: 'attached' });
  await page.locator('#raw-spp-filter').selectOption('raw-a');
  const row = page.locator('#raw-spp-rows tr').filter({ hasText: '原包raw-a' }).first();
  const compact = text => text.replace(/\s+/g, '').toLowerCase();
  assert.equal(compact(await row.locator('td').nth(5).textContent()), test.udp.slice(0, 48) + '…');

  await row.getByRole('button', { name: '查看完整源码' }).click();
  const udp = page.locator('#raw-spp-udp');
  await udp.waitFor({ state: 'visible' });
  await page.waitForFunction(expected => document.querySelector('#raw-spp-udp').value.replace(/\s+/g, '').toLowerCase() === expected, test.udp);
  assert.equal(await page.locator('#raw-spp-hex').isVisible(), false);
  await page.getByText('查看SPP包内源码', { exact: true }).click();
  assert.equal(compact(await page.locator('#raw-spp-hex').inputValue()), test.udp.slice(12));
  console.log('PASS browser displays full UDP by default and preserves optional inner SPP view');
} finally {
  await browser.close();
}
