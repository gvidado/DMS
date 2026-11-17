import assert from 'node:assert/strict';
import {createRequire} from 'node:module';import {resolve,join} from 'node:path';import {readFile} from 'node:fs/promises';
const require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const settings=JSON.parse(await readFile(process.argv[3],'utf8')),base=settings.base;
const browser=await chromium.launch({headless:true,channel:'msedge'});
try{
 const context=await browser.newContext({viewport:{width:1600,height:1050}}),page=await context.newPage(),errors=[];
 context.on('page',p=>p.on('pageerror',e=>errors.push(e.message)));page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base+'/?view=sequence-run');await page.locator('#login-username').fill(settings.username);await page.locator('#login-password').fill(settings.password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 await page.locator('#sequence-history-result').selectOption('abnormal');await page.waitForFunction(()=>document.querySelectorAll('#sequence-history-rows tr[data-abnormal-key]').length===50);
 assert.equal(await page.locator('#sequence-history-rows tr').count(),50);await page.locator('#sequence-history-next').click();await page.waitForFunction(()=>document.querySelector('#sequence-history-page').textContent.startsWith('2 / 2'));
 await page.locator('#sequence-history-scope').selectOption(settings.runId);await page.locator('#sequence-history-result').selectOption('pending');await page.waitForFunction(()=>document.querySelectorAll('#sequence-history-rows tr[data-abnormal-key]').length===1);
 const target=await page.locator('#sequence-history-rows tr[data-abnormal-key]').getAttribute('data-abnormal-key');await page.locator('#sequence-history-rows button').click();await page.locator('#abnormal-reason').fill('   ');await page.locator('#abnormal-release-save').click();assert.match(await page.locator('#abnormal-release-error').textContent(),/填写解除原因/);
 const reason='页面确认原因\n已核实测试条件';await page.locator('#abnormal-reason').fill(reason);await page.locator('#abnormal-release-save').click();await page.locator('#abnormal-dialog').waitFor({state:'hidden'});
 await page.waitForFunction(()=>document.querySelector('#sequence-history-rows').textContent.includes('暂无符合条件的异常记录'));
 await page.locator('#sequence-history-result').selectOption('released');await page.waitForFunction(()=>document.querySelectorAll('#sequence-history-rows tr[data-abnormal-key]').length===2);
 assert.ok((await page.locator('#sequence-history-rows').textContent()).includes(reason));assert.ok((await page.locator('#sequence-history-rows').textContent()).includes('已确认'));
 const url=new URL(await page.locator('#sequence-export').getAttribute('href'),base);assert.equal(url.searchParams.get('result'),'released');assert.equal(url.searchParams.get('runId'),settings.runId);const csv=await context.request.get(url.href);const header=(await csv.text()).replace(/^\uFEFF/,'').split(/\r?\n/)[0];assert.equal(header.split(',').length,11);assert.ok(header.includes('工作名称')&&!header.includes('确认原因'));
 const unified=await context.newPage();await unified.goto(base+'/?view=results');await unified.locator('#workspace').waitFor({state:'visible'});await unified.locator('#abnormal-type').selectOption('sequence-judge');await unified.locator('#abnormal-status').selectOption('released');await unified.locator('#abnormal-rows tr[data-abnormal-key="'+target+'"]').waitFor();assert.ok((await unified.locator('#abnormal-rows').textContent()).includes(reason));
 await page.locator('#sequence-history-scope').selectOption('all');await page.locator('#sequence-history-result').selectOption('pending');await page.waitForFunction(()=>[...document.querySelectorAll('#sequence-history-rows tr[data-abnormal-key]')].length>0&&[...document.querySelectorAll('#sequence-history-rows tr[data-abnormal-key]')].every(row=>row.dataset.released==='false'));
 const next=await page.locator('#sequence-history-rows tr[data-abnormal-key]').first().getAttribute('data-abnormal-key');await unified.locator('#abnormal-status').selectOption('pending');await unified.locator('#abnormal-rows tr[data-abnormal-key="'+next+'"] button').click();await unified.locator('#abnormal-reason').fill('统一记录确认');await unified.locator('#abnormal-release-save').click();await unified.locator('#abnormal-dialog').waitFor({state:'hidden'});
 await page.waitForFunction(key=>!document.querySelector('#sequence-history-rows tr[data-abnormal-key="'+key+'"]'),next);
 assert.deepEqual(errors,[]);console.log('PASS browser abnormal pagination, run/status filters, mandatory reason, CSV scope and two-way shared confirmation');
}finally{await browser.close();}
