import assert from 'node:assert/strict';import {createRequire} from 'node:module';import {resolve,join} from 'node:path';
let input='';for await(const chunk of process.stdin)input+=chunk;const config=JSON.parse(input),require=createRequire(join(resolve(process.argv[2]),'package.json'));
const {chromium}=require('playwright'),browser=await chromium.launch({headless:true,channel:'msedge'}),errors=[];
try{
  const page=await browser.newPage({viewport:{width:1680,height:1120}});page.on('pageerror',error=>errors.push(error.message));await page.goto(config.base);
  await page.locator('#login-username').fill('resyncqa');await page.locator('#login-password').fill(config.password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
  await page.evaluate(()=>navigate('overview',true));const b0=page.locator('#packet-stat-rows tr[data-packet-id="b0"]');await b0.waitFor({state:'visible'});
  assert.equal(await page.locator('#packet-stat-rows tr').count(),3);assert.equal(await b0.locator('td').count(),12);
  assert.equal(await b0.locator('.spp-timestamp').count(),3);
  assert.match(await b0.locator('.spp-result').textContent(),/参数已更新/);
  assert.ok(Number(await b0.locator('[data-field="accepted"]').textContent())>1);assert.equal(await b0.locator('[data-field="last-sequence"]').textContent(),'3127');
  assert.match(await b0.locator('[data-field="resync"]').getAttribute('title'),/3130 → 3126/);
  assert.match(await page.locator('#diagnostic-grid').textContent(),/计数重同步/);
  assert.equal(await page.evaluate(()=>{
    const row=document.querySelector('#packet-stat-rows tr[data-packet-id="b0"]');
    const field=document.querySelector('#diagnostic-grid .diag-value');
    renderOverview();
    return row===document.querySelector('#packet-stat-rows tr[data-packet-id="b0"]')&&field===document.querySelector('#diagnostic-grid .diag-value');
  }),true);assert.deepEqual(errors,[]);
  await page.locator('.spp-stat-panel').screenshot({path:config.screenshot});console.log('PASS live packet diagnostics show accepted frames, last sequence and per-stream resync details');
}finally{await browser.close();}
