import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {resolve,join} from 'node:path';
let input='';for await(const chunk of process.stdin)input+=chunk;
const config=JSON.parse(input),require=createRequire(join(resolve(process.argv[2]),'package.json'));
const {chromium}=require('playwright'),browser=await chromium.launch({headless:true,channel:'msedge'});
const errors=[],databaseRequests=[];
try{
  const page=await browser.newPage({viewport:{width:1600,height:1080}});
  page.on('pageerror',error=>errors.push(error.message));page.on('request',r=>{if(new URL(r.url()).pathname.startsWith('/api/history/'))databaseRequests.push(r.url());});
  await page.goto(config.base);await page.locator('#login-username').fill('nodbqa');await page.locator('#login-password').fill(config.password);await page.locator('#login-submit').click();await page.locator('#workspace').waitFor({state:'visible'});
  const sections=await page.locator('.sidebar [data-section]').evaluateAll(items=>items.map(item=>item.dataset.section));
  assert.deepEqual(sections,['data','configuration','local','commands','sequences','accounts']);
  assert.equal(await page.locator('[data-tab-view="history"],[data-tab-view="history-config"],#view-history,#view-history-config').count(),0);
  assert.ok((await page.evaluate(()=>WorkspaceUI.url('command-send'))).includes('section=commands'));
  assert.ok((await page.evaluate(()=>WorkspaceUI.url('sequence-run'))).includes('section=sequences'));
  for(const view of ['settings','parameters','results','archives','sequence-run','command-config']){
    await page.evaluate(view=>navigate(view,true),view);await page.locator('#view-'+view).waitFor({state:'visible'});
  }
  await page.goto(config.base+'/?section=history&view=history');await page.locator('#view-home').waitFor({state:'visible'});
  assert.deepEqual(errors,[]);assert.deepEqual(databaseRequests,[]);
  console.log('PASS browser keeps six functional sections, account management last, no database UI/requests, and redirects retired history bookmarks to home');
}finally{await browser.close();}
