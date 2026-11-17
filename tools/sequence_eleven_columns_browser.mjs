import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {writeFile} from 'node:fs/promises';
import {join,resolve} from 'node:path';
const options=JSON.parse(readFileSync(0,'utf8'));
const require=createRequire(join(resolve(process.argv[2]),'package.json'));
const {chromium}=require('playwright'),browser=await chromium.launch({headless:true,channel:'msedge'});
const checks=[],errors=[];
function check(value,label){assert.ok(value,label);checks.push(label);console.log('PASS',label);}
try{
  const page=await browser.newPage({viewport:{width:1700,height:1100}});
  page.on('pageerror',error=>errors.push(error.message));
  await page.goto(options.base);await page.locator('#login-username').fill('pluseqa');
  await page.locator('#login-password').fill(options.password);await page.locator('#login-submit').click();
  await page.locator('#workspace').waitFor({state:'visible'});
  await page.evaluate(()=>navigate('sequence-config',true));
  await page.locator('#sequence-mode').selectOption('execute');
  await page.locator('#sequence-file').setInputFiles(options.file);
  await page.locator('#sequence-convert').click();
  await page.waitForFunction(()=>document.querySelector('#sequence-preview-rows textarea[data-row="2"][data-field="Command"]')?.value==='BC2133');
  check(await page.locator('#sequence-preview-rows textarea[data-row="2"][data-field="Mark"]').inputValue()==='T0'
    &&await page.locator('#sequence-preview-rows textarea[data-row="2"][data-field="Expected"]').inputValue()==='F:2'
    &&await page.locator('#sequence-preview-rows textarea[data-row="2"][data-field="Timeout"]').inputValue()==='3',
    'browser preview shows source F/G/H in the standard mark and timing fields');
  check(await page.locator('#sequence-preview-rows textarea[data-row="6"][data-field="Keyword"]').inputValue()==='TEXT',
    'ignored-column command codes do not influence the visible conversion result');
  check(await page.locator('#sequence-preview-rows tr').count()===6,
    'browser preview excludes rows containing only ignored data');
  const downloadPromise=page.waitForEvent('download');
  await page.locator('#sequence-convert-export').click();const download=await downloadPromise;
  check(download.suggestedFilename()==='11列新序列表_判读格式匹配.xlsx',
    'browser download keeps the original filename and format-matching suffix');
  await download.saveAs(join(options.output,'sequence-eleven-browser.xlsx'));
  await page.screenshot({path:join(options.output,'sequence-eleven-preview.png'),fullPage:true});
  await page.locator('#sequence-import-cancel').click();
  check(await page.locator('#sequence-file').inputValue()===''
    &&(await page.evaluate(()=>api('sequence/config'))).definitions.length===0,
    'cancel clears the conversion preview without storing a sequence');
  check(errors.length===0,'eleven-column upload, preview, download and cancel have no browser errors');
  await writeFile(join(options.output,'sequence-eleven-browser-report.json'),
    JSON.stringify({checks:checks.length,details:checks},null,2));
}finally{await browser.close();}
