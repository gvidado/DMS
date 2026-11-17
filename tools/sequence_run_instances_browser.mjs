import assert from 'node:assert/strict';
import {createRequire} from 'node:module';import {resolve,dirname,join} from 'node:path';import {fileURLToPath} from 'node:url';
import {cp,rm,writeFile,mkdtemp,readFile} from 'node:fs/promises';import {spawn} from 'node:child_process';import {randomBytes} from 'node:crypto';import net from 'node:net';import dgram from 'node:dgram';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const temporary=await mkdtemp(join(root,'test-output','sequence-instance-ui-'));await cp(join(root,'config'),join(temporary,'config'),{recursive:true});await cp(join(root,'web'),join(temporary,'web'),{recursive:true});
for(const file of ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json'])await rm(join(temporary,'config',file),{force:true});
const udp=dgram.createSocket('udp4');await new Promise(r=>udp.bind(0,'127.0.0.1',r));const udpPort=udp.address().port;await new Promise(r=>udp.close(r));
const settings=JSON.parse((await readFile(join(temporary,'config','server.json'),'utf8')).replace(/^\uFEFF/,''));Object.assign(settings,{BindAddress:'127.0.0.1',UdpPort:udpPort,SourceIPs:['127.0.0.1'],MappingConfirmed:true,MulticastGroup:'',MulticastInterface:'',DataTimeoutMs:60000});await writeFile(join(temporary,'config','server.json'),JSON.stringify(settings));
await writeFile(join(temporary,'config','send-monitor.json'),JSON.stringify({SourceIP:'127.0.0.1',DestinationIP:'127.0.0.2',InterfaceId:'',DestinationPort:0}));
const judgeId='1'.repeat(32),executeId='2'.repeat(32),manualId='3'.repeat(32);
await writeFile(join(temporary,'config','sequences.json'),JSON.stringify({SchemaVersion:1,Revision:'ui-fixture',DefaultTimeoutSeconds:30,NextJudgeNumber:2,NextExecuteNumber:3,Sequences:[
 {Id:judgeId,Code:'XLP0001',Name:'独立判读',Mode:'judge',SourceIP:'127.0.0.1',Rows:[{Row:2,Keyword:'TEXT',Description:'判读时基',Mark:'T0'},{Row:3,Condition:'TM.A1.value==99',Timeout:'30'}]},
 {Id:executeId,Code:'XLZ0001',Name:'启动判读并继续',Mode:'execute',SourceIP:'127.0.0.1',Rows:[{Row:2,Keyword:'TEXT',Description:'执行时基',Mark:'T0'},{Row:3,Keyword:'CALL',Command:'XLP0001',Expected:'.5'},{Row:4,Keyword:'PAUSE',Description:'CALL后继续执行至暂停'}]},
 {Id:manualId,Code:'XLZ0002',Name:'独立人工执行',Mode:'execute',SourceIP:'127.0.0.1',Rows:[{Row:2,Keyword:'PAUSE',Description:'仅暂停执行'}]}
]}));
const socket=net.createServer();await new Promise(r=>socket.listen(0,'127.0.0.1',r));const port=socket.address().port;await new Promise(r=>socket.close(r));
const server=spawn(resolve(process.argv[3]),['--local','--root',temporary,'--web-port',String(port)],{windowsHide:true,stdio:'ignore'}),base='http://127.0.0.1:'+port,checks=[],errors=[];let browser;
function check(ok,label){assert.ok(ok,label);checks.push(label);console.log('PASS',label);}
async function until(fn,label='wait'){for(let i=0;i<180;i++){if(await fn())return;await new Promise(r=>setTimeout(r,40));}throw Error('Timed out: '+label);}
try{
 await until(async()=>{try{return(await fetch(base+'/api/auth/me')).ok;}catch{return false;}});browser=await chromium.launch({headless:true,channel:'msedge'});const context=await browser.newContext({viewport:{width:1600,height:1100}}),page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base);await page.locator('#login-screen').waitFor({state:'visible'});await page.locator('#login-username').fill('instanceui');await page.locator('#login-name').fill('运行页签测试');await page.locator('#login-password').fill(randomBytes(18).toString('hex'));await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 await page.evaluate(()=>navigate('sequence-run',true));await until(()=>page.locator('#sequence-run-select option').count().then(n=>n===2));
 check(await page.locator('#sequence-launch-panel').isVisible()&&!await page.locator('#sequence-task-detail').isVisible(),'sequence run opens a dedicated launch page');
 check(await page.locator('#feature-tab-sequence-config').count()===1&&await page.locator('#feature-tab-sequence-run').count()===1&&await page.locator('#feature-tab-sequence-execute').count()===0,'sequence feature contains configuration and one unified launch entry');
 await page.locator('#listen-button').click();await until(()=>page.evaluate(()=>app.state?.mode==='live'),'listener');
 async function launch(id,name){await page.locator('#sequence-task-tabs button').filter({hasText:'启动页面'}).first().click();await page.locator('#sequence-launch-mode').selectOption(id===judgeId?'judge':'execute');await page.locator('#sequence-run-select').selectOption(id);await page.locator('#sequence-start').click();await page.locator('#sequence-start-dialog').waitFor({state:'visible'});await page.locator('#sequence-run-name').fill(name);await page.locator('#sequence-start-confirm').click();await until(()=>page.locator('#sequence-summary').textContent().then(t=>t.includes(name)),'new named run');return await page.evaluate(()=>document.querySelector('.sequence-task-tab.active').dataset.runId);}
 const first=await launch(manualId,'本次执行甲');await until(()=>page.locator('#sequence-resume').isEnabled(),'first paused');
 check(await page.locator('#sequence-task-detail').isVisible()&&!await page.locator('#sequence-launch-panel').isVisible()&&!!first,'manual start creates and selects a named run tab');
 const second=await launch(manualId,'本次执行乙');await until(()=>page.locator('#sequence-resume').isEnabled(),'second paused');
 check(first!==second&&await page.locator('.sequence-task-tab[data-run-id]').count()===2,'starting the same definition again creates a second separate tab');
 await page.locator(`.sequence-task-tab[data-run-id="${first}"]`).click();await until(()=>page.locator('#sequence-summary').textContent().then(t=>t.includes('本次执行甲')));await page.locator('#sequence-stop').click();await until(()=>page.locator('#sequence-stop').isDisabled());
 await page.locator(`.sequence-task-tab[data-run-id="${second}"]`).click();await until(()=>page.locator('#sequence-resume').isEnabled());
 check((await page.locator('#sequence-summary').textContent()).includes('本次执行乙'),'stopping the first run leaves the second paused and independently controllable');
 check((await page.locator('#sequence-export').getAttribute('href')).includes('runId='+second)&&!await page.locator('#sequence-history-scope').isVisible(),'selected tab results and CSV are restricted to that exact run');
 const judgment=await launch(judgeId,'手动判读');await until(()=>page.locator('#sequence-stop').isEnabled());
 const parent=await launch(executeId,'调用执行');await until(()=>page.locator('.sequence-task-tab.active').textContent().then(t=>t.includes('独立判读')),'CALL opens new tab');
 const child=await page.evaluate(()=>document.querySelector('.sequence-task-tab.active').dataset.runId);
 check(child!==judgment&&child!==parent&&await page.locator('.sequence-task-tab[data-run-id]').count()===5,'CALL adds a new independent judgment tab even while the same target is already running');
 check(context.pages().length===1,'every run remains a tab inside the same sequence webpage');
 check((await page.locator('#sequence-other-runtime').textContent()).includes('调用执行'),'CALL target displays its parent run name');
 await page.locator(`.sequence-task-tab[data-run-id="${parent}"]`).click();await until(()=>page.locator('#sequence-resume').isEnabled());check((await page.locator('#sequence-summary').textContent()).includes('调用执行'),'CALL parent continues independently and keeps its own run tab');
 await page.reload();await page.locator('#workspace').waitFor({state:'visible'});await until(()=>page.locator('.sequence-task-tab[data-run-id]').count().then(n=>n===5));await until(()=>page.locator('#sequence-summary').textContent().then(t=>t.includes('调用执行')));
 check(await page.locator('#sequence-resume').isEnabled(),'refresh restores named tabs, selected task and live controls without restarting runs');
 await page.locator(`.sequence-task-tab[data-run-id="${first}"]`).click();await until(()=>page.locator('#sequence-stop').isDisabled());await page.getByRole('button',{name:'关闭页签 本次执行甲',exact:true}).click();
 check(await page.locator(`.sequence-task-tab[data-run-id="${first}"]`).count()===0&&await page.locator('#sequence-launch-panel').isVisible(),'ended tab can be closed without deleting its run');
 const item=page.locator('#sequence-task-list tr').filter({hasText:'本次执行甲'});await item.getByRole('button',{name:'查看运行'}).click();await until(()=>page.locator('#sequence-summary').textContent().then(t=>t.includes('本次执行甲')));
 check(await page.locator(`.sequence-task-tab[data-run-id="${first}"]`).count()===1&&!await page.locator('#sequence-task-tabs .sequence-task-tab.active').evaluate(el=>el.textContent.includes('启动页面')),'closed task can be reopened from launch page with original results');
 check(!await page.getByRole('button',{name:'关闭页签 本次执行乙',exact:true}).count(),'running tabs have no misleading close control');
 await page.locator(`.sequence-task-tab[data-run-id="${child}"]`).click();await until(()=>page.locator('#sequence-summary').textContent().then(t=>t.includes('独立判读')));
 check((await page.locator('#sequence-diagnostic-export').getAttribute('href')).includes('runId='+child),'diagnostic export is bound to selected run rather than latest mode');
 await page.locator('#sequence-task-tabs button').filter({hasText:'启动页面'}).first().click();check(await page.locator('#sequence-history-scope').isVisible(),'launch page retains cross-run history search');
 await page.evaluate(async()=>{await api('control',{action:'stop'});});await until(()=>page.locator('#sequence-task-list').textContent().then(t=>!t.includes('已暂停')),'all tasks stop with listener');
 await page.locator('#view-sequence-run').screenshot({path:join(root,'test-output','sequence-run-instances','launch-page.png')});
 await page.locator(`.sequence-task-tab[data-run-id="${parent}"]`).click();await until(()=>page.locator('#sequence-stop').isDisabled());await page.locator('#view-sequence-run').screenshot({path:join(root,'test-output','sequence-run-instances','run-page.png')});
 assert.deepEqual(errors,[]);check(true,'launch and multiple task tabs produce no JavaScript errors');
 await writeFile(join(root,'test-output','sequence-run-instances','browser-report.json'),JSON.stringify({checks:checks.length,details:checks},null,2));console.log('All '+checks.length+' run tab UI checks passed.');
}finally{await browser?.close();server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));assert.ok(resolve(temporary).startsWith(join(root,'test-output')+'\\'));await rm(temporary,{recursive:true,force:true});}
