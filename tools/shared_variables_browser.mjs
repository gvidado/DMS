import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {resolve,dirname,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {cp,rm,readFile,writeFile,mkdtemp,mkdir} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import net from 'node:net';import dgram from 'node:dgram';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(join(resolve(process.argv[2]),'package.json'));
const {chromium}=require('playwright'),temporary=await mkdtemp(join(tmpdir(),'shared-variable-ui-'));
await cp(join(root,'config'),join(temporary,'config'),{recursive:true});await cp(join(root,'web'),join(temporary,'web'),{recursive:true});await rm(join(temporary,'config','decoder.json'),{force:true});
const rules=[{Id:'manual-mode',Code:'A4',Name:'共享状态判读',Level:'重要',Enabled:true,ParameterId:'工仪全程/A4',SourceIP:'127.0.0.1',Bindings:{},Script:'if(gzMode==1) then RET=Normal; else RET=Error; end'},
 {Id:'disabled-writer',Code:'A1',Name:'停用写入规则',Level:'一般',Enabled:false,ParameterId:'工仪全程/A1',SourceIP:'127.0.0.1',Bindings:{},Script:'gzReady=true; RET=Normal;'}];
await writeFile(join(temporary,'config','continuous-rules.json'),JSON.stringify({SchemaVersion:1,Revision:'shared-ui',Rules:rules}));
await writeFile(join(temporary,'config','send-monitor.json'),JSON.stringify({SourceIPs:['127.0.0.1'],DestinationIPs:['127.0.0.2'],InterfaceId:'',DestinationPort:0}));
const emitter=dgram.createSocket('udp4');await new Promise(r=>emitter.bind(0,'127.0.0.1',r));const reserve=dgram.createSocket('udp4');await new Promise(r=>reserve.bind(0,'127.0.0.1',r));const udpPort=reserve.address().port;await new Promise(r=>reserve.close(r));
const settings=JSON.parse((await readFile(join(temporary,'config','server.json'),'utf8')).replace(/^\uFEFF/,''));Object.assign(settings,{BindAddress:'127.0.0.1',SourceIPs:['127.0.0.1'],UdpPort:udpPort,MappingConfirmed:true,DataTimeoutMs:10000,MulticastGroup:'',MulticastInterface:''});await writeFile(join(temporary,'config','server.json'),JSON.stringify(settings));
const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const port=listener.address().port;await new Promise(r=>listener.close(r));const base='http://127.0.0.1:'+port;
const server=spawn(process.argv[3]?resolve(process.argv[3]):join(root,'TelemetryMonitor.exe'),['--local','--root',temporary,'--web-port',String(port)],{windowsHide:true});let browser;
try {
 for(let i=0;;i++){try{await fetch(base+'/api/auth/me');break;}catch(e){if(i>60)throw e;await new Promise(r=>setTimeout(r,100));}}
 browser=await chromium.launch({headless:true,channel:'msedge'});const page=await browser.newPage({viewport:{width:1440,height:1100}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base);await page.locator('#login-username').fill('shared_ui');await page.locator('#login-name').fill('共享变量网页测试');await page.locator('#login-password').fill(randomBytes(16).toString('hex'));await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 await page.locator('.home-entry[data-open-view="judgments"]').click();const row=page.locator('#global-rows tr[data-global-name="gzMode"]');await row.waitFor();assert.equal(await row.locator('button').isDisabled(),true);assert.match(await page.locator('#global-rows').innerText(),/已停用/);
 await page.evaluate(async()=>{const interfaces=await api('send-monitor/interfaces');const adapter=interfaces.interfaces.find(a=>a.loopback);await api('send-monitor/config',{SourceIPs:['127.0.0.1'],DestinationIPs:['127.0.0.2'],InterfaceId:adapter.id,DestinationPort:0});await api('control',{action:'start'});});await page.waitForFunction(()=>!document.querySelector('#global-rows tr[data-global-name="gzMode"] button').disabled);
 await row.locator('button').click();await page.locator('#global-value').fill('1');await page.locator('#global-save').click();await page.locator('#global-dialog').waitFor({state:'hidden'});await page.waitForFunction(()=>document.querySelector('#global-rows tr[data-global-name="gzMode"]').textContent.includes('预设'));
 await page.locator('.nav-item[data-view="results"]').click();await page.locator('#rule-start').click();await page.waitForFunction(()=>document.querySelector('#rule-run-status').textContent==='判读运行中');
 const packet=Buffer.alloc(1030);packet.writeUInt16BE(0x0808,6);packet.writeUInt16BE(0xc001,8);packet.writeUInt16BE(1017,10);packet[12]=0x30;packet[13]=3;packet[14]=25;packet.writeUInt16BE(4100,18);packet.writeFloatBE(-2.5,35);let a=0,b=0;for(const n of packet.subarray(6,-2)){a=(a+n)%255;b=(b+a)%255;}packet[1028]=(255-(a+b)%255)%255;packet[1029]=(255-(a+packet[1028])%255)%255;
 await new Promise((r,j)=>emitter.send(packet,udpPort,'127.0.0.1',e=>e?j(e):r()));await page.waitForFunction(()=>document.querySelector('#judgment-rows tr[data-status="passed"]'));
 await page.locator('.nav-item[data-view="judgments"]').click();await page.waitForFunction(()=>!document.querySelector('#global-rows tr[data-global-name="gzMode"] button').disabled);assert.equal(await page.locator('#rule-new').isDisabled(),true);
 await row.locator('button').click();await page.locator('#global-value').fill('0');await page.locator('#global-save').click();await page.locator('#global-dialog').waitFor({state:'hidden'});await page.waitForFunction(async()=>{const s=await api('continuous-rules');return s.rows[0].result==='Error';});
 await row.locator('button').click();await page.locator('#global-kind').selectOption('nil');assert.equal(await page.locator('#global-value').isDisabled(),true);await page.locator('#global-save').click();await page.locator('#global-dialog').waitFor({state:'hidden'});await row.locator('button').click();assert.equal(await page.locator('#global-kind').inputValue(),'nil');await page.locator('#global-cancel').click();
 await row.locator('button').click();await page.evaluate(async()=>{const g=await api('continuous-rules/globals');const r=g.rows.find(r=>r.name==='gzMode');await api('continuous-rules/globals',{name:r.name,kind:'number',value:'2',revision:r.revision,generation:g.generation});});await page.locator('#global-kind').selectOption('number');await page.locator('#global-value').fill('3');await page.locator('#global-save').click();await page.waitForFunction(()=>document.querySelector('#global-editor-error').textContent.includes('状态已变化'));assert.equal(await page.locator('#global-dialog').isVisible(),true);await page.locator('#global-cancel').click();
 await mkdir(join(root,'test-output'),{recursive:true});await page.screenshot({path:join(root,'test-output','shared-variables-config.png'),fullPage:true});await page.setViewportSize({width:900,height:900});assert.equal(await page.locator('#shared-variable-panel').isVisible(),true);assert.deepEqual(errors,[]);
 console.log('PASS shared variable panel, listener guard, pre-run setup, editing during judgment, immediate dependent results, NIL editor, stale edit rejection and JavaScript checks');
} finally {await browser?.close();emitter.close();server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));await rm(temporary,{recursive:true,force:true});}
