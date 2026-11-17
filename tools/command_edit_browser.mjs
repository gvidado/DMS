import assert from 'node:assert/strict';
import {createRequire} from 'node:module';import {resolve,dirname,join} from 'node:path';import {fileURLToPath} from 'node:url';
import {cp,rm,writeFile,mkdtemp} from 'node:fs/promises';import {spawn} from 'node:child_process';import {randomBytes} from 'node:crypto';import net from 'node:net';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const temporary=await mkdtemp(join(root,'test-output','command-edit-browser-'));await cp(join(root,'config'),join(temporary,'config'),{recursive:true});await cp(join(root,'web'),join(temporary,'web'),{recursive:true});
for(const name of ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json'])await rm(join(temporary,'config',name),{force:true});
const commands=[{Number:1,Name:'指令甲',DestinationIP:'192.168.1.113',DestinationIPs:['192.168.1.113','192.168.1.111'],PayloadHex:'00 AA',Length:2},{Number:2,Name:'指令乙',DestinationIP:'192.168.1.113',PayloadHex:'00 BB',Length:2},{Number:3,Name:'未分类',DestinationIP:'127.0.0.2',PayloadHex:'00 CC',Length:2}];
await writeFile(join(temporary,'config','observed-commands.json'),JSON.stringify({SchemaVersion:1,SourceIP:'127.0.0.1',IdentityRule:'udp-payload',NextNumber:4,Commands:commands}));
await writeFile(join(temporary,'config','command-send.json'),JSON.stringify({SchemaVersion:1,SourceIP:'127.0.0.1',Routes:[{DestinationIP:'192.168.1.113',SourcePort:53334,DestinationPort:31000}]}));
const portServer=net.createServer();await new Promise(r=>portServer.listen(0,'127.0.0.1',r));const port=portServer.address().port;await new Promise(r=>portServer.close(r));
const server=spawn(resolve(process.argv[3]),['--local','--root',temporary,'--web-port',String(port)],{windowsHide:true,stdio:'ignore'}),base='http://127.0.0.1:'+port,password=randomBytes(18).toString('hex');let browser;
const checks=[];function check(value,label){assert.ok(value,label);checks.push(label);console.log('PASS',label);}
async function until(fn,label,timeout=12000){const end=Date.now()+timeout;while(Date.now()<end){if(await fn())return;await new Promise(r=>setTimeout(r,70));}throw new Error('Timed out: '+label);}
try{
 await until(async()=>{try{return(await fetch(base+'/api/auth/me')).ok;}catch{return false;}},'server');
 browser=await chromium.launch({headless:true,channel:'msedge'});const context=await browser.newContext({viewport:{width:1440,height:1050}}),page=await context.newPage(),errors=[];
 context.on('page',p=>p.on('pageerror',e=>errors.push(e.message)));page.on('pageerror',e=>errors.push(e.message));
 await context.request.post(base+'/api/auth/setup',{data:{username:'editorqa',name:'指令编辑测试',password}});await page.goto(base+'/?view=command-config');await page.locator('#login-username').fill('editorqa');await page.locator('#login-password').fill(password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 const row=code=>page.locator('#command-catalog-rows tr').filter({has:page.locator('td:first-child',{hasText:new RegExp('^'+code+'$')})});
 await row('BC0001').getByRole('button',{name:'编辑',exact:true}).waitFor();
 check(await row('BC0001').getByRole('button',{name:'编辑',exact:true}).isVisible(),'each saved library row has an edit button on its independent configuration page');
 await row('BC0001').getByRole('button',{name:'编辑',exact:true}).click();await page.locator('#command-edit-name').fill('取消的名称');await page.locator('#command-edit-code').fill('BC0042');await page.waitForTimeout(1200);
 check(await page.locator('#command-edit-name').inputValue()==='取消的名称','realtime page updates preserve the name and code being edited');await page.locator('#command-edit-cancel').click();
 check((await page.evaluate(()=>api('send-monitor/commands/list'))).rows.some(r=>r.code==='BC0001'&&r.name==='指令甲'),'cancel closes editing without changing the saved library');
 const sending=await context.newPage();await sending.goto(base+'/?view=command-send');await sending.locator('#workspace').waitFor({state:'visible'});await sending.locator('tr[data-send-code="BC0001"]').waitFor();await sending.locator('tr[data-send-code="BC0001"] td').first().click();
 await until(()=>sending.locator('#command-send-hex').inputValue().then(t=>t==='00 AA'),'send preview');
 await row('BC0001').getByRole('button',{name:'编辑',exact:true}).click();await page.locator('#command-edit-name').fill('修改后的名称');await page.locator('#command-edit-code').fill('BC0002');await page.locator('#command-edit-save').click();await page.locator('#command-edit-error').filter({hasText:'已存在'}).waitFor();
 check(await page.locator('#command-edit-dialog').isVisible()&&await page.locator('#command-edit-name').inputValue()==='修改后的名称','duplicate code errors keep the editor and typed name available for correction');
 await page.locator('#command-edit-code').fill('BC0042');await page.locator('#command-edit-save').click();await page.locator('#command-edit-dialog').waitFor({state:'hidden'});await row('BC0042').waitFor();
 check((await row('BC0042').textContent()).includes('修改后的名称')&&(await row('PSS0001').textContent()).includes('修改后的名称'),'saving refreshes the selected category code and the name shared across categories');
 await sending.locator('tr[data-send-code="BC0042"]').waitFor();check(await sending.locator('#command-send-hex').inputValue()===''&&(await sending.locator('#command-send-selected').textContent()).includes('重新选择'),'an already-open sending page refreshes codes and invalidates its old preview without sending');
 await row('BC0042').getByRole('button',{name:'编辑',exact:true}).click();await page.locator('#command-edit-name').fill('本窗口未保存');
 await sending.evaluate(async()=>{const d=await api('send-monitor/commands/list');await api('send-monitor/commands/edit',{number:'1',originalCode:'BC0042',code:'BC0042',name:'其他窗口的名称',revision:String(d.revision)});});
 await page.locator('#command-edit-save').click();await page.locator('#command-edit-error').filter({hasText:'已变化'}).waitFor();check(await page.locator('#command-edit-name').inputValue()==='本窗口未保存','a concurrent edit is rejected instead of overwriting another window');await page.locator('#command-edit-cancel').click();
 await page.locator('#command-catalog-refresh').click();await until(()=>row('BC0042').textContent().then(t=>t.includes('其他窗口的名称')),'fresh name');await page.locator('#command-catalog-rows tr').filter({hasText:'未分类 #3'}).getByRole('button',{name:'编辑',exact:true}).click();
 check(await page.locator('#command-edit-code').isDisabled()&&await page.locator('#command-edit-name').isEnabled(),'unclassified rows offer name editing and explain why no category code is available');await page.keyboard.press('Escape');
 await row('BC0042').getByRole('button',{name:'编辑',exact:true}).click();await page.screenshot({path:join(root,'test-output','command-edit-dialog.png'),fullPage:true});await page.locator('#command-edit-close').click();
 assert.deepEqual(errors,[]);check(true,'both configuration and sending pages remain free of browser script errors');console.log('All '+checks.length+' command-edit browser checks passed. No command sent.');
}finally{await browser?.close();server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));const absolute=resolve(temporary);assert.ok(absolute.startsWith(join(root,'test-output')+'\\'));await rm(temporary,{recursive:true,force:true});}
