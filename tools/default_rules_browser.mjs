import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {resolve,dirname,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {cp,rm,readFile,mkdtemp} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import net from 'node:net';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(join(resolve(process.argv[2]),'package.json'));
const {chromium}=require('playwright'),temporary=await mkdtemp(join(tmpdir(),'default-rules-ui-'));
await cp(join(root,'config'),join(temporary,'config'),{recursive:true});await cp(join(root,'web'),join(temporary,'web'),{recursive:true});
await rm(join(temporary,'config','continuous-rules.json'),{force:true});
const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const port=listener.address().port;await new Promise(r=>listener.close(r));const base='http://127.0.0.1:'+port;
const server=spawn(process.argv[3]?resolve(process.argv[3]):join(root,'TelemetryMonitor.exe'),['--local','--root',temporary,'--web-port',String(port)],{windowsHide:true});let browser;
try {
 for(let i=0;;i++){try{await fetch(base+'/api/auth/me');break;}catch(e){if(i>60)throw e;await new Promise(r=>setTimeout(r,100));}}
 browser=await chromium.launch({headless:true,channel:'msedge'});const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base);await page.locator('#login-username').fill('defaults_ui');await page.locator('#login-name').fill('默认初始化验证');await page.locator('#login-password').fill(randomBytes(16).toString('hex'));await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 await page.locator('.home-entry[data-open-view="judgments"]').click();await page.waitForFunction(()=>document.querySelector('#rule-rows tr[data-rule-id]'));
 const total=await page.evaluate(async()=>{const s=await fetch('/api/continuous-rules').then(r=>r.json());return s.total;});assert.ok(total>=709);
 const template=await page.evaluate(async()=>{const s=await fetch('/api/continuous-rules').then(r=>r.json());return s.defaultScript;});assert.ok(template.includes('this.state=0;') && template.includes('StateRegion\n\nEndStateRegion') && template.includes('RET=Normal;'));
 assert.equal(await page.locator('#rule-rows input:checked').count(),0);assert.match(await page.locator('#rule-rows').innerText(),/默认三域/);assert.doesNotMatch(await page.locator('#rule-rows').innerText(),/需修改/);
 await page.locator('#rule-rows tr[data-rule-id] button').first().click();await page.locator('#rule-dialog').waitFor({state:'visible'});assert.equal(await page.locator('#rule-script').inputValue(),template);assert.equal(await page.locator('#rule-level').inputValue(),'一般');assert.equal(await page.locator('#rule-enabled').isChecked(),false);
 await page.locator('#rule-level').selectOption('重要');await page.locator('#rule-save').click();await page.locator('#rule-dialog').waitFor({state:'hidden'});await page.waitForFunction(()=>document.querySelector('#rule-rows tr[data-rule-id] td:nth-child(2)').textContent==='重要');
 await page.locator('#rule-rows tr[data-rule-id] button').first().click();assert.equal(await page.locator('#rule-script').inputValue(),template);assert.equal(await page.locator('#rule-level').inputValue(),'重要');await page.locator('#rule-editor-cancel').click();
 await page.locator('#rule-new').click();assert.equal(await page.locator('#rule-script').inputValue(),template);await page.locator('#rule-editor-cancel').click();
 await page.screenshot({path:join(root,'test-output','default-rules-config.png'),fullPage:true});
 await page.locator('.nav-item[data-view="results"]').click();await page.waitForFunction(()=>document.querySelectorAll('#judgment-rows tr[data-status="disabled"]').length>0);assert.equal(await page.locator('#rule-start').isDisabled(),true);assert.equal(errors.length,0,errors.join('\n'));
 console.log('PASS existing decoder '+total+' parameter three-region defaults, empty state region, default editor/save/reopen/new, disabled results and no JavaScript errors');
} finally {await browser?.close();server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));await rm(temporary,{recursive:true,force:true});}
