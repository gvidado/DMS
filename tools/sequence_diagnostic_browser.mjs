import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {resolve,join} from 'node:path';
import {readFile} from 'node:fs/promises';
const require=createRequire(join(resolve(process.argv[2]),'package.json')),{chromium}=require('playwright'),settings=JSON.parse(await readFile(process.argv[3],'utf8'));
const browser=await chromium.launch({headless:true,channel:'msedge'});
try{
 const context=await browser.newContext({viewport:{width:1600,height:1080}}),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(settings.base+'/?section=sequences&view=sequence-execute');await page.locator('#login-username').fill(settings.username);await page.locator('#login-password').fill(settings.password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
 await page.waitForFunction(()=>document.querySelector('#sequence-run-rows').textContent.includes('最近判读'));
 assert.ok((await page.locator('#sequence-run-rows').textContent()).includes('条件尚未满足'));
 const target=new URL(await page.locator('#sequence-diagnostic-export').getAttribute('href'),settings.base);assert.equal(target.searchParams.get('id'),settings.id);assert.equal(target.searchParams.get('mode'),'execute');
 const [download]=await Promise.all([page.waitForEvent('download'),page.locator('#sequence-diagnostic-export').click()]);assert.equal(download.suggestedFilename(),'sequence-diagnostic.json');await download.saveAs(settings.output);
 const report=JSON.parse(await readFile(settings.output,'utf8'));assert.equal(report.runtimeMatchesSelection,true);assert.ok(report.engineBuild);assert.ok(report.rows[0].evaluationCount>=1);
 const parameter=report.rows[0].parameters[0];assert.equal(parameter.sourceIP,'127.0.0.1');assert.equal(parameter.latestDisplayedSource,'127.0.0.2');assert.equal(parameter.displaySourceMatchesBinding,false);assert.equal(report.rows[0].lastEvaluationResult,'不满足');
 assert.deepEqual(errors,[]);console.log('PASS live evaluation timestamp/count and actual authenticated JSON download with bound-source diagnosis');
}finally{await browser.close();}
