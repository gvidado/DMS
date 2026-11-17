import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join,resolve} from 'node:path';
import {readFileSync} from 'node:fs';
import {writeFile} from 'node:fs/promises';
const options=JSON.parse(readFileSync(0,'utf8'));
const require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright');
const browser=await chromium.launch({headless:true,channel:'msedge'}),errors=[],checks=[];
function check(ok,label){assert.ok(ok,label);checks.push(label);console.log('PASS',label);}
try{
 const page=await browser.newPage({viewport:{width:1500,height:1050}});page.setDefaultTimeout(60000);page.on('pageerror',e=>errors.push(e.message));
 await page.goto(options.base);await page.locator('#login-username').fill(options.username);await page.locator('#login-password').fill(options.password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 await page.evaluate(()=>navigate('judgments',true));await page.waitForFunction(()=>document.querySelector('#rule-summary').textContent.includes('500条规则'),null,{timeout:60000});
 check(!(await page.locator('#rule-error').textContent()).trim(),'large rule configuration opens in the browser without a JSON length error');
 const rows=page.locator('#rule-rows tr[data-rule-id]');check(await rows.count()>0,'configured rules are rendered through the existing paged table');
 await rows.first().getByRole('button',{name:'编辑',exact:true}).click();await page.locator('#rule-dialog').waitFor({state:'visible'});check((await page.locator('#rule-script').inputValue()).length===17511,'the rule editor receives the complete long script');await page.locator('#rule-editor-cancel').click();
 await page.locator('#rule-search').fill('TME.A499');await page.waitForFunction(()=>document.querySelectorAll('#rule-rows tr[data-rule-id]').length===1);check((await rows.first().textContent()).includes('TME.A499'),'the last rule remains searchable after loading the large response');
 check(errors.length===0,'large rule display and editor produce no browser script errors');
 await page.screenshot({path:join(options.output,'large-rules-page.png')});await writeFile(join(options.output,'large-rules-browser-report.json'),JSON.stringify({checks:checks.length,details:checks},null,2));
}finally{await browser.close();}
