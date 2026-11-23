import assert from 'node:assert/strict';
import vm from 'node:vm';
import {createRequire} from 'node:module';import {resolve,dirname,join} from 'node:path';import {fileURLToPath} from 'node:url';
import {cp,rm,writeFile,readFile,mkdtemp} from 'node:fs/promises';import {spawn} from 'node:child_process';import {randomBytes} from 'node:crypto';import net from 'node:net';import dgram from 'node:dgram';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const temporary=await mkdtemp(join(root,'test-output','browser-tabs-'));await cp(join(root,'config'),join(temporary,'config'),{recursive:true});await cp(join(root,'web'),join(temporary,'web'),{recursive:true});
for(const name of ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json'])await rm(join(temporary,'config',name),{force:true});
async function freePort(udp=false){const socket=udp?dgram.createSocket('udp4'):net.createServer();await new Promise(r=>udp?socket.bind(0,'127.0.0.1',r):socket.listen(0,'127.0.0.1',r));const port=socket.address().port;await new Promise(r=>socket.close(r));return port;}
const webPort=await freePort(),udpPort=await freePort(true),settings=JSON.parse((await readFile(join(temporary,'config/server.json'),'utf8')).replace(/^\uFEFF/,''));Object.assign(settings,{BindAddress:'127.0.0.1',UdpPort:udpPort,SourceIPs:['127.0.0.1'],MappingConfirmed:true,MulticastGroup:'',MulticastInterface:'',DataTimeoutMs:60000});await writeFile(join(temporary,'config/server.json'),JSON.stringify(settings));
await writeFile(join(temporary,'config/send-monitor.json'),JSON.stringify({SourceIP:'127.0.0.1',DestinationIP:'127.0.0.2',InterfaceId:'',DestinationPort:0}));
const server=spawn(resolve(process.argv[3]),['--local','--root',temporary,'--web-port',String(webPort)],{windowsHide:true,stdio:'ignore'}),base='http://127.0.0.1:'+webPort,password=randomBytes(18).toString('hex');let browser;
const checks=[];function check(value,label){assert.ok(value,label);checks.push(label);console.log('PASS',label);}
async function until(fn,label,timeout=12000){const end=Date.now()+timeout;while(Date.now()<end){if(await fn())return;await new Promise(r=>setTimeout(r,60));}throw new Error('Timed out: '+label);}
try{
 await until(async()=>{try{return(await fetch(base+'/api/auth/me')).ok;}catch{return false;}},'server');
 browser=await chromium.launch({headless:true,channel:'msedge'});const context=await browser.newContext({viewport:{width:1440,height:1050}}),home=await context.newPage(),errors=[];context.on('page',p=>p.on('pageerror',e=>errors.push(e.message)));home.on('pageerror',e=>errors.push(e.message));
 await context.request.post(base+'/api/auth/setup',{data:{username:'featuresqa',name:'主功能测试',password}});await home.goto(base);await home.locator('#login-username').fill('featuresqa');await home.locator('#login-password').fill(password);await home.locator('#login-submit').click();await home.locator('#workspace').waitFor({state:'visible'});
 check(await home.locator('.home-entry[data-open-section]').count()===6&&await home.locator('#feature-tabs').isHidden(),'home contains six main-function entries and no internal tab bar');
 check((await home.locator('.home-entry-card').last().getAttribute('data-group'))==='账号管理','account management remains the final main function');
 const groups=[['data','数据显示',['parameters','raw-spp','results','overview','archives']],['configuration','系统配置',['settings','decoders','parameter-displays','judgments']],['local','本地检验',['captures','network','time-difference']],['commands','指令控制',['command-panels','command-send','command-panel-config','command-config']],['sequences','序列任务',['sequence-config','sequence-run']],['accounts','账号管理',['accounts']]],pages=new Map();
 for(const [key,title,views] of groups){
  const popup=home.waitForEvent('popup');await home.locator(`[data-open-section="${key}"]`).click();const page=await popup;await page.locator('#workspace').waitFor({state:'visible'});await until(()=>page.evaluate(view=>app.view===view,views[0]),key+' initial view');pages.set(key,page);
  assert.equal(new URL(page.url()).searchParams.get('section'),key);assert.equal(await page.title(),title+' · 数据判读系统');assert.equal(await page.locator('.feature-tab-group:not(.hidden) .feature-tab').count(),views.length);assert.equal(await page.evaluate(()=>window.opener===null),true);
 }
 check(context.pages().length===7,'six primary functions open six independent browser tabs and share login');
 for(const [key,,views] of groups){const page=pages.get(key);const count=context.pages().length;for(const view of views){await page.locator(`[data-tab-view="${view}"]`).click();await until(()=>page.evaluate(view=>app.view===view,view),'local '+view);assert.equal(await page.locator('#view-'+view).isVisible(),true);assert.equal(await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--control-height').trim()),'38px');assert.equal(await page.locator(`[data-tab-view="${view}"]`).getAttribute('aria-selected'),'true');assert.equal(new URL(page.url()).searchParams.get('section'),key);}assert.equal(context.pages().length,count);}
 check(true,'all feature views load the shared controls and switch inside their parent page');
 const local=pages.get('local');
 await local.bringToFront();
 await local.locator('[data-tab-view="time-difference"]').click();
 const start=local.getByLabel('起始时间'),end=local.getByLabel('结束时间');
 const seconds=local.locator('#time-difference-seconds'),breakdown=local.locator('#time-difference-breakdown');
 async function difference(from,to,expected,detail){
  await start.fill(from);
  await end.fill(to);
  await end.press('Enter');
  assert.equal(await seconds.textContent(),expected);
  assert.equal(await breakdown.textContent(),detail);
 }
 await difference('2026-04-15 16：28：44','2026-04-17 12:15:49','157625','1 天 19 小时 47 分 5 秒');
 await difference('2026-04-15 16:28:44','2026-04-15 17:12:07','2603','43 分 23 秒');
 await difference('2029年9月10日12：8：54','2029年9月11日14：5：43','93409','1 天 1 小时 56 分 49 秒');
 check(true,'all three date and time examples produce the expected seconds');
 await local.getByRole('button',{name:'交换起止'}).click();
 assert.equal(await seconds.textContent(),'-93409');
 assert.match(await breakdown.textContent(),/结束时间早于起始时间/);
 await context.grantPermissions(['clipboard-read','clipboard-write'],{origin:base});
 await local.getByRole('button',{name:'复制总秒数'}).click();
 await until(()=>local.locator('#time-difference-copy-status').textContent().then(value=>value==='已复制'),'copy confirmation');
 assert.equal(await local.evaluate(()=>navigator.clipboard.readText()),'-93409');
 check(true,'swapping reverses the sign and the copy contains only the numeric result');
 await difference('2028/02/29 23:59:59.900','2028-03-01T00:00:00.025','0.125','0.125 秒');
 await difference('2026年1月1日8时5分','2026-01-01 08:05','0','0 秒');
 await start.fill('2026-02-29 00:00:00');
 await end.press('Enter');
 assert.equal(await seconds.textContent(),'—');
 assert.equal(await start.getAttribute('aria-invalid'),'true');
 assert.match(await local.locator('#time-difference-error').textContent(),/日期不存在/);
 assert(await local.getByRole('button',{name:'复制总秒数'}).isDisabled());
 await local.locator('#time-difference-form').getByRole('button',{name:'清空',exact:true}).click();
 assert.equal(await start.inputValue(),'');assert.equal(await end.inputValue(),'');
 assert(await local.locator('#time-difference-error').isHidden());
 const offset=local.locator('#time-offset-form');
 const baseTime=offset.getByLabel('基准时间'),offsetSeconds=offset.getByLabel('加减秒数');
 const shiftedTime=offset.locator('#time-offset-result'),offsetError=offset.locator('#time-offset-error');
 async function shift(from,by,expected){
  await baseTime.fill(from);await offsetSeconds.fill(by);await offsetSeconds.press('Enter');
  assert.equal(await shiftedTime.textContent(),expected);
 }
 await shift('2026-04-15 16:28:44','157625','2026-04-17 12:15:49');
 await shift('2026-04-15 17:12:07','-2603','2026-04-15 16:28:44');
 await shift('2026-12-31 23:59:59','＋１','2027-01-01 00:00:00');
 await shift('2028年3月1日00：00：00','−.125','2028-02-29 23:59:59.875');
 await shift('2028-02-29 23:59:59.875','0','2028-02-29 23:59:59.875');
 await offset.getByRole('button',{name:'复制时间'}).click();
 await until(()=>offset.locator('#time-offset-copy-status').textContent().then(value=>value==='已复制'),'time copy confirmation');
 assert.equal(await local.evaluate(()=>navigator.clipboard.readText()),'2028-02-29 23:59:59.875');
 check(true,'time shifting handles positive and negative offsets, year rollover, leap day, milliseconds and copying');
 await shift('9999-12-31 23:59:59','1','—');
 assert.match(await offsetError.textContent(),/超出/);
 await shift('2026-01-01 00:00:00','0.0001','—');
 assert.match(await offsetError.textContent(),/最多三位小数/);
 assert(await offset.getByRole('button',{name:'复制时间'}).isDisabled());
 await offset.getByRole('button',{name:'清空',exact:true}).click();
 assert.equal(await baseTime.inputValue(),'');assert.equal(await offsetSeconds.inputValue(),'');
 assert(await offsetError.isHidden());
 check(true,'invalid and out-of-range offsets are rejected and the offset form clears independently');
 await local.setViewportSize({width:390,height:820});
 check(await local.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'invalid dates, milliseconds, clear and narrow time calculator layout');
 await local.setViewportSize({width:1440,height:1050});
 const settings=pages.get('configuration');await settings.locator('[data-tab-view="settings"]').click();await settings.locator('#cfg-port').fill('45678');await settings.locator('[data-tab-view="decoders"]').click();await settings.locator('[data-tab-view="settings"]').click();check(await settings.locator('#cfg-port').inputValue()==='45678','switching internal tabs preserves unsaved listening settings');
 await settings.locator('[data-tab-view="judgments"]').click();const address=settings.url();await settings.reload();await settings.locator('#workspace').waitFor({state:'visible'});check(settings.url()===address&&await settings.locator('#view-judgments').isVisible(),'refresh restores both primary function and selected internal tab');
 const data=pages.get('data');await data.locator('[data-tab-view="overview"]').click();const count=context.pages().length;await data.locator('#open-unified-abnormals').click();await until(()=>data.evaluate(()=>app.view==='results'),'abnormal result shortcut');check(context.pages().length===count&&new URL(data.url()).hash==='#judgment-abnormal-panel','a shortcut within the same main function switches local tabs and scrolls to its panel');
 const popup=data.waitForEvent('popup');await data.locator('#judgment-open-config').click();const cross=await popup;await cross.locator('#workspace').waitFor({state:'visible'});check(new URL(cross.url()).searchParams.get('section')==='configuration'&&await data.locator('#view-results').isVisible(),'a shortcut across main functions opens a separate browser tab');await cross.close();
 const commands=pages.get('commands');await commands.locator('[data-tab-view="command-send"]').click();await commands.locator('#command-send-config-link').click();check(await commands.locator('#view-command-config').isVisible()&&context.pages().length===count,'instruction configuration shortcut stays within the instruction-control browser page');
 const sequence=pages.get('sequences');await sequence.locator('[data-tab-view="sequence-config"]').click();await sequence.locator('#sequence-go-run').click();check(await sequence.locator('#view-sequence-run').isVisible()&&context.pages().length===count,'sequence configuration/run shortcuts use local tabs');
 await data.locator('[data-tab-view="parameters"]').click();await data.locator('[data-tab-view="results"]').click();await data.goBack();await until(()=>data.evaluate(()=>app.view==='parameters'),'back navigation');await data.goForward();await until(()=>data.evaluate(()=>app.view==='results'),'forward navigation');check(true,'browser back and forward restore internal tab selections');
 await data.locator('[data-tab-view="parameters"]').click();await data.locator('[data-tab-view="parameters"]').focus();await data.keyboard.press('ArrowRight');check(await data.locator('#view-raw-spp').isVisible(),'arrow keys switch accessible internal tabs');
 const old=await context.newPage();await old.goto(base+'/?view=overview');await old.locator('#workspace').waitFor({state:'visible'});check(new URL(old.url()).searchParams.get('section')==='data'&&await old.locator('#view-overview').isVisible(),'existing function bookmarks remain usable and get a primary-function URL');await old.close();
 await data.screenshot({path:join(root,'test-output','six-primary-functions.png'),fullPage:true});
 await home.evaluate(async()=>{const adapter=(await api('send-monitor/interfaces')).interfaces.find(i=>i.loopback);await api('send-monitor/config',{SourceIPs:['127.0.0.1'],DestinationIPs:['127.0.0.2'],InterfaceId:adapter.id,DestinationPort:0});await api('control',{action:'start'});});
 await data.locator('[data-tab-view="overview"]').click();await data.locator('[data-tab-view="results"]').click();check(await home.evaluate(async()=>(await api('state')).mode)==='live','switching internal tabs leaves backend listening active');await home.evaluate(()=>api('control',{action:'stop'}));
 await home.evaluate(()=>api('users',{username:'feature_operator',name:'操作员',password:'Operator-test-123!'}));const restricted=await browser.newContext(),operator=await restricted.newPage();await operator.goto(base+'/?section=accounts');await operator.locator('#login-username').fill('feature_operator');await operator.locator('#login-password').fill('Operator-test-123!');await operator.locator('#login-submit').click();await operator.locator('#workspace').waitFor({state:'visible'});check(await operator.locator('[data-open-section="accounts"]').isHidden()&&await operator.locator('#feature-tabs').isHidden(),'non-admin account links remain protected');await restricted.close();
 await home.locator('#logout').click();for(const page of pages.values())await until(()=>page.locator('#login-screen').isVisible(),'shared logout');check(true,'logout still applies to all six function pages');assert.deepEqual(errors,[]);console.log('All '+checks.length+' two-level navigation checks passed.');
}finally{await browser?.close();server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));const absolute=resolve(temporary);assert.ok(absolute.startsWith(join(root,'test-output')+'\\'));await rm(temporary,{recursive:true,force:true});}
