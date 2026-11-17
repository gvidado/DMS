'use strict';
window.DecoderUI = (() => {
  document.querySelector('.sidebar-bottom p span').textContent = 'VERSION 0.10.26';
  document.querySelector('.story-bottom').textContent = '数据监听、常态化判读与序列任务 / v0.10.26';
  document.querySelector('.page-footer>span').textContent = '数据判读系统 · 参数监视与序列任务 v0.10.26';
  const state = { configuration: null, revision: '', preview: null, trial: null, loading: false, saving: false, running: false, loadSequence: 0, editRevision: '', sppLoading: false, sppSequence: 0, batchPreview: null, batchSequence: 0, batchLoading: false, editPacket: null, importLoading: false, importSequence: 0 };
  const appendButton = element('button', 'button primary', '添加到现有参数表'); appendButton.id = 'append-preview';
  $('apply-preview').after(appendButton);
  const matchingButton = element('button', 'button primary', '仅替换同名解码表'); matchingButton.id = 'replace-matching-preview';
  appendButton.after(matchingButton);
  const cancelPreviewButton = element('button', 'button', '取消'); cancelPreviewButton.id = 'cancel-decoder-preview'; matchingButton.after(cancelPreviewButton);
  const cancelImportButton = element('button', 'button', '取消'); cancelImportButton.id = 'cancel-decoder-import'; cancelImportButton.type = 'button';
  $('decoder-preview-button').after(cancelImportButton);
  const clearImportButton = addExcelImportClear('decoder-import-clear', 'decoder-file', cancelImportButton, () => {
    if (state.saving) return; discardImport(); toast('已清空本次遥测表读取');
  });
  $('apply-preview').closest('.import-action-line').querySelector('.muted').textContent = '停止接收时保存立即生效，运行中每2分钟统一应用；可全部替换、添加或仅替换同名表。取消将放弃本次预览。';
  const downloadAddition = element('button', 'button', '下载添加后的JSON'); downloadAddition.id = 'download-add-preview';
  $('download-preview').after(downloadAddition);
  const importPlan = element('div', 'import-plan'); importPlan.id = 'import-plan';
  $('apply-preview').closest('.import-action-line').before(importPlan);
  const nav = element('button', 'nav-item'); nav.dataset.view = 'decoders'; nav.append(element('span', 'nav-icon', '◇'), document.createTextNode('解码配置'));
  nav.addEventListener('click', () => navigate('decoders')); $('accounts-nav').before(nav);
  const legacySection = $('cfg-length').closest('.form-section');
  const mappingLink = element('div', 'form-section hidden'); mappingLink.append(element('h3', '', 'SPP解码由独立包配置管理'), element('p', 'muted small-text', '当前已启用新的解码配置。每个包的APID、校验、偏移和参数表在“解码配置”中管理。'));
  const link = element('button', 'button small', '打开解码配置'); link.type = 'button'; link.addEventListener('click', () => navigate('decoders')); mappingLink.append(link); legacySection.before(mappingLink);
  function setRunning(running) {
    state.running = running; const locked = state.loading || state.saving || state.sppLoading || state.batchLoading || state.importLoading;
    $('apply-preview').disabled = locked || state.reviewBusy || !state.preview || state.preview.canReplace === false;
    appendButton.disabled = locked || state.reviewBusy || !state.preview?.addition?.CanApply;
    matchingButton.disabled = locked || state.reviewBusy || !state.preview?.matching?.CanApply;
    cancelImportButton.disabled = state.saving || state.reviewBusy || !(state.importLoading || state.preview || $('decoder-file').files.length);
    clearImportButton.disabled = state.saving || state.reviewBusy;
    cancelPreviewButton.disabled = state.saving || state.reviewBusy || !state.preview;
    downloadAddition.disabled = !state.preview?.addition?.CanApply || state.importLoading || state.reviewBusy;
    $('download-preview').disabled = !state.preview || state.preview.canReplace === false || state.reviewBusy;
    $('decoder-preview-button').disabled = state.importLoading || state.saving || state.reviewBusy;
    $('save-packet').disabled = locked; $('new-packet').disabled = locked;
    document.querySelectorAll('.packet-edit, .packet-delete, .parameter-edit').forEach(button => button.disabled = locked);
    if($('parameter-edit-save'))$('parameter-edit-save').disabled=locked || parameterLoading;
    if ($('spp-batch-apply')) $('spp-batch-apply').disabled = locked || !state.batchPreview;
    if ($('spp-batch-cancel')) $('spp-batch-cancel').disabled = state.saving || !(state.batchLoading || state.batchPreview || $('spp-batch-file').files.length);
    if ($('spp-batch-clear')) $('spp-batch-clear').disabled = state.saving;
    if ($('spp-cancel')) $('spp-cancel').disabled = state.saving || !(state.sppLoading || $('spp-file').files.length);
    if ($('spp-clear')) $('spp-clear').disabled = state.saving;
    const native = !!app.state?.nativeDecoder; legacySection.classList.toggle('hidden', native); mappingLink.classList.toggle('hidden', !native);
  }
  function td(row, text, cls = '') { const cell = element('td', cls, text); row.append(cell); return cell; }
  const currentTables = element('section', 'panel current-tables');
  currentTables.innerHTML = `<div class="panel-heading"><div><h2>当前已导入参数表</h2><p>查看当前保存的参数表、定义完整性及SPP关联；读取新文件的预览不会改变这里的内容。</p></div><button id="current-tables-refresh" class="button small">刷新当前参数表</button></div><div class="section-pad"><p id="current-tables-source" class="muted small-text">正在读取当前参数配置…</p><div id="current-tables-summary" class="import-summary"></div><p class="muted small-text" id="tm-reference-guide">解码表保存原始代号；与SPP数据源关联后，工程值通过TME.代号.value或TMB.代号.value引用。本参数规则可用this.value，引用其他参数使用完整TM引用。代号大小写与Excel一致。</p><div class="capture-table-heading"><h3>已有参数表</h3><input id="current-tables-search" placeholder="搜索参数表或关联SPP包" aria-label="搜索已有参数表"></div><div class="table-scroll current-tables-scroll"><table class="data-table"><thead><tr><th>工作表 / Sheet</th><th>参数总数</th><th>定义完整</th><th>待补全</th><th>预留已跳过</th><th>关联SPP包 / 状态</th><th>使用情况</th><th>参数明细</th></tr></thead><tbody id="current-table-rows"></tbody></table></div><p id="current-tables-empty" class="muted small-text hidden">没有符合搜索条件的参数表。</p></div>`;
  document.querySelector('.decoder-import').before(currentTables);
  const tableDialog = element('dialog', 'packet-dialog current-table-dialog'); tableDialog.id = 'current-table-dialog';
  tableDialog.innerHTML = `<div class="dialog-heading"><div><h2 id="current-table-title">已导入参数明细</h2><p id="current-table-source" class="muted small-text"></p></div><button id="current-table-close" class="icon-button" aria-label="关闭参数表明细">×</button></div><div class="section-pad"><p class="muted small-text">点击参数行“编辑”可修改单项解码定义，保存后在下次统一切换时生效。字节和位位置相对用户数据域，数值按系数换算。工程值统一用TM.参数代号.value引用；本参数规则也可用this.value。</p><div class="capture-table-heading"><h3>参数定义</h3><input id="current-table-search" placeholder="搜索TM引用、名称或位置" aria-label="搜索当前参数表明细"></div><div class="table-scroll current-table-detail-scroll"><table class="data-table"><thead><tr><th>参数引用 / 工程值</th><th>参数名称 / Excel行</th><th>字节 / 位位置</th><th>类型</th><th>系数</th><th>单位</th><th>状态 / 提示</th><th>操作</th></tr></thead><tbody id="current-table-parameters"></tbody></table></div><div class="current-table-pagination"><span id="current-table-page-note" class="muted small-text"></span><div><button id="current-table-previous" class="button small">上一页</button> <button id="current-table-next" class="button small">下一页</button></div></div><details id="current-table-issues-wrap" class="import-details"><summary>此表读取提示</summary><div id="current-table-issues" class="import-issues"></div></details></div>`;
  document.body.append(tableDialog);
  let currentSheet = '', currentPage = 0, nativeTables = false;
  let parameterDraft=null,parameterRevision='',parameterLoading=false,parameterEpoch=0;
  const parameterDialog=element('dialog','packet-dialog current-table-dialog');parameterDialog.id='parameter-edit-dialog';parameterDialog.innerHTML=`<div class="dialog-heading"><div><h2 id="parameter-edit-title">编辑参数解码定义</h2><p id="parameter-edit-origin" class="muted small-text"></p></div><button id="parameter-edit-close" class="icon-button" aria-label="关闭参数编辑">×</button></div><form id="parameter-edit-form" class="section-pad"><p class="muted small-text">参数代号与所属表保持不变，已有TM引用和判读绑定保留。字节位置从用户数据域Byte0起算；B0方向由SPP配置决定。</p><div class="rule-form-grid"><label>参数代号<input id="parameter-edit-code" readonly></label><label>所属参数表<input id="parameter-edit-group" readonly></label><label>参数名称<input id="parameter-edit-name" required maxlength="240"></label><label>单位<input id="parameter-edit-unit" maxlength="100"></label><label>起始字节<input id="parameter-edit-offset" type="number" min="0" max="65507" step="1" required></label><label>占用字节数<input id="parameter-edit-length" type="number" min="1" max="9" step="1" required></label><label>数据类型<select id="parameter-edit-type"><option value="uint">无符号整数</option><option value="int">有符号整数</option><option value="float32">单精度浮点（4字节）</option><option value="float64">双精度浮点（8字节）</option><option value="ascii">ASCII文本</option><option value="bcd">BCD</option><option value="unknown">未知 / 待补全</option></select></label><label>显示精度<input id="parameter-edit-precision" type="number" min="0" max="12" step="1" required></label><label>系数<input id="parameter-edit-scale" type="number" step="any" required></label><label>偏置<input id="parameter-edit-bias" type="number" step="any" required></label><label>显示格式<select id="parameter-edit-format"><option>十进制</option><option>十六进制</option></select></label></div><p class="muted small-text">工程值 = 解码值 × 系数 + 偏置。</p><label class="checkbox-line"><input id="parameter-edit-bit-mode" type="checkbox">按位字段解码</label><div class="rule-form-grid"><label>起始位B（0～7）<input id="parameter-edit-bitstart" type="number" min="0" max="7" step="1"></label><label>位宽（1～64）<input id="parameter-edit-bitcount" type="number" min="1" max="64" step="1"></label></div><p id="parameter-edit-bit-note" class="muted small-text"></p><label class="checkbox-line"><input id="parameter-edit-supported" type="checkbox">定义完整，可进行解码</label><label>待补全说明<textarea id="parameter-edit-diagnostic" rows="2" maxlength="4000"></textarea></label><label>物理意义 / 备注<textarea id="parameter-edit-note" rows="3" maxlength="100000"></textarea></label><div class="rule-toolbar"><button id="parameter-edit-parse-note" type="button" class="button small">从物理意义生成注释映射</button><span class="muted small-text">如“0：关闭；1：开启”或“[0,10]：正常”。点击后重建下方映射。</span></div><details class="import-details"><summary>原码枚举注释</summary><p class="muted small-text">原码使用整数或0x十六进制；说明用于显示和INENUM判读。</p><button id="parameter-edit-add-enum" type="button" class="button small">添加枚举</button><div class="table-scroll short-table"><table class="data-table"><thead><tr><th>原码</th><th>说明</th><th>操作</th></tr></thead><tbody id="parameter-edit-enums"></tbody></table></div></details><details class="import-details"><summary>工程值区间注释</summary><p class="muted small-text">上下限均包含边界，使用换算后的工程值。</p><button id="parameter-edit-add-range" type="button" class="button small">添加区间</button><div class="table-scroll short-table"><table class="data-table"><thead><tr><th>下限</th><th>上限</th><th>说明</th><th>操作</th></tr></thead><tbody id="parameter-edit-ranges"></tbody></table></div></details><p id="parameter-edit-notice" class="muted small-text"></p><p id="parameter-edit-error" class="form-error" role="alert"></p><div class="import-action-line"><button id="parameter-edit-cancel" type="button" class="button">取消</button><button id="parameter-edit-save" type="submit" class="button primary">保存到当前解码配置</button></div></form>`;document.body.append(parameterDialog);
  const parameterScalar={Name:'name',Unit:'unit',Offset:'offset',Length:'length',Type:'type',Precision:'precision',Scale:'scale',Bias:'bias',Format:'format',Diagnostic:'diagnostic',Note:'note'};
  function addEnumRow(key='',text=''){const row=element('tr');row.dataset.enumRow='yes';const value=element('input');value.dataset.field='key';value.value=key;const meaning=element('textarea');meaning.dataset.field='text';meaning.rows=1;meaning.value=text;const remove=element('button','text-button','移除');remove.type='button';remove.addEventListener('click',()=>row.remove());td(row,'').append(value);td(row,'').append(meaning);td(row,'').append(remove);$('parameter-edit-enums').append(row);}
  function addRangeRow(min='',max='',text=''){const row=element('tr');row.dataset.rangeRow='yes';for(const [field,value] of [['min',min],['max',max],['text',text]]){const input=element(field==='text'?'textarea':'input');input.dataset.field=field;if(field==='text')input.rows=1;input.value=value;td(row,'').append(input);}const remove=element('button','text-button','移除');remove.type='button';remove.addEventListener('click',()=>row.remove());td(row,'').append(remove);$('parameter-edit-ranges').append(row);}
  function bitEditChanged(){const bit=$('parameter-edit-bit-mode').checked;$('parameter-edit-length').readOnly=bit;$('parameter-edit-bitstart').disabled=!bit;$('parameter-edit-bitcount').disabled=!bit;if(bit){const start=Number($('parameter-edit-bitstart').value),width=Number($('parameter-edit-bitcount').value);if(Number.isInteger(start) && Number.isInteger(width))$('parameter-edit-length').value=Math.ceil((start+width)/8);}$('parameter-edit-bit-note').textContent=bit?'占用字节数按起始位和位宽计算；跨字节按已配置的SPP位序提取。':'按整字节读取，起始位和位宽不参与解码。';}
  async function openParameter(parameter){
    if(state.saving || parameterLoading)return;parameterLoading=true;setRunning(state.running);const epoch=++parameterEpoch;
    try{const latest=await api('decoders/parameter?id='+encodeURIComponent(parameter.Id));if(epoch!==parameterEpoch || !app.user)return;parameterDraft=latest.parameter;parameterRevision=latest.revision;$('parameter-edit-title').textContent='编辑参数 · '+parameterDraft.Name;$('parameter-edit-origin').textContent='关联SPP：'+(latest.packets.join('、') || '尚未关联')+' · 保存只更新本地解码配置';$('parameter-edit-code').value=parameterDraft.Code;$('parameter-edit-group').value=parameterDraft.Group;for(const [field,id] of Object.entries(parameterScalar))$('parameter-edit-'+id).value=parameterDraft[field]??'';$('parameter-edit-bit-mode').checked=parameterDraft.BitStart>=0;$('parameter-edit-bitstart').value=parameterDraft.BitStart>=0?parameterDraft.BitStart:0;$('parameter-edit-bitcount').value=parameterDraft.BitCount || 1;$('parameter-edit-supported').checked=parameterDraft.Supported;$('parameter-edit-enums').replaceChildren();for(const [key,text] of Object.entries(parameterDraft.Enums || {}))addEnumRow(key,text);$('parameter-edit-ranges').replaceChildren();for(const range of parameterDraft.EnumRanges || [])addRangeRow(range.Min,range.Max,range.Text);$('parameter-edit-error').textContent='';$('parameter-edit-notice').textContent='';bitEditChanged();parameterDialog.showModal();}catch(e){toast(e.message,true);}finally{parameterLoading=false;setRunning(state.running);}
  }
  ['parameter-edit-close','parameter-edit-cancel'].forEach(id=>$(id).addEventListener('click',()=>{parameterEpoch++;parameterDialog.close();}));$('parameter-edit-add-enum').addEventListener('click',()=>addEnumRow());$('parameter-edit-add-range').addEventListener('click',()=>addRangeRow());['parameter-edit-bit-mode','parameter-edit-bitstart','parameter-edit-bitcount'].forEach(id=>$(id).addEventListener('input',bitEditChanged));
  $('parameter-edit-parse-note').addEventListener('click',async()=>{if(parameterLoading || state.saving)return;parameterLoading=true;setRunning(state.running);const epoch=parameterEpoch;try{const result=await api('decoders/parameter/note',{note:$('parameter-edit-note').value});if(epoch!==parameterEpoch)return;$('parameter-edit-enums').replaceChildren();$('parameter-edit-ranges').replaceChildren();for(const [key,text] of Object.entries(result.enums))addEnumRow(key,text);for(const range of result.ranges)addRangeRow(range.Min,range.Max,range.Text);$('parameter-edit-notice').textContent='已生成'+Object.keys(result.enums).length+'项枚举和'+result.ranges.length+'项区间。'+result.issues.map(i=>i.Message).join('；');$('parameter-edit-error').textContent='';}catch(e){$('parameter-edit-error').textContent=e.message;}finally{parameterLoading=false;setRunning(state.running);}});
  $('parameter-edit-form').addEventListener('submit',async event=>{
    event.preventDefault();if(!parameterDraft || state.saving || parameterLoading)return;
    try{const parameter={...parameterDraft},numeric=new Set(['Offset','Length','Precision','Scale','Bias']);for(const [field,id] of Object.entries(parameterScalar)){const text=$('parameter-edit-'+id).value;parameter[field]=numeric.has(field)?Number(text):text;if(numeric.has(field) && (text.trim()==='' || !Number.isFinite(parameter[field])))throw new Error('请填写有效的'+field+'数值');}const bits=$('parameter-edit-bit-mode').checked;parameter.BitStart=bits?Number($('parameter-edit-bitstart').value):-1;parameter.BitCount=bits?Number($('parameter-edit-bitcount').value):0;parameter.Supported=$('parameter-edit-supported').checked;parameter.Enums=Object.create(null);for(const row of $('parameter-edit-enums').querySelectorAll('[data-enum-row]')){const key=row.querySelector('[data-field="key"]').value.trim(),text=row.querySelector('[data-field="text"]').value;if(!key && !text.trim())continue;if(!key || !text.trim())throw new Error('请补全枚举原码和说明');if(Object.prototype.hasOwnProperty.call(parameter.Enums,key))throw new Error('枚举原码重复：'+key);parameter.Enums[key]=text;}parameter.EnumRanges=[];for(const row of $('parameter-edit-ranges').querySelectorAll('[data-range-row]')){const lo=row.querySelector('[data-field="min"]').value.trim(),hi=row.querySelector('[data-field="max"]').value.trim(),text=row.querySelector('[data-field="text"]').value;if(!lo && !hi && !text.trim())continue;if(!lo || !hi || !text.trim() || !Number.isFinite(Number(lo)) || !Number.isFinite(Number(hi)))throw new Error('请补全区间的有效上下限和说明');parameter.EnumRanges.push({Min:Number(lo),Max:Number(hi),Text:text});}state.saving=true;setRunning(state.running);await api('decoders/parameter',{id:parameterDraft.Id,revision:parameterRevision,parameter});parameterDialog.close();await changed();toast('单项参数已保存，下次统一切换后按新定义解码');}catch(e){$('parameter-edit-error').textContent=e.message;}finally{state.saving=false;setRunning(state.running);}
  });
  function sheetNames() {
    const c = state.configuration.Catalog;
    return [...new Set([...(c.Sheets || []).map(s => s.Name), ...c.Parameters.map(p => p.Group)])];
  }
  function renderCurrentTables() {
    const c = state.configuration.Catalog, names = sheetNames(), packets = state.configuration.Packets;
    const ready = c.Parameters.filter(p => p.Supported).length, bound = names.filter(name => packets.some(p => p.Groups.includes(name))).length;
    $('current-tables-source').textContent = nativeTables
      ? '参数来源：' + (c.SourceFile || '未记录来源文件') + (c.GeneratedAt ? ' · 配置生成时间：' + c.GeneratedAt : '')
      : '参数来源：旧版内置参数配置，以下为当前可用参数表；可直接查看，无需重新选择Excel。';
    $('current-tables-summary').replaceChildren(element('strong', '', names.length + '张参数表'), element('span', '', c.Parameters.length + '项参数'), element('span', '', ready + '项定义完整'), element('span', '', (c.Parameters.length - ready) + '项待补全'), element('span', '', '已关联' + bound + '张 / 未关联' + (names.length - bound) + '张'));
    const query = $('current-tables-search').value.toLowerCase().trim(), fragment = document.createDocumentFragment(); let shown = 0;
    for (const name of names) {
      const linked = packets.filter(p => p.Groups.includes(name));
      if (!(name + ' ' + linked.map(p => p.Name + ' APID ' + p.Apid).join(' ')).toLowerCase().includes(query)) continue;
      const parameters = c.Parameters.filter(p => p.Group === name), supported = parameters.filter(p => p.Supported).length;
      const sheet = (c.Sheets || []).find(s => s.Name === name), row = element('tr'); row.dataset.sheet = name; shown++;
      const title = td(row, name); if (sheet?.Index) title.append(element('div', 'muted small-text', 'Sheet ' + sheet.Index));
      td(row, parameters.length, 'mono'); td(row, supported, 'mono'); td(row, parameters.length - supported, 'mono'); td(row, sheet?.ReservedSkipped || 0, 'mono');
      const links = td(row, '');
      for (const p of linked) links.append(element('p', 'small-text', p.Name + ' / APID ' + p.Apid + ' · ' + (p.Enabled ? '遥测解码' : '未启用解码') + ' · ' + (p.MappingConfirmed ? '映射已确认' : '映射待确认')));
      if (!linked.length) links.textContent = '未关联SPP包';
      const active = linked.filter(p => p.Enabled), pending = parameters.length - supported;
      const status = !parameters.length ? '暂无参数' : pending ? '定义待补全' : !linked.length ? '未关联SPP' : !active.length ? '关联包未启用' : active.some(p => p.MappingConfirmed) ? '已关联并启用' : '映射待确认';
      td(row, '').append(element('span', 'status-tag ' + (pending ? 'warn' : status === '已关联并启用' ? 'good' : 'neutral'), status));
      const view = element('button', 'button small', '查看参数'); view.addEventListener('click', () => openCurrentTable(name)); td(row, '').append(view); fragment.append(row);
    }
    $('current-table-rows').replaceChildren(fragment); $('current-tables-empty').classList.toggle('hidden', shown > 0);
    if (tableDialog.open) renderCurrentParameters();
  }
  function openCurrentTable(name) {
    currentSheet = name; currentPage = 0; $('current-table-search').value = ''; renderCurrentParameters(); tableDialog.showModal();
  }
  function renderCurrentParameters() {
    const c = state.configuration.Catalog, query = $('current-table-search').value.toLowerCase().trim();
    const parameters = c.Parameters.filter(p => p.Group === currentSheet), filtered = parameters.filter(p => (parameterSearchText(p) + ' ' + (p.Position || '') + ' ' + p.Offset).toLowerCase().includes(query));
    const pages = Math.max(1, Math.ceil(filtered.length / 50)); currentPage = Math.min(currentPage, pages - 1);
    $('current-table-title').textContent = currentSheet + ' · 已导入参数';
    $('current-table-source').textContent = (c.SourceFile || '当前参数配置') + ' · 共' + parameters.length + '项 · 可在线单独编辑，保存后按周期统一生效';
    const types = { uint:'无符号整数', int:'有符号整数', float32:'单精度浮点', float64:'双精度浮点', ascii:'ASCII文本', bcd:'BCD' }, rows = document.createDocumentFragment();
    for (const p of filtered.slice(currentPage * 50, (currentPage + 1) * 50)) {
      const row = element('tr');row.dataset.parameterSourceId=p.Id; const reference = td(row, p.Code, 'mono'); reference.append(element('div', 'muted small-text', '关联SPP后生成TME/TMB引用')); const name = td(row, p.Name);
      if (p.SourceRow) name.append(element('div', 'muted small-text', '第' + p.SourceRow + '行'));
      td(row, p.Position || ('Byte' + p.Offset + '起 / ' + p.Length + '字节'), 'mono'); td(row, types[p.Type] || p.Type || '未定义');
      td(row, p.Scale ?? 1, 'mono'); td(row, p.Unit || '—');
      const quality = td(row, ''); quality.append(element('span', 'status-tag ' + (p.Supported ? 'good' : 'warn'), p.Supported ? '定义完整' : '待补全'));
      if (p.Diagnostic) quality.append(element('p', 'muted small-text', p.Diagnostic));const edit=element('button','button small parameter-edit','编辑');edit.disabled=state.loading || state.saving || parameterLoading;edit.addEventListener('click',()=>openParameter(p));td(row,'').append(edit);rows.append(row);
    }
    $('current-table-parameters').replaceChildren(rows);
    $('current-table-page-note').textContent = '搜索结果 ' + filtered.length + '项 · 第' + (currentPage + 1) + '/' + pages + '页，每页50项';
    $('current-table-previous').disabled = currentPage === 0; $('current-table-next').disabled = currentPage >= pages - 1;
    const issues = [...(c.Issues || []), ...(c.ReferenceIssues || [])].filter(i => i.Sheet === currentSheet), notes = document.createDocumentFragment();
    for (const issue of issues) notes.append(element('p', issue.Level === 'error' ? 'issue-error' : 'issue-warning', (issue.Row ? '第' + issue.Row + '行 ' : '') + (issue.Code || '') + '：' + issue.Message));
    $('current-table-issues').replaceChildren(notes); $('current-table-issues-wrap').classList.toggle('hidden', !issues.length);
  }
  $('current-tables-refresh').addEventListener('click', load);
  $('current-tables-search').addEventListener('input', () => { if (state.configuration) renderCurrentTables(); });
  $('current-table-close').addEventListener('click', () => tableDialog.close());
  $('current-table-search').addEventListener('input', () => { currentPage = 0; renderCurrentParameters(); });
  $('current-table-previous').addEventListener('click', () => { currentPage--; renderCurrentParameters(); });
  $('current-table-next').addEventListener('click', () => { currentPage++; renderCurrentParameters(); });
  async function load() {
    if (!app.user) return;
    const sequence = ++state.loadSequence; state.loading = true; setRunning(state.running);
    try {
      const data = await api('decoders'); if (sequence !== state.loadSequence) return;
      state.configuration = data.configuration; state.revision = data.revision;
      nativeTables = data.native; renderCurrentTables();
      renderPackets();
      const source = state.configuration.Catalog;
      $('decoder-catalog-count').textContent = source.Parameters.length + '项 · ' + sheetNames().length + '张参数表';
      $('decoder-mode-note').textContent = data.native ? '当前使用本地decoder.json，配置版本 ' + data.revision.slice(0, 8) : '当前仍使用原版配置。首次应用Excel或保存SPP包后，将自动迁移到独立decoder.json。';
    } catch (e) { toast(e.message, true); } finally { if (sequence === state.loadSequence) { state.loading = false; setRunning(state.running); } }
  }
  function renderPackets() {
    const packets = state.configuration.Packets, fragment = document.createDocumentFragment(); const old = $('try-packet').value;
    for (const p of packets) {
      const card = element('div', 'packet-definition');
      const icon = element('div', 'packet-apid mono', '0x' + p.Apid.toString(16).toUpperCase().padStart(3, '0'));
      const text = element('div', 'packet-definition-text');
      const heading = element('div', 'packet-definition-heading'); heading.append(element('h3', '', p.Name), element('span', 'status-tag ' + (p.Enabled ? 'good' : 'neutral'), p.Enabled ? '遥测解码' : '未启用解码'));
      if(p.MonitorRaw)heading.append(element('span','status-tag good','监测原包'));
      text.append(heading, element('p', 'muted small-text', (p.SourceIP || '匹配接收白名单') + ' · ' + (p.Groups.join(' + ') || '未关联参数表')),
        element('p', 'muted small-text mono', 'ID ' + p.Id + ' · SPP偏移 ' + (p.SppOffset === -1 ? '自动' : p.SppOffset) + ' · 用户域 +' + p.PayloadOffset + ' · ' + 'Fletcher-16（固定）'));
      if (p.SppSourceSheet) text.append(element('p', 'muted small-text', '导入来源：' + p.SppSourceFile + ' / ' + p.SppSourceSheet));
      text.append(element('p', 'muted small-text mono', '数据源 '+(p.OuterSourceId>=0?'0x'+p.OuterSourceId.toString(16).toUpperCase().padStart(4,'0')+' / '+p.TelemetryNamespace:p.MonitorRaw&&!p.Enabled?'/ · 原包仅按APID匹配':'未设置 · 请编辑补齐')+(p.OuterSourceId<0&&p.MonitorRaw&&!p.Enabled?' · 匹配条件：仅APID '+p.Apid:' · 匹配条件：源地址 0x' + p.SourceAddress.toString(16).toUpperCase().padStart(4, '0') + ' · 业务类型 0x' + p.ServiceType.toString(16).toUpperCase().padStart(2, '0') + ' · 业务子类型 0x' + p.ServiceSubtype.toString(16).toUpperCase().padStart(2, '0'))));
      const actions = element('div', 'packet-definition-actions');
      const edit = element('button', 'button small packet-edit', '编辑'); edit.addEventListener('click', () => openPacket(p));
      const remove = element('button', 'text-button packet-delete', '删除'); remove.addEventListener('click', async () => {
        if (!window.confirm('删除包定义“' + p.Name + '”？对应实时值会被清空，已下载的配置文件不受影响。')) return;
        try { await api('decoders/packet/delete', { id: p.Id, revision: state.revision }); await changed(); toast('包定义已删除'); } catch (e) { toast(e.message, true); }
      }); actions.append(edit, remove); card.append(icon, text, actions); fragment.append(card);
    }
    if (!packets.length) fragment.append(element('div', 'empty', '暂无SPP包定义。可新增包并选择遥测解码或监测原包；仅监测原包时无需参数表。'));
    $('packet-definitions').replaceChildren(fragment);
    $('try-packet').replaceChildren(...packets.map(p => new Option(p.Name + ' / APID ' + p.Apid, p.Id)));
    if (packets.some(p => p.Id === old)) $('try-packet').value = old;
    $('try-decode').disabled = !packets.length; setRunning(state.running);
  }
  async function changed() {
    clearBatch();
    state.preview = null; $('decoder-preview').classList.add('hidden'); state.trial = null; $('try-result').classList.add('hidden');
    await load(); await refreshCatalog();
  }
  async function readFile() {
    if (state.importLoading || state.saving) return;
    const file = $('decoder-file').files[0]; if (!file) { $('decoder-import-error').textContent = '请先选择.xlsx遥测表或.json解码配置'; return; }
    if (file.size > 8 * 1024 * 1024) { $('decoder-import-error').textContent = '导入文件最大8MB'; return; }
    clearBatch(); discardImport(false); const sequence = ++state.importSequence; state.importLoading = true; setRunning(state.running);
    $('decoder-import-error').textContent = ''; state.preview = null; $('decoder-preview').classList.add('hidden');
    try {
      const response = await fetch('/api/decoders/preview?filename=' + encodeURIComponent(file.name), { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-Token': app.user.csrf }, body: file });
      const data = await response.json(); if (!response.ok) throw new Error(data.error || '读取失败');
      if (sequence !== state.importSequence) { revokeImport(data.token); return; }
      state.preview = data; renderImport();
    } catch (e) { if (sequence === state.importSequence) $('decoder-import-error').textContent = e.message; }
    finally { if (sequence === state.importSequence) { state.importLoading = false; setRunning(state.running); } }
  }
  function revokeImport(token) {
    if (token) api('decoders/preview/cancel', { token }).catch(() => {});
  }
  function discardImport(clearFile = true, revoke = true) {
    const token = state.preview?.token;
    ++state.importSequence; state.importLoading = false; state.preview = null;
    window.TelemetryImportUI?.reset();
    $('decoder-preview').classList.add('hidden'); $('decoder-import-error').textContent = '';
    $('import-parameter-search').value = '';
    for (const id of ['import-summary', 'import-sheets', 'import-parameters', 'import-issues', 'import-plan']) $(id).replaceChildren();
    if (clearFile) $('decoder-file').value = '';
    setRunning(state.running); if (revoke) revokeImport(token);
  }
  function renderImport() {
    const source = state.preview.configuration.Catalog;
    $('decoder-preview').classList.remove('hidden'); const supported = source.Parameters.filter(p => p.Supported).length;
    const reserved = source.Sheets.reduce((total, sheet) => total + (sheet.ReservedSkipped || 0), 0);
    $('import-summary').replaceChildren(element('strong', '', source.SourceFile), element('span', '', source.Parameters.length + '项参数'), element('span', '', supported + '项可生成'), element('span', '', source.Parameters.length - supported + '项定义待补全'), element('span', '', reserved + '项预留已跳过'));
    const rows = document.createDocumentFragment();
    for (const sheet of source.Sheets) { const row = element('tr'); td(row, (sheet.Index || '—') + ' / ' + sheet.Name); td(row, sheet.HeaderRow || '—'); td(row, sheet.Parameters); td(row, sheet.Supported); td(row, sheet.ReservedSkipped || 0); rows.append(row); }
    $('import-sheets').replaceChildren(rows); renderImportParameters();
    const allIssues = [...(source.Issues || []), ...(source.ReferenceIssues || [])];
    const issues = document.createDocumentFragment();
    for (const note of source.Assumptions || []) issues.append(element('p', 'import-assumption', note));
    for (const issue of allIssues) {
      const line = element('p', issue.Level === 'error' ? 'issue-error' : 'issue-warning', (issue.Sheet ? issue.Sheet + (issue.Row ? ' 第' + issue.Row + '行' : '') + ' / ' : '') + (issue.Code ? issue.Code + '：' : '') + issue.Message);
      const row = state.preview.review?.rows.find(r => !r.discarded && r.sheet === issue.Sheet && r.row === issue.Row);
      if (row) { const edit = element('button', 'button small', '定位并修改'); edit.type = 'button'; edit.addEventListener('click', () => window.TelemetryImportUI.open(row.key)); line.append(document.createTextNode(' '), edit); }
      issues.append(line);
    }
    $('import-issues').replaceChildren(issues); $('import-issues-summary').textContent = '读取提示 / ' + allIssues.length + '项（枚举、缺失字段或TM引用）';
    $('import-issues-details').open = allIssues.some(i => i.Level === 'error') || !!source.ReferenceIssues?.length;
    const excel = state.preview.sourceType === 'xlsx', addition = state.preview.addition, matching = state.preview.matching;
    matchingButton.classList.toggle('hidden', !excel);
    $('apply-preview').textContent = excel ? '替换当前参数表' : '替换完整配置';
    $('download-preview').textContent = '下载替换配置JSON';
    const plan = document.createDocumentFragment();
    for (const message of state.preview.reviewErrors || []) plan.append(element('p', 'issue-error small-text', message));
    plan.append(element('p', 'small-text', excel
      ? '替换：用本次读取的' + source.Parameters.length + '项参数更新当前参数表，保留SPP包定义；引用已移除参数表的包会停用。'
      : '替换：使用JSON内的完整参数表与SPP包定义更新当前配置。'));
    if (excel && addition) plan.append(element('p', 'small-text', '添加：新增' + addition.AddedSheets.length + '张参数表、' + addition.AddedParameters + '项参数；相同参数跳过' + addition.DuplicateParameters + '项，冲突' + addition.ConflictCount + '项。保留已有参数与SPP关联。'));
    if (excel && matching) {
      plan.append(element('p', 'small-text', '仅替换同名解码表：按Sheet名称完全一致匹配，整表更新' + matching.ReplacedSheets.length + '张（原' + matching.PreviousParameters + '项→新' + matching.ImportedParameters + '项参数）；保留其余已有表及SPP关联。未出现在新表中的原参数会从该同名表移除。'));
      if (matching.ReplacedSheets.length) plan.append(element('p', 'small-text', '将替换：' + matching.ReplacedSheets.join('、')));
      if (matching.RetainedSheets.length) plan.append(element('p', 'muted small-text', '保留已有表：' + matching.RetainedSheets.join('、')));
      if (matching.SkippedSheets.length) plan.append(element('p', 'muted small-text', '跳过新文件中的不同名表：' + matching.SkippedSheets.join('、') + '；如需导入请使用“添加”。'));
      for (const message of matching.Errors || []) plan.append(element('p', 'issue-warning small-text', '同名替换：' + message));
      for (const issue of matching.ReferenceIssues || []) plan.append(element('p', 'issue-warning small-text', '同名替换后的TM引用提示：' + issue.Sheet + ' / ' + issue.Code + '：' + issue.Message));
    }
    for (const message of [...(addition?.Errors || []), ...(addition?.Conflicts || [])]) plan.append(element('p', 'issue-error small-text', message));
    for (const issue of addition?.ReferenceIssues || []) plan.append(element('p', 'issue-warning small-text', '添加后的TM引用提示：' + issue.Sheet + ' / ' + issue.Code + '：' + issue.Message));
    if (addition?.ConflictCount > (addition.Conflicts || []).length) plan.append(element('p', 'muted small-text', '这里只显示前100条冲突，请核对同名工作表中的参数编码。'));
    if (addition?.ConflictCount) plan.append(element('p', 'muted small-text', '添加不会覆盖冲突项；可修正新表，或使用“仅替换同名解码表”更新匹配表的定义。'));
    importPlan.replaceChildren(plan); setRunning(state.running);
  }
  function renderImportParameters() {
    if (!state.preview) return;
    if (state.preview.review && window.TelemetryImportUI) { window.TelemetryImportUI.render(state.preview); return; }
    window.TelemetryImportUI?.hide();
    const query = $('import-parameter-search').value.trim().toLowerCase(); const rows = document.createDocumentFragment();
    for (const p of state.preview.configuration.Catalog.Parameters.filter(p => (parameterSearchText(p) + ' ' + p.Group).toLowerCase().includes(query)).slice(0, 100)) {
      const row = element('tr'); const first = td(row, p.Name + ' / ' + p.Code); first.append(element('div', 'muted small-text', p.Group + ' · 第' + p.SourceRow + '行'), element('div', 'mono small-text', '待SPP来源映射'));
      const address = td(row, p.Position || 'Byte' + p.Offset); address.append(element('div', 'muted small-text', p.Type + ' · 占用' + p.Length + '字节' + (p.CrossByte ? ' · 跨字节' : '')));
      td(row, '× ' + p.Scale, 'mono'); td(row, p.Supported ? p.CrossByte ? '首位最高位' : '可生成' : p.Diagnostic || '定义待核实'); rows.append(row);
    }
    $('import-parameters').replaceChildren(rows);
  }
  $('decoder-preview-button').addEventListener('click', readFile);
  $('decoder-file').addEventListener('change', () => discardImport(false));
  cancelImportButton.addEventListener('click', () => { discardImport(); toast('已取消本次导入，当前配置未修改'); });
  cancelPreviewButton.addEventListener('click', () => { discardImport(); toast('已取消本次导入，当前配置未修改'); });
  $('import-parameter-search').addEventListener('input', renderImportParameters);
  $('download-preview').addEventListener('click', () => {
    if (!state.preview || state.preview.canReplace === false) return;
    const link = element('a'); link.href = '/api/decoders/preview/export?token=' + encodeURIComponent(state.preview.token); link.download = 'telemetry-decoder.json';
    document.body.append(link); link.click(); link.remove();
  });
  downloadAddition.addEventListener('click', () => {
    if (!state.preview?.addition?.CanApply) return;
    const link = element('a'); link.href = '/api/decoders/preview/export?mode=add&token=' + encodeURIComponent(state.preview.token); link.download = 'telemetry-decoder-added.json';
    document.body.append(link); link.click(); link.remove();
  });
  async function applyImport(mode) {
    if (!state.preview || state.saving || state.reviewBusy) return; state.saving = true; setRunning(state.running);
    const preview = state.preview;
    try { await api('decoders/apply', { token: preview.token, revision: preview.revision, mode }); await changed(); toast(mode === 'add' ? '已添加' + preview.addition.AddedParameters + '项参数，当前配置已保存' : mode === 'replace-matching' ? '已替换' + preview.matching.ReplacedSheets.length + '张同名解码表，其他表已保留' : '解码配置已替换并保存'); }
    catch (e) { $('decoder-import-error').textContent = e.message; } finally { state.saving = false; setRunning(state.running); }
  }
  $('apply-preview').addEventListener('click', () => applyImport('replace'));
  appendButton.addEventListener('click', () => applyImport('add'));
  matchingButton.addEventListener('click', () => applyImport('replace-matching'));
  const sourceRow=element('div','form-section');sourceRow.innerHTML=`<h3>外层数据源</h3><div class="form-grid"><label>UDP载荷前2字节（十六进制）<input id="pkt-outer-source" placeholder="例如0x0802、0202、0422或/" maxlength="16"><small>六字节外层头：数据源2字节 + 0000 + 长度2字节。只监测原包时可填/，仅按APID匹配；遥测解码必须填写具体数据源。</small></label><label>参数分类<input id="pkt-namespace" readonly value="来源未设置"><small>0x0202或0x0422归TMB，其余有效标识归TME。SPP起点为UDP载荷偏移6。</small></label></div><p class="muted small-text">关联参数表并保存后建立TME/TMB变量映射及停用的默认判读规则；解码表中的原参数代号保持不变。</p>`;$('packet-dialog').querySelector('.packet-form-body').prepend(sourceRow);
  function outerSource(value){const text=String(value??'').trim();if(!text||text==='-1'||text==='/')return -1;const hex=text.replace(/^0x/i,'').replace(/\s/g,'');if(!/^[0-9a-f]{1,4}$/i.test(hex))throw new Error('数据源应为2字节十六进制，例如0202或0x0802；仅监测原包可填/');return parseInt(hex,16);}
  function namespaceHint(){try{const source=outerSource($('pkt-outer-source').value);$('pkt-namespace').value=source<0?($('pkt-outer-source').value.trim()==='/'?'原包监测：仅按APID匹配':'来源未设置'):(source===0x0202||source===0x0422?'TMB':'TME')+' / 0x'+source.toString(16).toUpperCase().padStart(4,'0');}catch{$('pkt-namespace').value='数据源格式无效';}}
  $('pkt-outer-source').addEventListener('input',namespaceHint);
  const scalar = { Name: 'pkt-name', Id: 'pkt-id', SourceIP: 'pkt-source', OuterSourceId: 'pkt-outer-source', Apid: 'pkt-apid', Version: 'pkt-version', PacketType: 'pkt-type', SecondaryHeader: 'pkt-secondary', SequenceFlags: 'pkt-seqflags', ExpectedLengthField: 'pkt-lengthfield', ExpectedDatagramLength: 'pkt-datagram', SppOffset: 'pkt-sppoffset', PayloadOffset: 'pkt-payload', PusVersion: 'pkt-pusversion', PusFlags: 'pkt-pusflags', ServiceType: 'pkt-service', ServiceSubtype: 'pkt-subtype', PacketCounter: 'pkt-counter', DestinationApid: 'pkt-destapid', SourceAddress: 'pkt-sourceaddress', DestinationAddress: 'pkt-destaddress', ByteOrder: 'pkt-byteorder', BitOrder: 'pkt-bitorder', CrossByteOrder: 'pkt-crossorder' };
  const rawOption=element('div','form-section');rawOption.innerHTML='<h3>原包监测</h3><label class="check-label"><input id="pkt-monitor-raw" type="checkbox">监测原包（独立于遥测解码）</label><p class="muted small-text">无需关联参数表或启用解码。外层数据源填/时，仅按APID匹配，不检查外层标识、源地址、业务类型与子类型；仍记录实际长度和Fletcher校验结果。自动SPP偏移为6，也可明确填写偏移。随开始监听或文件演示接收，按本包单独保留最近200条SPP源码；重复帧和校验异常包也留存。可在“数据显示 → SPP原包监视”按包筛选、查看并导出CSV，关闭程序后清空。</p>';$('packet-form-error').before(rawOption);
  const boolean = { Enabled: 'pkt-enabled', MonitorRaw: 'pkt-monitor-raw', StrictLength: 'pkt-strict' };
  function packetEnableHint(text=''){setText('pkt-enable-note',text);$('pkt-enable-note').classList.toggle('hidden',!text);}
  $('pkt-enabled').addEventListener('change',()=>packetEnableHint());
  const textFields = new Set(['Name', 'Id', 'SourceIP', 'ByteOrder', 'BitOrder', 'CrossByteOrder']);
  const batchPanel = element('section', 'panel spp-batch-panel'); batchPanel.id = 'spp-batch-panel';
  const batchHeading = element('div', 'panel-heading'), batchHeadingText = element('div');
  batchHeadingText.append(element('h2', '', '批量读取SPP Excel · 一个Sheet一个包'), element('p', '', '从第1张表开始逐表读取；Sheet名称作为新包名称，每包显示外层数据源、TME/TMB分类及16位SPP包标识。')); batchHeading.append(batchHeadingText);
  const batchBody = element('div', 'section-pad'), batchLine = element('div', 'upload-line');
  const batchLabel = element('label', 'file-field', '选择包含多个SPP包的Excel');
  const batchFile = element('input'); batchFile.id = 'spp-batch-file'; batchFile.type = 'file'; batchFile.accept = '.xlsx'; batchLabel.append(batchFile);
  const batchRead = element('button', 'button primary', '读取全部Sheet并预览'); batchRead.id = 'spp-batch-read'; batchRead.type = 'button'; batchLine.append(batchLabel, batchRead);
  const batchCancel = element('button', 'button', '取消'); batchCancel.id = 'spp-batch-cancel'; batchCancel.type = 'button'; batchLine.append(batchCancel);
  addExcelImportClear('spp-batch-clear', 'spp-batch-file', batchCancel, () => {
    if (state.saving) return; batchFile.value = ''; clearBatch(); toast('已清空本次SPP批量读取');
  });
  const batchError = element('div', 'form-error'); batchError.id = 'spp-batch-error'; batchError.setAttribute('role', 'alert');
  const batchPreview = element('div', 'hidden'); batchPreview.id = 'spp-batch-preview';
  const batchSummary = element('div', 'import-summary'); batchSummary.id = 'spp-batch-summary';
  const batchWrap = element('div', 'table-scroll short-table'), batchTable = element('table', 'data-table');
  const batchHead = element('thead'), batchHeadRow = element('tr');
  for (const title of ['Sheet / 操作', '外层数据源 / 分类', 'APID', '主导头前16位', '包数据长度（参考）', '业务类型 / 子类型', '源地址 / 目的地址']) batchHeadRow.append(element('th', '', title));
  batchHead.append(batchHeadRow); const batchRows = element('tbody'); batchRows.id = 'spp-batch-rows'; batchTable.append(batchHead, batchRows); batchWrap.append(batchTable);
  const batchWarnings = element('div', 'import-issues'); batchWarnings.id = 'spp-batch-warnings';
  const batchDetails = element('div'); batchDetails.id = 'spp-batch-details';
  const batchActions = element('div', 'import-action-line');
  const batchDownload = element('button', 'button', '下载预览JSON'); batchDownload.type = 'button'; batchDownload.id = 'spp-batch-download';
  const batchApply = element('button', 'button primary', '批量导入SPP包'); batchApply.type = 'button'; batchApply.id = 'spp-batch-apply';
  batchActions.append(batchDownload, batchApply);
  batchPreview.append(batchSummary, batchWrap, batchWarnings, batchDetails, batchActions);
  batchBody.append(batchLine, element('p', 'muted small-text', '导入后遥测解码与原包监测均先关闭。点击“编辑”按需要启用；仅监测原包时无需关联遥测表。同名文件及Sheet更新原包，其他包保留。任一表有错误时，本次不导入。'), batchError, batchPreview);
  batchPanel.append(batchHeading, batchBody); document.querySelector('.decoder-packets').before(batchPanel);
  function clearBatch() {
    revokeImport(state.batchPreview?.token);
    ++state.batchSequence; state.batchPreview = null; state.batchLoading = false;
    batchSummary.replaceChildren(); batchRows.replaceChildren(); batchDetails.replaceChildren(); batchWarnings.replaceChildren();
    batchApply.textContent = '批量导入SPP包';
    batchRead.disabled = false; batchError.textContent = ''; batchPreview.classList.add('hidden'); setRunning(state.running);
  }
  batchFile.addEventListener('change', clearBatch);
  batchCancel.addEventListener('click', () => { if (state.saving) return; batchFile.value = ''; clearBatch(); toast('已取消SPP批量导入，当前配置未修改'); });
  batchRead.addEventListener('click', async () => {
    const file = batchFile.files[0]; if (!file) { batchError.textContent = '请选择SPP .xlsx文件'; return; }
    if (file.size > 8 * 1024 * 1024) { batchError.textContent = 'SPP Excel最大8MB'; return; }
    clearBatch(); discardImport(false);
    const sequence = state.batchSequence; state.batchLoading = true; batchRead.disabled = true; setRunning(state.running);
    try {
      const response = await fetch('/api/decoders/spp/batch/preview?filename=' + encodeURIComponent(file.name), { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-Token': app.user.csrf }, body: file });
      const data = await response.json(); if (sequence !== state.batchSequence) { revokeImport(data.token); return; }
      if (!response.ok) throw new Error([data.error || '读取失败', ...(data.errors || [])].join('\n'));
      state.batchPreview = data;
      batchSummary.replaceChildren(element('strong', '', data.sourceFile), element('span', '', data.rows.length + '个SPP包'), element('span', '', data.rows.filter(row => row.action === '新增').length + '个新增'), element('span', '', data.rows.filter(row => row.action === '更新').length + '个更新'));
      const rows = document.createDocumentFragment(), details = document.createDocumentFragment();
      for (const item of data.rows) {
        const sheet = item.sheet, f = sheet.Fields, row = element('tr');
        td(row, sheet.Sheet + ' / ' + item.action); td(row, f.OuterSourceId>=0?'0x'+f.OuterSourceId.toString(16).toUpperCase().padStart(4,'0')+' / '+(f.OuterSourceId===0x0202||f.OuterSourceId===0x0422?'TMB':'TME'):sheet.OuterSourceUnrestricted?'/ · 仅APID原包监测':'Excel未提供，需核对现有设置'); td(row, f.Apid + ' / 0x' + f.Apid.toString(16).toUpperCase(), 'mono');
        const head = td(row, sheet.IdentifierHex, 'mono'); head.append(element('div', 'small-text', sheet.IdentifierBits));
        td(row, f.ExpectedLengthField + '（SPP ' + (f.ExpectedLengthField + 7) + '字节）');
        td(row, '0x' + f.ServiceType.toString(16).toUpperCase() + ' / 0x' + f.ServiceSubtype.toString(16).toUpperCase(), 'mono');
        td(row, '0x' + f.SourceAddress.toString(16).toUpperCase().padStart(4, '0') + ' / 0x' + f.DestinationAddress.toString(16).toUpperCase().padStart(4, '0'), 'mono'); rows.append(row);
        const detail = element('details', 'import-details'); detail.append(element('summary', '', sheet.Sheet + ' · 查看9项字段原文与读取结果'));
        for (const field of sheet.Rows) detail.append(element('p', 'small-text spp-field-note', '第' + field.Row + '行 / ' + field.Name + '（' + field.Bits + 'bit）=' + field.Value + '；C列：' + field.Original));
        details.append(detail);
      }
      batchRows.replaceChildren(rows); batchDetails.replaceChildren(details);
      batchWarnings.replaceChildren(...data.warnings.map(message => element('p', 'issue-warning', message)));
      batchApply.textContent = '导入' + data.rows.length + '个SPP包（先停用）'; batchPreview.classList.remove('hidden');
    } catch (e) { if (sequence === state.batchSequence) batchError.textContent = e.message; }
    finally { if (sequence === state.batchSequence) { state.batchLoading = false; batchRead.disabled = false; setRunning(state.running); } }
  });
  batchDownload.addEventListener('click', () => {
    if (!state.batchPreview) return; const anchor = element('a'); anchor.href = '/api/decoders/preview/export?token=' + encodeURIComponent(state.batchPreview.token); anchor.download = 'telemetry-decoder.json'; document.body.append(anchor); anchor.click(); anchor.remove();
  });
  batchApply.addEventListener('click', async () => {
    if (!state.batchPreview || state.saving) return;
    const preview = state.batchPreview; state.saving = true; setRunning(state.running); batchError.textContent = '';
    try { await api('decoders/apply', { token: preview.token, revision: preview.revision }); await changed(); toast('已导入' + preview.rows.length + '个SPP包，请逐包关联参数表并启用'); }
    catch (e) { batchError.textContent = e.message; } finally { state.saving = false; setRunning(state.running); }
  });
  const sppImport = element('section', 'spp-import');
  sppImport.append(element('h3', '', '从Excel读取SPP包定义'), element('p', 'muted small-text', 'B列为位长，C列为值；支持第2行新增数据源，有无新增行均可读取。填写后仍可修改，保存SPP包后生效。'));
  const sppLine = element('div', 'upload-line');
  const sppFileLabel = element('label', 'file-field', '选择SPP Excel');
  const sppFile = element('input'); sppFile.id = 'spp-file'; sppFile.type = 'file'; sppFile.accept = '.xlsx'; sppFileLabel.append(sppFile);
  const sppSheetLabel = element('label', 'spp-sheet-label', '工作表名称');
  const sppSheet = element('input'); sppSheet.id = 'spp-sheet'; sppSheet.placeholder = '留空读取第1张表'; sppSheetLabel.append(sppSheet);
  const sppRead = element('button', 'button', '读取并填入'); sppRead.id = 'spp-read'; sppRead.type = 'button';
  const sppCancel = element('button', 'button', '取消'); sppCancel.id = 'spp-cancel'; sppCancel.type = 'button';
  let sppDraft = null;
  sppLine.append(sppFileLabel, sppSheetLabel, sppRead, sppCancel);
  addExcelImportClear('spp-clear', 'spp-file', sppCancel, () => {
    if (state.saving) return; discardSppRead(); toast('已清空SPP读取，恢复读取前填写的包信息');
  });
  const sppError = element('p', 'form-error'); sppError.id = 'spp-error'; sppError.setAttribute('role', 'alert');
  const sppResult = element('details', 'import-details hidden'); sppResult.id = 'spp-result';
  const sppSummary = element('summary'); sppSummary.id = 'spp-summary';
  const sppTableWrap = element('div', 'table-scroll short-table'), sppTable = element('table', 'data-table');
  const sppHead = element('thead'), sppHeadRow = element('tr');
  for (const name of ['来源行 / 字段', '位长', 'C列原文', '读取值']) sppHeadRow.append(element('th', '', name));
  sppHead.append(sppHeadRow); const sppRows = element('tbody'); sppRows.id = 'spp-import-rows'; sppTable.append(sppHead, sppRows); sppTableWrap.append(sppTable);
  sppResult.append(sppSummary, sppTableWrap);
  sppImport.append(sppLine, sppError, sppResult, element('p', 'muted small-text', '其他字段保留当前填写值。请继续核对参数表关联及用户域起点；校验统一使用Fletcher-16。'));
  $('packet-dialog').querySelector('.packet-form-body').prepend(sppImport);
  function clearSppImport() {
    ++state.sppSequence; state.sppLoading = false; sppRead.disabled = false;
    sppError.textContent = ''; sppResult.classList.add('hidden'); setRunning(state.running);
  }
  sppFile.addEventListener('change', clearSppImport); sppSheet.addEventListener('input', clearSppImport);
  $('packet-dialog').addEventListener('close', clearSppImport);
  function discardSppRead() {
    if (sppDraft) {
      for (const [id, value] of Object.entries(sppDraft.values)) $(id).value = value;
      $('pkt-enabled').checked = sppDraft.enabled;$('pkt-monitor-raw').checked=!!sppDraft.monitorRaw; packetEnableHint(sppDraft.note); identifier();
    }
    sppDraft = null; sppFile.value = ''; sppSheet.value = ''; clearSppImport(); sppRows.replaceChildren(); sppSummary.textContent = '';
    sppResult.open = false;
  }
  sppCancel.addEventListener('click', () => {
    if (state.saving) return; discardSppRead();
    toast('已取消SPP读取，恢复读取前填写的包信息');
  });
  sppRead.addEventListener('click', async () => {
    const file = sppFile.files[0]; if (!file) { sppError.textContent = '请先选择SPP .xlsx文件'; return; }
    if (file.size > 8 * 1024 * 1024) { sppError.textContent = 'SPP Excel最大8MB'; return; }
    if (!sppDraft) sppDraft = { values: Object.fromEntries(Object.values(scalar).map(id => [id, $(id).value])), enabled: $('pkt-enabled').checked, monitorRaw:$('pkt-monitor-raw').checked, note: $('pkt-enable-note').textContent };
    const sequence = ++state.sppSequence; state.sppLoading = true; sppRead.disabled = true; setRunning(state.running);
    sppError.textContent = ''; sppResult.classList.add('hidden');
    try {
      const response = await fetch('/api/decoders/spp/preview?filename=' + encodeURIComponent(file.name) + '&sheet=' + encodeURIComponent(sppSheet.value.trim()), {
        method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-Token': app.user.csrf }, body: file
      });
      const data = await response.json(); if (sequence !== state.sppSequence) return;
      if (!response.ok) throw new Error(data.error || 'SPP读取失败');
      const previousSecondary = $('pkt-secondary').value;
      for (const [key, value] of Object.entries(data.Fields)) { if(key==='OuterSourceId'&&value<0){if(data.OuterSourceUnrestricted)$(scalar[key]).value='/';continue;} $(scalar[key]).value = ['OuterSourceId','Apid', 'ServiceType', 'ServiceSubtype', 'SourceAddress', 'DestinationAddress'].includes(key) ? '0x' + value.toString(16).toUpperCase() : value; }
      if ($('pkt-secondary').value !== previousSecondary) $('pkt-secondary').dispatchEvent(new Event('change'));
      $('pkt-enabled').checked = false;$('pkt-monitor-raw').checked=false;packetEnableHint('已读入新的SPP格式，请重新核对并按需要勾选遥测解码或监测原包。');identifier();
      sppSummary.textContent = data.SourceFile + ' / ' + data.Sheet + ' · 已填入9项 · 主导头前16位 ' + data.IdentifierHex + ' / ' + data.IdentifierBits;
      const fragment = document.createDocumentFragment();
      for (const field of data.Rows) { const row = element('tr'); td(row, '第' + field.Row + '行 / ' + field.Name); td(row, field.Bits + ' bit'); td(row, field.Original); td(row, field.Field==='OuterSourceId'&&field.Value<0?'/ · 仅APID原包监测':field.Value + ' / 0x' + field.Value.toString(16).toUpperCase(), 'mono'); fragment.append(row); }
      sppRows.replaceChildren(fragment); sppResult.classList.remove('hidden'); sppResult.open = true;
    } catch (e) { if (sequence === state.sppSequence) sppError.textContent = e.message; }
    finally { if (sequence === state.sppSequence) { state.sppLoading = false; sppRead.disabled = false; setRunning(state.running); } }
  });
  function integer(text) {
    const s = String(text).trim(); if (!/^(?:-?\d+|0[xX][0-9a-fA-F]+|0[bB][01]+)$/.test(s)) throw new Error('请输入十进制整数、0x十六进制或0b二进制：' + s);
    const result = Number(s); if (!Number.isSafeInteger(result)) throw new Error('数值过大'); return result;
  }
  function newPacket() {
    return { Id: 'packet-' + Date.now().toString(36), Name: '新SPP遥测包', Enabled: false, MonitorRaw:false, SourceIP: '', OuterSourceId: -1, Version: 0, PacketType: 0, SecondaryHeader: 1, Apid: 8, SequenceFlags: 3, ExpectedLengthField: 1017, ExpectedDatagramLength: 0, SppOffset: -1, PayloadOffset: 16, StrictLength: true, PusVersion: 3, PusFlags: 0, ServiceType: 3, ServiceSubtype: 25, PacketCounter: 0, DestinationApid: 0, SourceAddress: 4100, DestinationAddress: 0, ByteOrder: 'big', BitOrder: 'msb0', CrossByteOrder: 'firstMsb', ChecksumMode: 'fletcher16', ChecksumStart: 0, MappingConfirmed: false, Groups: [] };
  }
  function openPacket(packet = null) {
    if (!state.configuration) return;
    clearSppImport(); sppDraft = null; sppFile.value = ''; sppSheet.value = '';
    const p = packet || newPacket(); state.editPacket = p; state.editRevision = state.revision; $('packet-dialog-title').textContent = packet ? '编辑SPP包 · ' + packet.Name : '新增SPP包'; $('packet-form-error').textContent = '';
    for (const [key, id] of Object.entries(scalar)) $(id).value = key==='OuterSourceId'?(p[key]>=0?'0x'+p[key].toString(16).toUpperCase().padStart(4,'0'):p.MonitorRaw&&!p.Enabled?'/':''):p[key];
    for (const [key, id] of Object.entries(boolean)) $(id).checked = !!p[key];
    $('pkt-enabled').checked=!!p.Enabled&&!!p.MappingConfirmed;packetEnableHint(!!p.Enabled!==!!p.MappingConfirmed?'当前包尚未同时启用并确认映射，勾选保存后会同时开启。':'');
    for (const key of ['Apid', 'ServiceType', 'ServiceSubtype', 'SourceAddress', 'DestinationAddress']) $(scalar[key]).value = '0x' + p[key].toString(16).toUpperCase();
    $('pkt-id').readOnly = !!packet;
    const groups = new Map(); for (const parameter of state.configuration.Catalog.Parameters) groups.set(parameter.Group, (groups.get(parameter.Group) || 0) + 1);
    const fragment = document.createDocumentFragment();
    for (const [name, count] of groups) { const label = element('label', 'packet-group'); const input = element('input'); input.type = 'checkbox'; input.value = name; input.checked = p.Groups.includes(name); label.append(input, element('span', '', name), element('small', '', count + '项')); fragment.append(label); }
    $('pkt-groups').replaceChildren(fragment); identifier(); $('packet-dialog').showModal();
  }
  function collectPacket() {
    const packet = { SppSourceFile: state.editPacket?.SppSourceFile || '', SppSourceSheet: state.editPacket?.SppSourceSheet || '' };
    for (const [key, id] of Object.entries(scalar)) packet[key] = key==='OuterSourceId'?outerSource($(id).value):textFields.has(key) ? $(id).value.trim() : integer($(id).value);
    for (const [key, id] of Object.entries(boolean)) packet[key] = $(id).checked;
    packet.MappingConfirmed=packet.Enabled;
    packet.Groups = [...$('pkt-groups').querySelectorAll('input:checked')].map(input => input.value); return packet;
  }
  function identifier() {
    namespaceHint();
    try {
      const values = ['pkt-version', 'pkt-type', 'pkt-secondary', 'pkt-apid'].map(id => integer($(id).value));
      if (values.some((v, i) => v < 0 || v > [7, 1, 1, 2047][i])) throw new Error('主导头字段超出位长');
      const id = (values[0] << 13) | (values[1] << 12) | (values[2] << 11) | values[3];
      $('packet-identifier').textContent = '主导头前16位：' + id.toString(2).padStart(16, '0') + ' / 包标识 0x' + id.toString(16).toUpperCase().padStart(4, '0') + '（3+1+1+11bit，由高到低） · 总SPP长度=长度字段+7';
    } catch { $('packet-identifier').textContent = '请填写有效主导头字段：版本0～7、类型0～1、副导头0～1、APID 0～2047'; }
  }
  ['pkt-version', 'pkt-type', 'pkt-secondary', 'pkt-apid'].forEach(id => $(id).addEventListener('input', identifier));
  $('pkt-secondary').addEventListener('change', () => { $('pkt-payload').value = $('pkt-secondary').value === '0' ? '6' : '16'; });
  $('new-packet').addEventListener('click', () => openPacket()); ['close-packet-dialog', 'cancel-packet-dialog'].forEach(id => $(id).addEventListener('click', () => $('packet-dialog').close()));
  $('packet-form').addEventListener('submit', async event => {
    event.preventDefault(); if (state.saving || state.sppLoading) return; state.saving = true; setRunning(state.running); $('packet-form-error').textContent = '';
    try { await api('decoders/packet', { packet: collectPacket(), revision: state.editRevision }); $('packet-dialog').close(); await changed(); toast('SPP包配置已保存'); }
    catch (e) { $('packet-form-error').textContent = e.message; } finally { state.saving = false; setRunning(state.running); }
  });
  $('use-received-packet').addEventListener('click', async () => { try { const packet = await api('packet'); if (!packet.rawHex) throw new Error('当前没有收到UDP报文'); $('try-hex').value = packet.rawHex; } catch (e) { toast(e.message, true); } });
  $('try-decode').addEventListener('click', async () => {
    $('try-error').textContent = ''; $('try-result').classList.add('hidden'); $('try-decode').disabled = true;
    try {
      state.trial = await api('decoders/try', { packetId: $('try-packet').value, hex: $('try-hex').value });
      const p = state.trial.packet; $('try-header').replaceChildren(element('strong', '', p.name), element('span', '', p.identifier), element('span', '', '计数 ' + p.sequence), element('span', '', 'SPP ' + p.sppLength + '字节 / 偏移 ' + p.sppOffset), element('span', '', '校验' + p.checksum));
      $('try-result').classList.remove('hidden'); renderTrial();
    } catch (e) { $('try-error').textContent = e.message; } finally { $('try-decode').disabled = !state.configuration?.Packets.length; }
  });
  function renderTrial() {
    if (!state.trial) return; const query = $('try-result-search').value.toLowerCase().trim(); const fragment = document.createDocumentFragment();
    for (const p of state.trial.rows.filter(p => (parameterSearchText(p) + ' ' + p.group).toLowerCase().includes(query))) {
      const row = element('tr'); const name = td(row, p.name + ' / ' + parameterReference(p)); name.append(element('div', 'muted small-text', p.group + ' · 第' + p.sourceRow + '行')); name.title = parameterValueReference(p);
      td(row, p.raw || '—', 'mono'); td(row, p.value + (p.unit ? ' ' + p.unit : '')); const status = td(row, p.status); if (p.diagnostic) status.append(element('div', 'muted small-text', p.diagnostic)); fragment.append(row);
    }
    $('try-rows').replaceChildren(fragment);
  }
  $('try-result-search').addEventListener('input', renderTrial);
  return { load, setRunning, resetImport: () => discardImport(true,false), importPreview: () => state.preview,
    acceptImportReview(data) { if (state.preview?.token !== data.token) return false; state.preview = data; renderImport(); return true; },
    importReviewBusy(busy) { state.reviewBusy = busy; setRunning(state.running); } };
})();
