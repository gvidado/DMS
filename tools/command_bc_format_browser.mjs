import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {join,resolve} from 'node:path';

let input='';for await(const chunk of process.stdin)input+=chunk;
const config=JSON.parse(input),require=createRequire(join(resolve(process.argv[2]),'package.json'));
const {chromium}=require('playwright'),browser=await chromium.launch({headless:true,channel:'msedge'});
const errors=[];
try{
  const page=await browser.newPage({viewport:{width:1536,height:1080}});
  page.on('pageerror',error=>errors.push(error.message));
  await page.goto(config.base);
  await page.locator('#login-username').fill('formatqa');
  await page.locator('#login-password').fill(config.password);
  await page.locator('#login-submit').click();
  await page.locator('#workspace').waitFor({state:'visible'});
  await page.evaluate(()=>navigate('command-send',true));
  for(const item of config.samples){
    const row=page.locator(`#command-send-catalog tr[data-send-number="${item.number}"]`);
    await row.locator('td').nth(1).click();
    await page.waitForFunction(expected=>document.querySelector('#command-send-hex').value===expected&&
      !document.querySelector('#command-send-preview').disabled,item.hex);
    assert.equal(await row.getAttribute('aria-selected'),'true');
    assert.equal(await page.locator('#command-send-data-hex').inputValue(),item.dataHex);
    assert.equal(await page.locator('#command-send-destinations input:checked').count(),2);
  }
  assert.match(await page.locator('#view-command-send').innerText(),/B令格式测试版/);
  assert.match(await page.locator('.page-footer>span').first().textContent(),/0\.10\.14/);
  assert.deepEqual(errors,[]);
  await page.screenshot({path:config.screenshot,fullPage:true});
  console.log('PASS browser shows actual B/broadcast/injection/raw source, preserves data area and selection, and labels the test release');
}finally{await browser.close();}
