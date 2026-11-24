'use strict';
names['time-difference']=['时间差计算','计算时间差，或给一个时刻加减秒数。'];

window.TimeDifferenceUI=(()=>{
  const form=$('time-difference-form');
  const start=$('time-difference-start');
  const end=$('time-difference-end');
  const error=$('time-difference-error');
  const total=$('time-difference-seconds');
  const breakdown=$('time-difference-breakdown');
  const copy=$('time-difference-copy');
  const copyStatus=$('time-difference-copy-status');
  const offsetForm=$('time-offset-form');
  const offsetBase=$('time-offset-base');
  const offsetSeconds=$('time-offset-seconds');
  const offsetError=$('time-offset-error');
  const offsetResult=$('time-offset-result');
  const offsetCopy=$('time-offset-copy');
  const offsetCopyStatus=$('time-offset-copy-status');

  function timestamp(value) {
    const text=value.normalize('NFKC').trim();
    const chineseDate=/^(\d{4})年\s*(\d{1,2})月\s*(\d{1,2})日\s*(.+)$/.exec(text);
    const numericDate=/^(\d{4})([-/])(\d{1,2})\2(\d{1,2})(?:T|\s+)\s*(.+)$/.exec(text);
    const parts=chineseDate?.slice(1)||numericDate?.slice(1).filter((_,index)=>index!==1);
    if(!parts)return null;
    const [year,month,day]=parts.slice(0,3).map(Number);
    const time=parts[3].trim();
    const clock=/^(\d{1,2})\s*:\s*(\d{1,2})(?:\s*:\s*(\d{1,2})(?:\.(\d{1,3}))?)?$/.exec(time)
      || /^(\d{1,2})\s*[时点]\s*(\d{1,2})\s*分(?:\s*(\d{1,2})(?:\.(\d{1,3}))?\s*秒)?$/.exec(time);
    if(!clock||year===0||month<1||month>12||day<1||day>31)return null;
    const hours=Number(clock[1]),minutes=Number(clock[2]),seconds=Number(clock[3]||0);
    if(hours>23||minutes>59||seconds>59)return null;

    // Compare the civil timestamps directly across calendar days, independent of the browser timezone.
    const date=new Date(0);
    date.setUTCFullYear(year,month-1,day);
    if(date.getUTCMonth()!==month-1||date.getUTCDate()!==day)return null;
    return date.setUTCHours(hours,minutes,seconds,Number((clock[4]||'').padEnd(3,'0')));
  }

  function clearResult() {
    total.textContent='—';
    breakdown.textContent='';
    copy.disabled=true;
    copyStatus.textContent='';
    error.hidden=true;
    error.textContent='';
    for(const input of [start,end])input.removeAttribute('aria-invalid');
  }

  function reportError(input,message,field) {
    field.textContent=message;
    field.hidden=false;
    input.setAttribute('aria-invalid','true');
    input.focus();
  }

  function readTime(input,label,field) {
    const value=timestamp(input.value);
    if(value!==null)return value;
    const message=input.value.trim()?label+'格式有误或日期不存在。请核对日期和时间。':'请填写'+label+'。';
    reportError(input,message,field);
    return null;
  }

  function calculate() {
    clearResult();
    const from=readTime(start,'起始时间',error);
    if(from===null)return;
    const to=readTime(end,'结束时间',error);
    if(to===null)return;
    const difference=to-from;
    const whole=Math.floor(Math.abs(difference)/1000);
    const fraction=String(Math.abs(difference)%1000).padStart(3,'0').replace(/0+$/,'');
    const suffix=fraction?'.'+fraction:'';
    total.textContent=(difference<0?'-':'')+whole+suffix;
    const days=Math.floor(whole/86400),hours=Math.floor(whole%86400/3600),minutes=Math.floor(whole%3600/60);
    const duration=[days&&days+' 天',hours&&hours+' 小时',minutes&&minutes+' 分',whole%60+suffix+' 秒'].filter(Boolean).join(' ');
    breakdown.textContent=(difference<0?'结束时间早于起始时间 · ':'')+duration;
    copy.disabled=false;
  }

  function clearOffset() {
    offsetResult.textContent='—';
    offsetCopy.disabled=true;
    offsetCopyStatus.textContent='';
    offsetError.hidden=true;
    offsetError.textContent='';
    for(const input of [offsetBase,offsetSeconds])input.removeAttribute('aria-invalid');
  }

  function shiftTime() {
    clearOffset();
    const base=readTime(offsetBase,'基准时间',offsetError);
    if(base===null)return;
    const text=offsetSeconds.value.normalize('NFKC').trim().replace('−','-');
    const parts=/^([+-]?)(\d*)(?:\.(\d{1,3}))?$/.exec(text);
    if(!parts||!(parts[2]||parts[3])) {
      reportError(offsetSeconds,text?'请输入秒数，最多三位小数。':'请填写加减秒数。',offsetError);
      return;
    }
    const magnitude=Number(parts[2])*1000+Number((parts[3]||'').padEnd(3,'0'));
    const date=new Date(base+(parts[1]==='-'?-magnitude:magnitude));
    const year=date.getUTCFullYear();
    if(!Number.isSafeInteger(magnitude)||!(year>=1&&year<=9999)) {
      reportError(offsetSeconds,'计算结果超出 0001 至 9999 年。请核对秒数。',offsetError);
      return;
    }
    offsetResult.textContent=date.toISOString().replace('T',' ').replace(/(?:\.000)?Z$/,'');
    offsetCopy.disabled=false;
  }

  function copyFromSelection(node) {
    const selection=window.getSelection();
    const range=document.createRange();
    range.selectNodeContents(node);
    selection.removeAllRanges();
    selection.addRange(range);
    try {
      if(document.execCommand('copy')){selection.removeAllRanges();return true;}
    }catch{}
    return false;
  }
  async function copyValue(node,status) {
    const value=node.textContent;
    try {
      if(!navigator.clipboard)throw new Error();
      await navigator.clipboard.writeText(value);
      if(node.textContent===value)status.textContent='已复制';
    }catch{
      if(node.textContent===value)status.textContent=copyFromSelection(node)?'已复制':'请按 Ctrl+C 复制';
    }
  }

  form.addEventListener('submit',event=>{event.preventDefault();calculate();});
  form.addEventListener('input',clearResult);
  form.addEventListener('reset',clearResult);
  $('time-difference-swap').addEventListener('click',()=>{
    [start.value,end.value]=[end.value,start.value];
    if(start.value.trim()&&end.value.trim())calculate();
    else clearResult();
  });
  copy.addEventListener('click',()=>copyValue(total,copyStatus));
  offsetForm.addEventListener('submit',event=>{event.preventDefault();shiftTime();});
  offsetForm.addEventListener('input',clearOffset);
  offsetForm.addEventListener('reset',clearOffset);
  offsetCopy.addEventListener('click',()=>copyValue(offsetResult,offsetCopyStatus));
  return {parseTimestamp:timestamp,reset(){form.reset();offsetForm.reset();}};
})();
