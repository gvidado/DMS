import assert from 'node:assert/strict';
import {createRequire} from 'node:module';import {resolve,dirname,join} from 'node:path';import {fileURLToPath} from 'node:url';
import {cp,rm,writeFile,mkdtemp,readFile} from 'node:fs/promises';import {spawn} from 'node:child_process';import {randomBytes} from 'node:crypto';import net from 'node:net';import dgram from 'node:dgram';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const temporary=await mkdtemp(join(root,'test-output','parameter-layout-ui-'));await cp(join(root,'config'),join(temporary,'config'),{recursive:true});await cp(join(root,'web'),join(temporary,'web'),{recursive:true});
for(const file of ['decoder.json','continuous-rules.json','tc-bindings.json','sequences.json','observed-commands.json','command-send.json'])await rm(join(temporary,'config',file),{force:true});
await writeFile(join(temporary,'config','parameters.json'),JSON.stringify(['A1','A2','B1'].map((code,i)=>({Id:'test/'+code,Code:code,Name:['加速度','工作状态','电源电压'][i],Group:'工仪全程',Offset:i*2,Length:2,Type:'uint',Scale:i===0?.01:1,Unit:i===0?'m/s²':'V',Supported:true}))));
const udp=dgram.createSocket('udp4');await new Promise(r=>udp.bind(0,'127.0.0.1',r));const udpPort=udp.address().port;await new Promise(r=>udp.close(r));
const settings=JSON.parse((await readFile(join(temporary,'config','server.json'),'utf8')).replace(/^\uFEFF/,''));Object.assign(settings,{BindAddress:'127.0.0.1',UdpPort:udpPort,SourceIPs:['127.0.0.1'],MappingConfirmed:true,MulticastGroup:'',MulticastInterface:'',DataTimeoutMs:1000});await writeFile(join(temporary,'config','server.json'),JSON.stringify(settings));
await writeFile(join(temporary,'config','send-monitor.json'),JSON.stringify({SourceIP:'127.0.0.1',DestinationIP:'127.0.0.2',InterfaceId:'',DestinationPort:0}));
const socket=net.createServer();await new Promise(r=>socket.listen(0,'127.0.0.1',r));const port=socket.address().port;await new Promise(r=>socket.close(r));
let server;function start(){server=spawn(resolve(process.argv[3]),['--local','--root',temporary,'--web-port',String(port)],{windowsHide:true,stdio:'ignore'});}start();
const base='http://127.0.0.1:'+port,checks=[],errors=[],sender=dgram.createSocket('udp4'),password=randomBytes(18).toString('hex');let browser;
function check(ok,label){assert.ok(ok,label);checks.push(label);console.log('PASS',label);}
async function until(fn,label='wait'){for(let i=0;i<250;i++){if(await fn())return;await new Promise(r=>setTimeout(r,40));}throw Error('Timed out: '+label);}
function packet(sequence,value){const b=Buffer.alloc(1030);b.writeUInt16BE(0x0802,0);b.writeUInt16BE(1023,4);b.writeUInt16BE(0x0808,6);b.writeUInt16BE(0xc000|sequence,8);b.writeUInt16BE(1017,10);b.set([0x30,3,25,0],12);b.writeUInt16BE(0x1004,18);b.writeUInt16BE(value,22);b.writeUInt16BE(3,24);b.writeUInt16BE(28,26);let a=0,c=0;for(let i=6;i<b.length-2;i++){a=(a+b[i])%255;c=(c+a)%255;}b[b.length-2]=(255-(a+c)%255)%255;b[b.length-1]=(255-(a+b[b.length-2])%255)%255;return b;}
async function send(seq,value){await new Promise((r,j)=>sender.send(packet(seq,value),udpPort,'127.0.0.1',e=>e?j(e):r()));}
async function stop(){server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));}
try{
 await until(async()=>{try{return(await fetch(base+'/api/auth/me')).ok;}catch{return false;}});
 browser=await chromium.launch({headless:true,channel:'msedge'});const context=await browser.newContext({viewport:{width:1600,height:1100}}),page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base);await page.locator('#login-screen').waitFor({state:'visible'});await page.locator('#login-username').fill('layoutqa');await page.locator('#login-name').fill('布局验证');await page.locator('#login-password').fill(password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 const original=await page.evaluate(()=>api('parameter-displays'));check(original.Screens[0].Boxes[0].Parameters.length===3,'legacy selected parameters migrate into the first screen without losing selection');
 const diskBefore=JSON.parse(await readFile(join(temporary,'data','accounts.json'),'utf8'));check(!diskBefore[0].DisplayLayout,'viewing the migration does not silently rewrite user accounts');
 await page.evaluate(()=>navigate('parameters',true));await page.locator('#pd-monitor-canvas .pd-box').waitFor();
 check(!await page.locator('.page-heading').isVisible()&&await page.locator('#listen-button').isVisible()&&await page.locator('#view-parameters h2').count()===0,'real-time page contains only parameter boxes, with the shared reception control in the top bar');
 const tabs=await page.locator('#pd-screen-tabs').boundingBox(),canvas=await page.locator('#pd-monitor-canvas').boundingBox();check(tabs.x+tabs.width<=canvas.x+1,'screen selector is at the left');
 await page.evaluate(()=>navigate('overview',true));check(await page.locator('#listen-button').isVisible()&&await page.locator('#metric-received').isVisible()&&await page.locator('#device-address').isVisible()&&await page.locator('#recent-events').isVisible(),'reception controls, metrics, data source and events remain available in overview');
 await page.evaluate(()=>navigate('parameter-displays',true));await page.locator('#pd-properties').click();await page.locator('[data-pd-property="style"]').click();await page.locator('#pd-columns').fill('1');await page.locator('#pd-columns').dispatchEvent('change');await page.locator('#pd-font').fill('22');await page.locator('#pd-font').dispatchEvent('change');
 check(await page.locator('.page-footer').evaluate(el=>el.getBoundingClientRect().top>document.getElementById('view-parameter-displays').getBoundingClientRect().top),'configuration canvas precedes the page footer');
 await page.locator('#pd-add-box').click();const second=page.locator('#pd-editor-canvas .pd-box').last();const secondId=await second.getAttribute('data-box-id');
 await page.locator('[data-pd-property="geometry"]').click();for(const [id,value] of [['pd-x','950'],['pd-y','50'],['pd-width','400'],['pd-height','300']]){await page.locator('#'+id).fill(value);await page.locator('#'+id).dispatchEvent('change');}
 await page.locator('[data-pd-property="parameters"]').click();await page.locator('#pd-select-parameters').click();await page.locator('#catalog-options input').nth(1).check();await page.locator('#save-selection').click();check(await second.locator('[data-parameter-id="test/A2"]').count()===1,'each box can select its own parameter set');
 check((await page.evaluate(()=>api('parameter-displays'))).Screens[0].Boxes.length===1,'draft edits do not modify saved layouts before Save');
 await page.locator('#pd-close-properties').click();const first=page.locator('#pd-editor-canvas .pd-box').first(),before=await first.boundingBox(),other=await second.boundingBox(),handle=await first.locator('.pd-dragbar').boundingBox();
 await page.mouse.move(handle.x+30,handle.y+15);await page.mouse.down();await page.mouse.move(handle.x+70,handle.y+45,{steps:6});await page.mouse.up();const after=await first.boundingBox();check(Math.abs(after.x-before.x-40)<2&&Math.abs(after.y-before.y-30)<2&&(await second.boundingBox()).x===other.x,'dragging moves only the selected box');
 const resize=await second.locator('.pd-resize').boundingBox();await page.mouse.move(resize.x+12,resize.y+12);await page.mouse.down();await page.mouse.move(resize.x-250,resize.y-250,{steps:6});await page.mouse.up();const minimum=await second.boundingBox();check(minimum.width===300&&minimum.height===180,'drag resizing enforces 300 by 180 pixel minimum');
 await page.locator('#pd-save').click();await until(()=>page.locator('#pd-save-state').textContent().then(t=>t==='配置已保存'));
 const saved=await page.evaluate(()=>api('parameter-displays'));check(saved.Screens[0].Boxes.length===2&&saved.Screens[0].Boxes[0].FontSize===22&&saved.Screens[0].Boxes[1].FontSize===18,'box layout and styles save independently');
 await page.locator('#pd-add-screen').click();await page.locator('#pd-screen-name').fill('姿态监视');await page.locator('#pd-screen-name').dispatchEvent('change');await page.locator('#pd-add-box').click();await page.locator('[data-pd-property="parameters"]').click();await page.locator('#pd-select-parameters').click();await page.locator('#catalog-options input').last().check();await page.locator('#save-selection').click();await page.locator('#pd-save').click();await until(()=>page.locator('#pd-save-state').textContent().then(t=>t==='配置已保存'));
 check((await page.evaluate(()=>api('selection'))).length===3,'live subscription covers the union of all screen parameters');
 await page.evaluate(()=>navigate('parameters',true));await page.locator('#pd-screen-tabs button').last().click();check(await page.locator('#pd-monitor-canvas .pd-box').count()===1&&await page.locator('#pd-monitor-canvas [data-parameter-id="test/B1"]').count()===1,'screen switching uses that screen\'s saved boxes');
 await page.reload();await page.locator('#workspace').waitFor({state:'visible'});check(await page.locator('#pd-screen-tabs button.active').textContent()==='姿态监视','browser refresh restores the current screen');
 await page.locator('#pd-screen-tabs button').first().click();await page.evaluate(()=>api('control',{action:'start'}));await send(1,979);await until(()=>page.locator('#pd-monitor-canvas [data-parameter-id="test/A1"] .pd-value').textContent().then(t=>t==='9.79'),'UDP live update');
 const size1=await page.locator('#pd-monitor-canvas .pd-box').first().boundingBox();await send(2,980);await until(()=>page.locator('#pd-monitor-canvas [data-parameter-id="test/A1"] .pd-value').textContent().then(t=>t==='9.8'));const size2=await page.locator('#pd-monitor-canvas .pd-box').first().boundingBox();check(size1.width===size2.width&&size1.height===size2.height,'actual UDP engineering values update without changing box size');
 check(/03\s?D4/i.test(await page.locator('#pd-monitor-canvas [data-parameter-id="test/A1"] .pd-hex').textContent()),'hex source remains enabled by default');
 await until(()=>page.locator('#pd-monitor-canvas [data-parameter-id="test/A1"] .status-tag').textContent().then(t=>t==='过期'));await send(3,979);await until(()=>page.locator('#pd-monitor-canvas [data-parameter-id="test/A1"] .status-tag').textContent().then(t=>t==='有效'));check(true,'stale quality recovers on fresh incoming telemetry');
 const clonePage=await context.newPage();clonePage.on('pageerror',e=>errors.push(e.message));await clonePage.goto(base+'/?section=data&view=parameters');await clonePage.locator('#workspace').waitFor({state:'visible'});await clonePage.locator('#pd-screen-tabs button').last().click();check(await page.locator('#pd-screen-tabs button.active').textContent()==='画面1','other pages may select a different screen without switching this page');
 await page.screenshot({path:join(root,'test-output','parameter-displays','monitor.png'),fullPage:true});
 await page.evaluate(()=>navigate('parameter-displays',true));await page.locator('#pd-edit-screen').selectOption(original.Screens[0].Id);await page.locator('#pd-screen-name').fill('本地草稿');await page.locator('#pd-screen-name').dispatchEvent('change');
 await clonePage.evaluate(async()=>{const c=await api('parameter-displays');c.Screens[0].Name='其他页面更新';await api('parameter-displays',{configuration:c});});await page.locator('#pd-save').click();await until(()=>page.locator('#pd-error').textContent().then(t=>t.includes('其他页面')));check(await page.locator('#pd-screen-name').inputValue()==='本地草稿','concurrent save conflict preserves the unsaved local draft');
 page.once('dialog',d=>d.accept());await page.locator('#pd-reload').click();await until(()=>page.locator('#pd-screen-name').inputValue().then(t=>t==='其他页面更新'));
 const invalid=await page.evaluate(async()=>{const c=await api('parameter-displays');c.Screens[0].Boxes[0].Width=1;const r=await fetch('/api/parameter-displays',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':app.user.csrf},body:JSON.stringify({configuration:c})});return{status:r.status,body:await r.json()};});check(invalid.status===400&&invalid.body.error.includes('300'),'server also rejects malformed minimum box sizes');
 check((await context.request.post(base+'/api/parameter-displays',{data:{configuration:original}})).status()===403,'layout writes require authenticated CSRF validation');
 await page.screenshot({path:join(root,'test-output','parameter-displays','configuration.png'),fullPage:true});await page.setViewportSize({width:390,height:844});await page.evaluate(()=>navigate('parameters',true));check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'narrow screens scroll within the layout viewport without page overflow');
 await page.evaluate(()=>api('control',{action:'stop'}));await clonePage.close();await stop();start();await until(async()=>{try{return(await fetch(base+'/api/auth/me')).ok;}catch{return false;}});await page.reload();await page.locator('#login-screen').waitFor({state:'visible'});await page.locator('#login-username').fill('layoutqa');await page.locator('#login-password').fill(password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});const restored=await page.evaluate(()=>api('parameter-displays'));check(restored.Screens.length===2&&restored.Screens[0].Name==='其他页面更新'&&restored.Screens[0].Boxes[1].Id===secondId,'saved layouts and parameter bindings survive service restart');
 const newUser=await page.evaluate(async()=>{await api('users',{username:'another',name:'独立账号',password:'display-test-password'});return true;});check(newUser,'layout support retains existing account management');
 const isolated=await browser.newContext(),otherPage=await isolated.newPage();await otherPage.goto(base);await otherPage.locator('#login-screen').waitFor({state:'visible'});await otherPage.locator('#login-username').fill('another');await otherPage.locator('#login-password').fill('display-test-password');await otherPage.locator('#login-submit').click();await otherPage.locator('#workspace').waitFor({state:'visible'});check((await otherPage.evaluate(()=>api('parameter-displays'))).Screens.length===1,'different accounts retain separate display layouts');await isolated.close();
 await page.setViewportSize({width:1600,height:1100});
 await page.evaluate(async()=>{
   const c=await api('parameter-displays'),b=c.Screens[0].Boxes[0];
   b.Parameters=app.catalog.map(p=>p.Id);b.Columns=2;b.RowHeight=54;b.Width=1400;b.Height=400;b.ShowHex=true;b.ShowUnit=true;b.ShowStatus=true;b.ShowTime=true;
   c.Screens[0].Boxes=[b];await api('parameter-displays',{configuration:c});await ParameterDisplayUI.load(true);navigate('parameters',true);
 });
 await page.locator('#pd-monitor-canvas tbody tr').first().waitFor();
 await page.locator('#pd-screen-tabs button').first().click();
 const fixedRows=await page.evaluate(()=>{
   const names=app.catalog.map(p=>p.Id);
   for(const id of names){const p=app.definitions.get(id);app.definitions.set(id,{...p,Name:'长参数名称'.repeat(100),Unit:'非常长的单位'.repeat(60)});}
   app.connected=true;app.state={...app.state,rows:names.map(id=>({id,value:'18446744073709551615'+'.0123456789'.repeat(50),hexSource:'AA '.repeat(1500),status:'有效',time:'2026-11-11 12:34:56.789'}))};
   ParameterDisplayUI.render();
   return{heights:[...document.querySelectorAll('#pd-monitor-canvas tbody tr')].map(r=>r.getBoundingClientRect().height),
     title:document.querySelector('#pd-monitor-canvas .pd-hex')?.title};
 });
 console.log('ROW HEIGHTS',JSON.stringify(fixedRows.heights));
 check(fixedRows.heights.length===3&&fixedRows.heights.every(h=>Math.abs(h-54)<1),'configured row heights stay identical across columns with long names, units, values and raw hex');
 check(fixedRows.title?.length>1000,'clipped source text remains fully available in its hover title');
 await page.screenshot({path:join(dirname(resolve(process.argv[3])),'fixed-row-heights.png'),fullPage:true});
 await page.evaluate(()=>navigate('judgments',true));await page.locator('#rule-new').click();
 await page.locator('#rule-parameter').selectOption('test/A1');await page.locator('#rule-name').fill('IF模板验证');
 await page.locator('#rule-template-if').click();const ifScript=await page.locator('#rule-script').inputValue();
 check(['InitRegion','StateRegion','ResultRegion','if(this.value > 0) then','RET=Normal;','RET=Error;'].every(part=>ifScript.includes(part)),'IF template supplies all three regions and both judgment outcomes');
 await page.locator('#rule-save').click();await page.locator('#rule-dialog').waitFor({state:'hidden'});
 check(await page.evaluate(async script=>(await api('continuous-rules')).rows.some(r=>r.rule.Script===script),ifScript),'inserted IF template passes actual server validation and saves');
 await page.evaluate(async()=>{navigate('parameter-displays',true);await ParameterDisplayUI.load();});
 await page.locator('#pd-edit-screen').selectOption({index:0});
 if(!await page.locator('#pd-inspector').isVisible())await page.locator('#pd-properties').click();
 await page.locator('[data-pd-property="style"]').click();
 await page.locator('#pd-font').fill('18');await page.locator('#pd-font').dispatchEvent('change');
 await page.locator('#pd-hex').check();await page.locator('#pd-row').fill('32');await page.locator('#pd-row').dispatchEvent('change');
 const rowMinimum=async()=>Number((await page.locator('#pd-row-hint').textContent()).match(/最小行高：(\d+)/)[1]);
 const smallFont=await rowMinimum();
 check(smallFont>32&&await page.locator('#pd-row-hint').evaluate(e=>e.classList.contains('pd-row-warning'))&&await page.locator('#pd-row').inputValue()==='32',
   'small configured rows show a minimum-height warning without silently changing the chosen height');
 await page.locator('#pd-font').fill('40');const largeFont=await rowMinimum();
 check(largeFont>smallFont,'minimum-height guidance updates immediately as font size increases');
 await page.locator('#pd-font').dispatchEvent('change');await page.locator('#pd-hex').uncheck();const withoutHex=await rowMinimum();
 check(withoutHex<largeFont,'disabling the hex source reduces the required height at a large font size');
 await page.locator('#pd-row-use-minimum').click();
 check(Number(await page.locator('#pd-row').inputValue())===withoutHex
   &&!await page.locator('#pd-row-hint').evaluate(e=>e.classList.contains('pd-row-warning')),
   'one click applies the recommended height and clears the clipping warning');
 const previewHeights=await page.locator('#pd-editor-canvas tbody tr').evaluateAll(rows=>rows.map(row=>row.getBoundingClientRect().height));
 console.log('MINIMUM HEIGHT CHECK',JSON.stringify({smallFont,largeFont,withoutHex,previewHeights}));
 check(previewHeights.length===3&&previewHeights.every(height=>Math.abs(height-withoutHex)<1),
   'the editing preview uses the chosen minimum uniformly for every row');
 await page.locator('#pd-save').click();await page.waitForFunction(()=>!document.getElementById('pd-save').disabled);
 check((await page.evaluate(()=>api('parameter-displays'))).Screens[0].Boxes[0].RowHeight===withoutHex,
   'recommended row height persists through the normal layout save API');
 await page.screenshot({path:join(dirname(resolve(process.argv[3])),'row-height-guidance.png'),fullPage:true});
 assert.deepEqual(errors,[]);check(true,'layout editing, reception and refresh have no JavaScript errors');
 await writeFile(join(dirname(resolve(process.argv[3])),'parameter-display-browser-report.json'),JSON.stringify({checks:checks.length,details:checks},null,2));
 await writeFile(join(root,'test-output','parameter-displays','browser-report.json'),JSON.stringify({checks:checks.length,details:checks},null,2));console.log('All '+checks.length+' parameter display checks passed.');
}finally{sender.close();await browser?.close();await stop();assert.ok(resolve(temporary).startsWith(join(root,'test-output')+'\\'));await rm(temporary,{recursive:true,force:true});}
