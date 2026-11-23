'use strict';
window.WorkspaceUI = (() => {
  names.home = ['功能首页', '选择功能入口，开始数据监视、系统配置或本地检验。'];
  names.judgments = ['判读配置', '管理常态化判读规则、Excel批量导入及TC指令代号绑定。'];
  const groups = [
    {name:'数据显示', icon:'▦', description:'查看实时参数、判读结果与接收情况。', items:[
      ['parameters','实时参数','查看所选参数的当前工程值与数据状态。','▦'],
      ['raw-spp','SPP原包监视','独立查看选中SPP包的完整源码，每包保留最近200条。','◇'],
      ['results','判读显示','实时查看参数是否通过，按重要程度和状态筛选。','✓'],
      ['overview','接收概览','查看SPP计数、发送监视、异常与告警记录。','◉'],
      ['archives','判读存档','按日期、参数或序列名称检索存档并导出。','▤']]},
    {name:'系统配置', icon:'⚙', description:'设置数据入口、解码方式与判读规则。', items:[
      ['settings','监听配置','设置UDP端口、组播、来源与数据超时。','⚙'],
      ['decoders','解码配置','导入遥测表，配置需要识别和解码的SPP包。','◇'],
      ['parameter-displays','参数显示配置','管理显示画面，配置多个参数框的位置、尺寸与显示内容。','▦'],
      ['judgments','判读配置','常态化规则、Excel导入、TC指令代号绑定。','≡']]},
    {name:'本地检验', icon:'⇄', description:'检验抓包、模拟收发或计算两个时刻的间隔。', items:[
      ['captures','文件检验','检查pcapng报文，查看统计与完整异常原文。','▤'],
      ['network','回放模拟','按抓包时间进行UDP回放，验证真实收发。','⇄'],
      ['time-difference','时间差计算','计算两个时刻的间隔，得到总秒数。','◷']]},
    {name:'账号管理', icon:'♙', description:'管理系统账号与操作员访问。', items:[
      ['accounts','账号管理','创建操作员账号、查看账号和重置密码。','♙']]},
    {name:'指令控制', icon:'➤', description:'发送库内指令，配置发送端口、监视范围和指令库。', items:[
      ['command-panels','指令面板','切换画面，点击自由排布的按钮发送库内指令。','▦'],
      ['command-send','指令发送','选择指令和目的IP，按固定来源端口发送完整UDP源码。','➤'],
      ['command-panel-config','面板配置','自由排布按钮，配置文字、尺寸、颜色并关联指令。','▦'],
      ['command-config','指令配置','发送端口对应表、发送监视配置、指令库与文件导入。','≡']]},
    {name:'序列任务', icon:'≋', description:'按Excel执行串行与并行步骤，独立控制指令发送和判读。', items:[
      ['sequence-config','序列配置','导入七列或十一列Excel，检查参数判据、指令及并行结构。','≡'],
      ['sequence-run','序列运行','启动执行或判读任务，每次启动生成独立的运行页签。','▷']]}
  ];
  // Keep account administration last when more feature groups are added.
  const accountGroup=groups.findIndex(group=>group.items.some(([key])=>key==='accounts'));
  if(accountGroup>=0)groups.push(...groups.splice(accountGroup,1));
  const sectionKeys=['data','configuration','local','commands','sequences','accounts'];
  groups.forEach((group,index)=>{group.key=sectionKeys[index];});
  const groupOf=view=>groups.find(group=>group.items.some(([key])=>key===view));
  function url(view,anchor=''){
    const value=new URL('/',location.href),group=groupOf(view);
    if(group){value.searchParams.set('section',group.key);value.searchParams.set('view',view);}if(anchor)value.hash=anchor;return value.href;
  }
  function initialView(){
    const query=new URL(location.href).searchParams,section=groups.find(group=>group.key===query.get('section'));
    const requested=query.get('view')||(section?section.items[0][0]:'home'),view=requested==='sequence-execute'?'sequence-run':requested;
    return Object.prototype.hasOwnProperty.call(names,view)&&(view!=='accounts'||app.user?.admin)?view:'home';
  }
  function open(view,anchor=''){
    if(!app.user||!Object.prototype.hasOwnProperty.call(names,view)||(view==='accounts'&&!app.user.admin))return false;
    if(view===app.view || (view!=='home' && groupOf(view)===groupOf(app.view))){
      const target=url(view,anchor);if(location.href!==target)history.pushState({view},'',target);return navigate(view,true);
    }
    window.open(url(view,anchor),'_blank','noopener');return false;
  }
  function link(view,cls,internal=false){
    const item=element('a',cls);item.href=url(view);
    if(!internal){item.target='_blank';item.rel='noopener';}
    item.addEventListener('click',event=>{
      if(!app.user||(view==='accounts'&&!app.user.admin)){event.preventDefault();return;}
      if(event.ctrlKey||event.metaKey||event.shiftKey||event.altKey||event.button!==0)return;
      if(internal || (view!=='home'&&groupOf(view)===groupOf(app.view))){event.preventDefault();open(view);}
    });return item;
  }
  const sidebar=document.querySelector('.sidebar'),main=document.querySelector('.main'),topbar=document.querySelector('.topbar');
  const leading=element('div','topbar-leading'),toggle=element('button','sidebar-toggle');toggle.id='sidebar-toggle';toggle.type='button';toggle.setAttribute('aria-controls','app-sidebar');toggle.setAttribute('aria-expanded','false');toggle.setAttribute('aria-label','展开导航');toggle.title='展开导航';
  const toggleIcon=element('span','sidebar-toggle-icon','☰');toggleIcon.setAttribute('aria-hidden','true');toggle.append(toggleIcon,element('span','','导航'));
  leading.append(toggle,topbar.querySelector('.breadcrumb'));topbar.prepend(leading);
  const close=element('button','sidebar-close','×');close.id='sidebar-close';close.type='button';close.setAttribute('aria-label','收起导航');close.title='收起导航';sidebar.prepend(close);
  const backdrop=element('div','sidebar-backdrop');backdrop.id='sidebar-backdrop';backdrop.hidden=true;backdrop.setAttribute('aria-hidden','true');document.querySelector('#workspace').append(backdrop);
  function closeNavigation(){
    const wasOpen=document.body.classList.contains('sidebar-drawer-open');document.body.classList.remove('sidebar-drawer-open');sidebar.inert=true;sidebar.setAttribute('aria-hidden','true');sidebar.removeAttribute('aria-modal');sidebar.removeAttribute('role');main.inert=false;backdrop.hidden=true;toggle.setAttribute('aria-expanded','false');toggle.setAttribute('aria-label','展开导航');toggle.title='展开导航';if(wasOpen&&app.user)toggle.focus({preventScroll:true});
  }
  function openNavigation(){
    if(!app.user)return;document.body.classList.add('sidebar-drawer-open');sidebar.inert=false;sidebar.setAttribute('aria-hidden','false');sidebar.setAttribute('role','dialog');sidebar.setAttribute('aria-modal','true');backdrop.hidden=false;toggle.setAttribute('aria-expanded','true');toggle.setAttribute('aria-label','收起导航');toggle.title='收起导航';main.inert=true;close.focus({preventScroll:true});
  }
  toggle.addEventListener('click',()=>document.body.classList.contains('sidebar-drawer-open')?closeNavigation():openNavigation());close.addEventListener('click',closeNavigation);backdrop.addEventListener('click',closeNavigation);
  sidebar.addEventListener('click',event=>{if(event.target.closest('a[href]'))closeNavigation();});
  document.addEventListener('keydown',event=>{
    if(!document.body.classList.contains('sidebar-drawer-open'))return;
    if(event.key==='Escape'){event.preventDefault();closeNavigation();return;}
    if(event.key!=='Tab')return;
    const focusable=[...sidebar.querySelectorAll('a[href],button:not([disabled]),[tabindex="0"]')].filter(el=>!el.closest('.hidden,[hidden]')&&el.getClientRects().length>0);
    const first=focusable[0],last=focusable[focusable.length-1];if(!first)return;
    if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}
  });
  const nav=document.querySelector('.sidebar nav');
  const homeLink=link('home','nav-item');homeLink.dataset.view='home';homeLink.append(element('span','nav-icon','⌂'),document.createTextNode('功能首页'));nav.replaceChildren(homeLink);document.querySelector('.sidebar>.nav-label')?.remove();
  for(const group of groups){
    const section=element('div','nav-group');section.dataset.group=group.name;
    const item=link(group.items[0][0],'nav-item');item.dataset.section=group.key;if(group.key==='accounts')item.id='accounts-nav';item.title=group.name+'（独立浏览器标签页）';item.append(element('span','nav-icon',group.icon),document.createTextNode(group.name));section.append(item);nav.append(section);
  }
  document.querySelectorAll('.sidebar>.sidebar-divider,.sidebar>.nav-label,.sidebar>.future-item').forEach(el=>el.remove());
  const brand=document.querySelector('.sidebar .brand');brand.href=url('home');brand.target='_blank';brand.rel='noopener';
  const tabs=element('nav','feature-tabs hidden');tabs.id='feature-tabs';tabs.setAttribute('aria-label','主功能内标签页');tabs.setAttribute('role','tablist');
  const tabGroups=new Map();
  for(const group of groups){
    const list=element('div','feature-tab-group hidden');list.dataset.section=group.key;
    for(const [key,title,,icon] of group.items){const tab=link(key,'feature-tab',true);tab.dataset.tabView=key;tab.id='feature-tab-'+key;tab.setAttribute('role','tab');tab.setAttribute('aria-controls','view-'+key);tab.append(element('span','',icon),document.createTextNode(title));list.append(tab);}
    tabs.append(list);tabGroups.set(group.key,list);
  }
  document.querySelector('.page-heading').after(tabs);
  const home=element('section','view hidden');home.id='view-home';const grid=element('div','home-entry-grid');
  for(const [index,group] of groups.entries()){
    const card=element('section','panel home-entry-card');card.dataset.group=group.name;
    const heading=element('div','home-group-heading');heading.append(element('span','home-group-icon',group.icon));
    const title=element('div');title.append(element('div','home-group-number','0'+(index+1)),element('h2','',group.name),element('p','muted',group.description));heading.append(title);card.append(heading);
    const links=element('div','home-entry-links'),item=link(group.items[0][0],'home-entry');item.dataset.openSection=group.key;
    const text=element('span','home-entry-text');text.append(element('strong','','进入'+group.name),element('span','',group.items.map(([,label])=>label).join(' · ')));item.append(text,element('span','home-entry-arrow','↗'));links.append(item);card.append(links);grid.append(card);
  }
  home.append(grid);document.querySelector('.metric-grid').after(home);
  function activate(view){
    closeNavigation();
    const admin=!!app.user?.admin;document.querySelector('.nav-group[data-group="账号管理"]').classList.toggle('hidden',!admin);grid.querySelector('[data-group="账号管理"]').classList.toggle('hidden',!admin);
    const group=groupOf(view);tabs.classList.toggle('hidden',!group);
    for(const [key,list] of tabGroups)list.classList.toggle('hidden',key!==group?.key);
    for(const tab of tabs.querySelectorAll('[role="tab"]')){const active=tab.dataset.tabView===view;tab.classList.toggle('active',active);tab.setAttribute('aria-selected',String(active));tab.tabIndex=active?0:-1;}
    for(const item of nav.querySelectorAll('[data-section]'))item.classList.toggle('active',item.dataset.section===group?.key);
    if(group){$('page-title').textContent=group.name;$('breadcrumb-current').textContent=group.name+' / '+names[view][0];}
    document.title=(group?group.name:'功能首页')+' · 数据判读系统';
    const target=new URL(url(view,location.hash.slice(1)));if(location.href!==target.href)history.replaceState({view},'',target.href);
    if(location.hash)requestAnimationFrame(()=>{let id;try{id=decodeURIComponent(location.hash.slice(1));}catch{return;}document.getElementById(id)?.scrollIntoView({block:'start'});});
  }
  tabs.addEventListener('keydown',event=>{
    if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
    const group=groupOf(app.view);if(!group)return;event.preventDefault();const items=[...tabGroups.get(group.key).children],index=items.indexOf(document.activeElement);if(index<0)return;
    const next=event.key==='Home'?0:event.key==='End'?items.length-1:(index+(event.key==='ArrowRight'?1:-1)+items.length)%items.length;items[next].click();items[next].focus();
  });
  function reset(){closeNavigation();document.title='数据判读系统';tabs.classList.add('hidden');}
  window.addEventListener('popstate',()=>{if(app.user)navigate(initialView(),true);});
  return {activate,reset,initialView,open,url,closeNavigation};
})();
