import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {resolve,dirname,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {cp,rm,writeFile,readFile,readdir,mkdtemp} from 'node:fs/promises';
import {spawn,execFileSync} from 'node:child_process';
import {randomBytes,createHash} from 'node:crypto';
import net from 'node:net';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const clearMode=process.argv.includes('--clear'),sppReset=clearMode?'spp-clear':'spp-cancel';
const require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const temporary=await mkdtemp(join(root,'test-output','excel-cancel-'));
await cp(join(root,'config'),join(temporary,'config'),{recursive:true});await cp(join(root,'web'),join(temporary,'web'),{recursive:true});
for(const name of ['decoder.json','continuous-rules.json','tc-bindings.json','observed-commands.json','sequences.json','command-send.json'])await rm(join(temporary,'config',name),{force:true});
execFileSync(resolve(process.argv[4]),['-c',String.raw`
import sys
from pathlib import Path
from catalog_import_test import workbook
from continuous_rules_test import workbook as rules
from sequence_test import xlsx
p=Path(sys.argv[1])
(p/'telemetry.xlsx').write_bytes(workbook([('参数表',[('A1','参数一',0,1,'无符号整数',1)])]))
(p/'rules.xlsx').write_bytes(rules([[1,'TME.A1','参数一','一般','否','RET=Normal;']]))
(p/'sequence.xlsx').write_bytes(xlsx([('序列测试',[['TEXT','说明','','','','',''],['','检查','','TME.A1.value>0','','','2']])]))
(p/'commands.xlsx').write_bytes(xlsx([('B指令',[['指令代号','指令名称','遥控包编码'],['BC3383','测试B指令','1A08C000001130020A0000000000010800010D370D370736']])]))
fields=[('版本',3,0),('类型',1,0),('副导头',1,1),('APID',11,8),('序列标志',2,3),('序号',14,0),('长度',16,1017),('PUS',4,3),('标志',4,0),('业务类型',8,3),('业务子类型',8,25),('计数',8,0),('空闲',5,0),('目的APID',11,0),('源地址',16,4100),('目的地址',16,0)]
(p/'spp.xlsx').write_bytes(xlsx([('测试SPP',[(name,str(bits)+'bit','0x'+format(value,'X')) for name,bits,value in fields])]))
`,temporary],{cwd:join(root,'tools'),windowsHide:true});
const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const port=listener.address().port;await new Promise(r=>listener.close(r));
const server=spawn(resolve(process.argv[3]),['--local','--root',temporary,'--web-port',String(port)],{windowsHide:true,stdio:'ignore'});
const base='http://127.0.0.1:'+port,checks=[],errors=[];let browser;
function check(ok,label){assert.ok(ok,label);checks.push(label);console.log('PASS',label);}
async function until(fn,label='wait'){for(let i=0;i<150;i++){if(await fn())return;await new Promise(r=>setTimeout(r,40));}throw Error('Timed out: '+label);}
async function hashes(){const result={};for(const name of (await readdir(join(temporary,'config'))).sort())if(name.endsWith('.json'))result[name]=createHash('sha256').update(await readFile(join(temporary,'config',name))).digest('hex');return result;}
try{
 await until(async()=>{try{return(await fetch(base+'/api/auth/me')).ok;}catch{return false;}});
 browser=await chromium.launch({headless:true,channel:'msedge'});const page=await browser.newPage({viewport:{width:1600,height:1200}});page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base);await page.locator('#login-screen').waitFor({state:'visible'});await page.locator('#login-username').fill('excelcancel');await page.locator('#login-name').fill('取消检查');await page.locator('#login-password').fill(randomBytes(18).toString('hex'));await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 async function view(name){await page.evaluate(v=>navigate(v,true),name);await until(()=>page.evaluate(v=>app.view===v,name),name+' navigation');}
 await view('decoders');await until(()=>page.locator('#decoder-catalog-count').textContent().then(t=>!t.includes('尚未读取')),'decoder ready');
 await page.locator('#decoder-file').setInputFiles(join(temporary,'telemetry.xlsx'));await page.locator('#decoder-preview-button').click();await page.locator('#decoder-preview').waitFor({state:'visible'});await page.locator('#apply-preview').click();await page.locator('#decoder-preview').waitFor({state:'hidden'});
 assert.ok(await page.evaluate(async()=>(await api('decoders')).configuration.Catalog.Parameters.some(p=>p.Code==='A1')),'baseline telemetry A1 saved');
 await page.locator('#new-packet').click();await page.locator('#pkt-source').fill(await page.evaluate(()=>app.config.SourceIPs[0]));await page.locator('#pkt-outer-source').fill('0x0802');await page.locator('#pkt-groups input[value="参数表"]').check();await page.locator('#save-packet').click();await page.locator('#packet-dialog').waitFor({state:'hidden'});
 await view('judgments');await until(()=>page.locator('#rule-summary').textContent().then(t=>!!t));await view('sequence-config');await until(()=>page.locator('#sequence-source option').count().then(n=>n>1));await view('command-config');await until(()=>page.locator('#command-catalog-summary').textContent().then(t=>!!t));
 const before=await hashes();
 const cases=[
  {name:'遥测参数表',view:'decoders',input:'decoder-file',read:'decoder-preview-button',cancel:'cancel-decoder-import',panel:'decoder-preview',file:'telemetry.xlsx',url:'**/api/decoders/preview?*',revoke:'decoders/preview/export?token='},
  {name:'SPP批量',view:'decoders',input:'spp-batch-file',read:'spp-batch-read',cancel:'spp-batch-cancel',panel:'spp-batch-preview',file:'spp.xlsx',url:'**/api/decoders/spp/batch/preview?*',revoke:'decoders/preview/export?token='},
  {name:'常态化规则',view:'judgments',input:'rule-file',read:'rule-read',cancel:'rule-import-cancel',panel:'rule-preview',file:'rules.xlsx',url:'**/api/continuous-rules/preview?*',revoke:'continuous-rules/errors?token='},
  {name:'指令Excel',view:'command-config',input:'command-csv-file',read:'command-csv-read',cancel:'command-csv-cancel',panel:'command-csv-preview',file:'commands.xlsx',url:'**/api/send-monitor/commands/preview?*',revoke:'send-monitor/commands/apply'},
  {name:'序列读取',view:'sequence-config',input:'sequence-file',read:'sequence-read',cancel:'sequence-import-cancel',panel:'sequence-import-sheets',file:'sequence.xlsx',url:'**/api/sequence/preview?*',sequence:true,revoke:'sequence/recheck'},
  {name:'序列转换',view:'sequence-config',input:'sequence-file',read:'sequence-convert',cancel:'sequence-import-cancel',panel:'sequence-import-sheets',file:'sequence.xlsx',url:'**/api/sequence/preview?*',sequence:true,revoke:'sequence/recheck'},
 ];
 if(clearMode){
  const clearIds={'decoder-file':'decoder-import-clear','spp-batch-file':'spp-batch-clear','rule-file':'rule-import-clear','command-csv-file':'command-csv-clear','sequence-file':'sequence-import-clear'};
  const errorIds={'decoder-file':'decoder-import-error','spp-batch-file':'spp-batch-error','rule-file':'rule-import-error','command-csv-file':'command-csv-error','sequence-file':'sequence-import-error'};
  for(const item of cases){item.cancel=clearIds[item.input];item.error=errorIds[item.input];}
  const inventory=await page.locator('input[type="file"]').evaluateAll(inputs=>inputs.filter(input=>input.accept.includes('.xlsx')).map(input=>({id:input.id,buttons:[...document.querySelectorAll('button[data-excel-input]')].filter(button=>button.dataset.excelInput===input.id).map(button=>({text:button.textContent,type:button.type}))})));
  assert.equal(inventory.length,6);assert.ok(inventory.every(item=>item.buttons.length===1&&item.buttons[0].text==='清空'&&item.buttons[0].type==='button'));check(true,'所有Excel文件选择入口各有一个独立清空按钮，保留原取消按钮');
 }
 async function hasPreview(item){return item.sequence?(await page.locator('#'+item.panel).textContent()).includes('测试') : await page.locator('#'+item.panel).isVisible();}
 async function revoked(item,token){const response=await page.evaluate(async ({path,token})=>{const post=!path.includes('?');return(await fetch('/api/'+path+(post?'':token),{method:post?'POST':'GET',headers:post?{'Content-Type':'application/json','X-CSRF-Token':app.user.csrf}:{},body:post?JSON.stringify({token,id:'missing'}):undefined})).status;},{path:item.revoke,token});return response===400;}
 for(const item of cases){
  await view(item.view);if(item.view==='command-config')await page.getByText('从Excel导入B指令 / 广播令，或导入指令CSV',{exact:true}).evaluate(e=>e.parentElement.open=true);
  if(clearMode){await page.locator('#'+item.cancel).click();await page.locator('#'+item.read).click();await until(()=>page.locator('#'+item.error).textContent().then(Boolean));await page.locator('#'+item.cancel).click();assert.equal(await page.locator('#'+item.error).textContent(),'');check(true,item.name+'未选择文件时也可清空读取错误');}
  await page.locator('#'+item.input).setInputFiles(join(temporary,item.file));check(await page.locator('#'+item.cancel).isEnabled(),item.name+'选文件后可取消');await page.locator('#'+item.cancel).click();check(await page.locator('#'+item.input).inputValue()==='',item.name+'取消清除文件');
  let results=[],release=null,held=false,holdNext=false,done=false;
  await page.route(item.url,async route=>{const response=await route.fetch();const data=await response.json();results.push(data);if(holdNext){holdNext=false;held=true;await new Promise(r=>release=r);}await route.fulfill({response});done=true;});
  await page.locator('#'+item.input).setInputFiles(join(temporary,item.file));await page.locator('#'+item.read).click();await until(()=>results.length,item.name+' response');assert.ok(!results.at(-1).error,JSON.stringify(results.at(-1)));await until(()=>hasPreview(item),item.name+'preview');check(!results.at(-1).error,item.name+'真实Excel读取成功');
  const parsed=results.at(-1);if(item.name==='常态化规则')assert.equal(parsed.accepted,1,JSON.stringify(parsed.errors));if(item.name==='指令Excel')assert.equal(parsed.canApply,true,JSON.stringify(parsed.errors));if(item.sequence)assert.equal(parsed.definitions[0].errors.length,0,JSON.stringify(parsed.definitions[0].errors));
  const token=parsed.token;await page.locator('#'+item.cancel).click();await until(()=>revoked(item,token),item.name+' token revoked');check(!await hasPreview(item),item.name+'预览取消且服务端令牌失效');
  if(clearMode&&item.sequence){assert.equal(await page.locator('#sequence-conversion-report').textContent(),'');assert.equal(await page.locator('#sequence-preview-name').inputValue(),'');assert.ok(await page.locator('#sequence-preview-name').isDisabled());assert.ok(await page.locator('#sequence-recheck').isDisabled());assert.ok(await page.locator('#sequence-convert-export').isDisabled());assert.match(await page.locator('#sequence-preview-page').textContent(),/1 \/ 1 · 0项/);check(true,item.name+'清空名称、错误、转换报告和分页，并禁用旧预览操作');}
  results=[];holdNext=true;done=false;await page.locator('#'+item.input).setInputFiles(join(temporary,item.file));await page.locator('#'+item.read).click();await until(()=>held,item.name+' held');check(await page.locator('#'+item.cancel).isEnabled(),item.name+'读取中可取消');await page.locator('#'+item.cancel).click();
  await page.locator('#'+item.input).setInputFiles(join(temporary,item.file));await page.locator('#'+item.read).click();await until(()=>results.length===2&&hasPreview(item),item.name+' new preview');const latest=results[1].token;done=false;release();await until(()=>done);await until(()=>revoked(item,results[0].token),item.name+' stale token');check(await hasPreview(item),item.name+'迟到结果不覆盖新读取');
  await page.locator('#'+item.cancel).click();await until(()=>revoked(item,latest));await page.unroute(item.url);
 }
 await view('decoders');await page.locator('#new-packet').click();await page.locator('#pkt-apid').fill('0x123');await page.locator('#pkt-payload').fill('22');const draft=await page.locator('#pkt-apid').inputValue();
 await page.locator('#spp-file').setInputFiles(join(temporary,'spp.xlsx'));await page.locator('#spp-read').click();await page.locator('#spp-result').waitFor({state:'visible'});check(await page.locator('#pkt-apid').inputValue()==='0x8','SPP编辑读取实际填入字段');
 if(clearMode){await page.locator('#pkt-apid').fill('0x456');await page.locator('#spp-read').click();await until(()=>page.locator('#pkt-apid').inputValue().then(value=>value==='0x8'));}
 await page.locator('#'+sppReset).click();check(await page.locator('#pkt-apid').inputValue()===draft&&await page.locator('#pkt-payload').inputValue()==='22'&&await page.locator('#spp-file').inputValue()==='','SPP清理恢复首次读取前字段');
 let releaseSingle,ready=false,doneSingle=false;await page.route('**/api/decoders/spp/preview?*',async route=>{const response=await route.fetch();ready=true;await new Promise(r=>releaseSingle=r);await route.fulfill({response});doneSingle=true;});await page.locator('#spp-file').setInputFiles(join(temporary,'spp.xlsx'));await page.locator('#spp-read').click();await until(()=>ready);await page.locator('#'+sppReset).click();releaseSingle();await until(()=>doneSingle);await page.waitForTimeout(120);check(await page.locator('#pkt-apid').inputValue()===draft&&await page.locator('#spp-result').isHidden(),'SPP编辑清理后迟到结果不回填');await page.unroute('**/api/decoders/spp/preview?*');await page.locator('#cancel-packet-dialog').click();
 // Failed reads must also be dismissible, then allow a new selection.
 for(const item of cases.filter(i=>i.name!=='序列转换')){
  await view(item.view);await page.locator('#'+item.input).setInputFiles({name:'bad.xlsx',mimeType:'application/octet-stream',buffer:Buffer.from('invalid workbook')});await page.locator('#'+item.read).click();await until(()=>page.locator('#'+item.read).isEnabled());check(await page.locator('#'+item.cancel).isEnabled(),item.name+'失败后仍可取消');await page.locator('#'+item.cancel).click();check(await page.locator('#'+item.input).inputValue()==='',item.name+'失败后可清理并重选');
 }
 assert.deepEqual(await hashes(),before);check(true,'全部取消操作均未改动已保存配置文件');
 for(const item of cases.filter(i=>['SPP批量','常态化规则','指令Excel','序列读取'].includes(i.name))){
  await view(item.view);await page.locator('#'+item.input).setInputFiles(join(temporary,item.file));await page.locator('#'+item.read).click();await until(()=>hasPreview(item));
  const apply={SPP批量:'spp-batch-apply',常态化规则:'rule-add',指令Excel:'command-csv-apply',序列读取:'sequence-apply'}[item.name];await until(()=>page.locator('#'+apply).isEnabled(),item.name+' apply ready');
  let releaseSave,saveHeld=false;const saveRoute='**/api/'+{SPP批量:'decoders/apply',常态化规则:'continuous-rules/apply',指令Excel:'send-monitor/commands/apply',序列读取:'sequence/apply'}[item.name];
  if(clearMode)await page.route(saveRoute,async route=>{saveHeld=true;await new Promise(resolve=>releaseSave=resolve);await route.continue();});
  await page.locator('#'+apply).click();
  if(clearMode){await until(()=>saveHeld);check(await page.locator('#'+item.cancel).isDisabled(),item.name+'提交保存时禁用清空');releaseSave();}
  if(item.sequence){await until(()=>page.locator('#sequence-read').isEnabled());const saved=await page.evaluate(async()=>(await api('sequence/config')).definitions);assert.equal(saved.length,1,await page.locator('#sequence-config-error').textContent());}else await until(async()=>!await hasPreview(item),item.name+' saved');check(true,item.name+'清理后重新读取仍可正常保存');if(clearMode)await page.unroute(saveRoute);
 }
 assert.deepEqual(errors,[]);check(true,'所有界面无脚本错误');
 await writeFile(join(root,'test-output',clearMode?'excel-clear-report.json':'excel-cancel-report.json'),JSON.stringify({mode:clearMode?'clear':'cancel',checks:checks.length,details:checks},null,2));console.log('All '+checks.length+' Excel '+(clearMode?'clear':'cancellation')+' checks passed.');
}finally{await browser?.close();server.kill();await new Promise(r=>server.exitCode!==null?r():server.once('exit',r));assert.ok(resolve(temporary).startsWith(join(root,'test-output')+'\\'));await rm(temporary,{recursive:true,force:true});}
