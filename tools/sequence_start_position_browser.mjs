import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {resolve,dirname,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {cp,rm,writeFile,readFile,mkdtemp} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import net from 'node:net';
import dgram from 'node:dgram';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const temp=await mkdtemp(join(root,'test-output','sequence-start-browser-')),checks=[];let server,browser;
function check(ok,label){assert.ok(ok,label);checks.push(label);console.log('PASS',label);}
async function until(fn,label){const end=Date.now()+12000;while(Date.now()<end){if(await fn())return;await new Promise(r=>setTimeout(r,60));}throw Error('Timed out: '+label);}
async function port(udp=false){const s=udp?dgram.createSocket('udp4'):net.createServer();await new Promise(r=>udp?s.bind(0,'127.0.0.1',r):s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
try{
 await cp(join(root,'config'),join(temp,'config'),{recursive:true});await cp(join(root,'web'),join(temp,'web'),{recursive:true});
 for(const f of ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json','command-send.json'])await rm(join(temp,'config',f),{force:true});
 const web=await port(),udp=await port(true),base='http://127.0.0.1:'+web,password=randomBytes(20).toString('hex'),j='1'.repeat(32),e='2'.repeat(32),paged='3'.repeat(32);
 const settings=JSON.parse((await readFile(join(temp,'config/server.json'),'utf8')).replace(/^\uFEFF/,''));Object.assign(settings,{BindAddress:'127.0.0.1',UdpPort:udp,SourceIPs:['127.0.0.1'],MappingConfirmed:true,MulticastGroup:'',MulticastInterface:'',DataTimeoutMs:60000});await writeFile(join(temp,'config/server.json'),JSON.stringify(settings));
 await writeFile(join(temp,'config/send-monitor.json'),JSON.stringify({SourceIP:'127.0.0.1',DestinationIP:'127.0.0.2',InterfaceId:'',DestinationPort:0}));
 const row=(Row,Keyword='',Expected='')=>({Row,Keyword,Description:'启动设置检查',Condition:Keyword?'':'TM.A1.value==99',Mark:'',Expected,Timeout:Keyword?'':'5'});
 const marker={...row(2,'TEXT'),Mark:'T0'};
 await writeFile(join(temp,'config/sequences.json'),JSON.stringify({SchemaVersion:1,Revision:'start-ui',DefaultTimeoutSeconds:5,NextJudgeNumber:3,NextExecuteNumber:2,Sequences:[
  {Id:j,Code:'XLP0001',Name:'判读指定行',Mode:'judge',SourceIP:'127.0.0.1',Rows:[marker,row(4,'','T0:.6'),row(8,'','F:6')]},
  {Id:e,Code:'XLZ0001',Name:'执行指定行',Mode:'execute',SourceIP:'127.0.0.1',Rows:[row(2,'PAUSE'),row(4,'','F:6')]},
  {Id:paged,Code:'XLP0002',Name:'跨页指定行',Mode:'judge',SourceIP:'127.0.0.1',Rows:[...Array.from({length:55},(_,i)=>row(i+2,'TEXT')),row(200)]}
 ]}));
 server=spawn(resolve(process.argv[3]),['--local','--root',temp,'--web-port',String(web)],{windowsHide:true,stdio:'ignore'});await until(async()=>{try{return(await fetch(base+'/api/auth/me')).ok;}catch{return false;}},'server');
 browser=await chromium.launch({headless:true,channel:'msedge'});const context=await browser.newContext({viewport:{width:1600,height:1080}}),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await context.request.post(base+'/api/auth/setup',{data:{username:'entryui',name:'启动界面检查',password}});await context.request.post(base+'/api/auth/login',{data:{username:'entryui',password}});await page.goto(base+'/?section=sequences&view=sequence-run');await page.locator('#workspace').waitFor({state:'visible'});
 await until(()=>page.locator('#sequence-run-select option').count().then(n=>n===2),'judge options');await page.locator('#sequence-run-select').selectOption(j);
 await page.evaluate(async()=>{const adapter=(await api('send-monitor/interfaces')).interfaces.find(i=>i.loopback);await api('send-monitor/config',{SourceIPs:['127.0.0.1'],DestinationIPs:['127.0.0.2'],InterfaceId:adapter.id,DestinationPort:0});await api('control',{action:'start'});});await until(()=>page.evaluate(()=>app.state?.mode==='live'),'listener state');
 await page.locator('#sequence-start').click();await page.locator('#sequence-start-dialog').waitFor({state:'visible'});
 check(await page.locator('#sequence-start-row').inputValue()===''&&await page.locator('#sequence-start-t0').inputValue()===''&&await page.locator('#sequence-start-f0').inputValue()==='','new startup inputs default to blank');
 await page.locator('#sequence-start-row').fill('4');await page.locator('#sequence-start-confirm').click();await until(()=>page.locator('#sequence-start-error').textContent().then(t=>t.includes('T0')),'missing T0 error');check(await page.locator('#sequence-start-dialog').isVisible(),'invalid start keeps dialog open with its specific failure reason');
 await page.locator('#sequence-start-t0').fill('T0=0.1');await page.locator('#sequence-start-f0').fill('F0=5');await page.locator('#sequence-start-dialog').screenshot({path:join(root,'test-output/sequence-start-position/start-dialog.png')});await page.locator('#sequence-start-confirm').click();await page.locator('#sequence-start-dialog').waitFor({state:'hidden'});
 const initial=await page.evaluate(async id=>(await api('sequence/state?id='+id)).runtime,j);
 check(initial.startRow===4&&initial.initialT0===.1&&initial.initialF0===5&&initial.skipped===1,'form submits original row and both labeled presets');
 await until(()=>page.locator('#sequence-run-rows tr').first().textContent().then(t=>t.includes('已跳过')),'skipped row rendered');check((await page.locator('#sequence-summary').textContent()).includes('从原表第4行开始')&&(await page.locator('#sequence-t0').textContent()).includes('启动补设'),'summary distinguishes partial start and preset T0');
 await page.locator('#sequence-stop').click();await until(()=>page.locator('#sequence-stop').isDisabled(),'stopped');await page.locator('#sequence-start').click();check(await page.locator('#sequence-start-row').inputValue()===''&&await page.locator('#sequence-start-t0').inputValue()===''&&await page.locator('#sequence-start-f0').inputValue()==='','reopening startup dialog clears prior settings');await page.locator('#sequence-start-cancel').click();
 check(!(await page.evaluate(async()=>(await api('sequence/config')).runtimes.judge.running)),'cancel does not start another run');
 await page.locator('#sequence-run-select').selectOption(paged);await page.locator('#sequence-start').click();await page.locator('#sequence-start-row').fill('200');await page.locator('#sequence-start-confirm').click();await page.locator('#sequence-start-dialog').waitFor({state:'hidden'});await until(()=>page.locator('#sequence-run-page').textContent().then(t=>t.startsWith('2 / 2')),'start row page');
 check((await page.locator('#sequence-run-rows').textContent()).includes('200')&&(await page.locator('#sequence-summary').textContent()).includes('已跳过 55项'),'partial start automatically opens the page containing the selected row');await page.locator('#sequence-stop').click();await until(()=>page.locator('#sequence-stop').isDisabled(),'paged run stopped');
 await page.locator('#feature-tab-sequence-execute').click();await until(()=>page.locator('#sequence-run-select option').count().then(n=>n===1),'execute options');await page.locator('#sequence-start').click();await page.locator('#sequence-start-row').fill('4');await page.locator('#sequence-start-f0').fill('5');await page.locator('#sequence-start-confirm').click();await page.locator('#sequence-start-dialog').waitFor({state:'hidden'});await until(()=>page.locator('#sequence-run-rows').textContent().then(t=>t.includes('已跳过')),'execution skip');
 const executed=await page.evaluate(async()=>(await api('sequence/config')).runtimes.execute);check(executed.startRow===4&&executed.initialF0===5&&executed.initialT0===null&&!executed.paused,'execution tab supports partial start with F0 only and skips earlier pause');await page.locator('#sequence-stop').click();await page.evaluate(()=>api('control',{action:'stop'}));
 check(errors.length===0,'no browser errors');await writeFile(join(root,'test-output/sequence-start-position/browser-report.json'),JSON.stringify({checks:checks.length,details:checks},null,2));console.log('All '+checks.length+' sequence startup UI checks passed.');
}finally{
 await browser?.close();if(server){server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));}assert.ok(resolve(temp).startsWith(join(root,'test-output')+'\\'));await rm(temp,{recursive:true,force:true});
}
