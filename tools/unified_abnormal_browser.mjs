import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {writeFile} from 'node:fs/promises';
import {join,resolve} from 'node:path';
const options=JSON.parse(readFileSync(0,'utf8'));
const require=createRequire(join(resolve(process.argv[2]),'package.json'));
const {chromium}=require('playwright'),browser=await chromium.launch({headless:true,channel:'msedge'});
const checks=[],errors=[];
function check(value,label){assert.ok(value,label);checks.push(label);console.log('PASS',label);}
try{
  const page=await browser.newPage({viewport:{width:1600,height:1100}});
  page.on('pageerror',error=>errors.push(error.message));
  await page.goto(options.base);await page.locator('#login-username').fill('unifiedqa');
  await page.locator('#login-password').fill(options.password);await page.locator('#login-submit').click();
  await page.locator('#workspace').waitFor({state:'visible'});
  await page.evaluate(()=>{navigate('results',true);UnifiedAbnormalUI.open();});
  const all=page.locator('#abnormal-release-all');await all.waitFor({state:'visible'});
  await page.waitForFunction(()=>!document.getElementById('abnormal-release-all').disabled);
  await page.locator('#abnormal-type').selectOption('spp');
  await all.click();await page.locator('#abnormal-dialog').waitFor({state:'visible'});
  check((await page.locator('#abnormal-release-target').textContent()).includes('全部类型、全部分页'),
    'batch dialog states that release covers all types and pages despite active filters');
  await page.locator('#abnormal-release-save').click();
  check(await page.locator('#abnormal-dialog').isVisible()
    &&(await page.evaluate(()=>api('abnormal-records'))).pending===options.pending,
    'an empty common reason cannot submit the release');
  await page.locator('#abnormal-reason').fill(options.reason);
  const response=page.waitForResponse(value=>value.url().endsWith('/api/abnormal-records/release-all')&&value.request().method()==='POST');
  await page.locator('#abnormal-release-save').click();
  const release=await(await response).json();
  await page.locator('#abnormal-dialog').waitFor({state:'hidden'});
  check(release.ok&&release.released===options.pending&&release.released>50&&release.remaining===0,
    'one browser submission releases every pending record beyond the current page');
  await page.waitForFunction(()=>document.getElementById('abnormal-release-all').disabled);
  check((await page.evaluate(()=>api('abnormal-records'))).pending===0,
    'browser refresh shows no pending records after batch release');
  check(errors.length===0,'batch release UI has no browser script errors');
  await page.screenshot({path:join(options.output,'batch-release.png'),fullPage:true});
  await writeFile(join(options.output,'batch-release-browser-report.json'),
    JSON.stringify({checks:checks.length,details:checks,release},null,2));
}finally{await browser.close();}
