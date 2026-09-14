// ---- 域间依赖(拆分脚本自动生成) ----
import { downloadTemplate, exportCsv, refreshBatchRows, renderBatchRows, renderColHelp, runHisBatch, setBatchLimits, uploadCsv } from './batch.js';
import { ChipField } from './chips.js';
import { $, $$, api, bindDialogChrome, bindTablist, confirmDlg, fmtInt, initMaskToggle, rovingArrows, setHidden, showErr } from './dom.js';
import { initFeeForm } from './fee.js';
import { resetForm, runSingle, stashResultPlaceholder, toggleWhatIfPanel } from './group.js';
import { openExampleDlg, refreshPickedBar, runHisFetch } from './his.js';
import { switchTab } from './nav.js';
import { codeVersion, ensureCodeVersions, ensureRegions, fields, restoreCodeVersion, rerunLastCase, setCodeVersion, setCodeVersionChangedHook, state } from './state.js';
import { initTheme } from './theme.js';

'use strict';

// ============================================================================
// DRG 分组工作台 — 前端逻辑(原生 JS,无构建步骤,与后端零边界)
//
// 对接后端 API:
//   GET  /api/info                方案/计数
//   GET  /api/search?type&q&limit 字典检索(编码/名称前缀与包含)
//   GET  /api/lookup?type&code    精确查码(含映射后的标准码)
//   POST /api/names {codes}        批量取名称(并发症/操作回显)
//   POST /api/group {…}            单病例分组(携带判定轨迹)
//   POST /api/group/batch/csv       批量分组(上传 HQMS 首页 CSV/XLSX,form-data: file)
// ============================================================================

// -------------------------------- 初始化 ----------------------------------

export function init() {
  initTheme();
  stashResultPlaceholder();

  // 标签切换:点击 + 方向键/Home/End + roving tabindex(与子标签、批量来源共用同一实现)
  $$('.tab').forEach(t => t.addEventListener('click', () => switchTab(t.dataset.tab)));
  bindTablist($('.tabs'), '.tab', el => switchTab(el.dataset.tab));

  // 性别二选一
  $$('.seg-item').forEach(b => b.addEventListener('click', () => {
    state.gender = b.dataset.gender;
    $$('.seg-item').forEach(x => { x.classList.toggle('is-active', x === b); x.setAttribute('aria-checked', x === b); });
  }));
  // radio 方向键:←/→ 互斥切换(点击逻辑复用,保持 state 同步)
  $$('.seg').forEach(seg => rovingArrows(seg, '.seg-item', b => b.click()));

  // 姓名脱敏(显示层):两个 HIS 入口共用一个设置;切换后重绘已渲染的姓名,
  // 否则屏幕上会留着切换前的真名,看起来像没生效。
  initMaskToggle(() => { refreshPickedBar(); refreshBatchRows(); }, '#maskNameChk', '#maskNameChkBatch');

  // 新生儿病案字段
  const neoToggle = $('#neoToggle');
  const neoFields = $('#neoFields');
  neoToggle.addEventListener('click', () => {
    const open = neoFields.hidden;
    if (open) { setHidden(neoFields, false); neoToggle.setAttribute('aria-expanded', 'true'); }
    else {
      setHidden(neoFields, true); neoToggle.setAttribute('aria-expanded', 'false');
      $('#ageDayInput').value = ''; $('#weightInput').value = '';
    }
  });

  // 编码芯片字段。primary 标记"主要"位置:该处不显示 MCC/CC 徽章
  // (CHS-DRG 的并发症分档只针对其他诊断,引擎判 MCC/CC 时也只遍历 otherDiagnoses)。
  fields.mainDx = new ChipField($('#mainDxBox'), 'diagnosis', { single: true, primary: true });
  fields.otherDx = new ChipField($('#otherDxBox'), 'diagnosis');
  fields.mainProc = new ChipField($('#mainProcBox'), 'procedure', { single: true, primary: true });
  fields.otherProc = new ChipField($('#otherProcBox'), 'procedure');
  initCodeVersionSwitch();

  // 换主诊/手术模拟:入口在「诊断与手术操作」节标题旁,弹窗原生 dialog(Esc/焦点陷阱自带)
  $('#whatIfToggle').addEventListener('click', toggleWhatIfPanel);
  bindDialogChrome($('#whatIfDlg'));

  // 示例病例:同款 dialog 交互(backdrop/Esc 关闭),点选病例即填入表单并自动分组
  $('#exampleBtn').addEventListener('click', openExampleDlg);
  bindDialogChrome($('#exampleDlg'));
  // 结果区空态里的「看一个示例病例」:用委托绑定 —— 空态 DOM 在 resetForm 时会被整体还原,
  // 直接绑定到节点上会随还原失效。
  $('#resultPane').addEventListener('click', e => {
    if (e.target.closest('#placeholderExample')) openExampleDlg();
  });

  // 单病例
  $('#recordForm').addEventListener('submit', e => { e.preventDefault(); runSingle(); });
  $('#resetBtn').addEventListener('click', async () => {
    const ok = await confirmDlg({
      title: '清空当前录入',
      body: '将清除已录入的患者信息、诊断与手术编码、实际费用，此操作不可撤销。',
      confirmText: '清空', danger: true,
    });
    if (ok) resetForm();
  });

  // 批量
  $('#batchCsvTop').addEventListener('click', exportCsv);
  $('#batchCsvUpload').addEventListener('click', uploadCsv);
  // 重新选文件即清掉上一次的错误:否则"已修改但留着旧报错"会让人以为没改动
  $('#batchFile').addEventListener('change', () => showErr('#batchFileError'));
  $('#batchTplHqms').addEventListener('click', () => downloadTemplate('hqms'));
  $('#hisBatchRun').addEventListener('click', runHisBatch);
  $('#hisBatchFrom').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); runHisBatch(); } });
  $('#hisBatchTo').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); runHisBatch(); } });
  // 默认查询区间:最近 7 天(含今天),按本地时区取整
  const now = new Date(), pad = n => String(n).padStart(2, '0');
  const iso = d => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const weekAgo = new Date(now); weekAgo.setDate(now.getDate() - 6);
  $('#hisBatchFrom').value = iso(weekAgo);
  $('#hisBatchTo').value = iso(now);
  renderColHelp('hqms');

  // 病案提取(单病例左侧输入区入口,扫码枪友好:回车即提取并分组)
  $('#hisFetch').addEventListener('click', runHisFetch);
  $('#hisIdInput').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); runHisFetch(); } });

  // 批量来源切换(HIS 按日期 / 上传文件,同一结果区)。
  // 抽成具名函数供鼠标与键盘共用;激活项变化后重算 roving tabindex。
  const selectBatchSrc = src => {
    $$('#batchSrcSeg .seg-tab').forEach(x => {
      const on = x.dataset.src === src;
      x.classList.toggle('is-active', on);
      x.setAttribute('aria-selected', String(on));
    });
    ['his', 'file'].forEach(s => { $('#bsrc-' + s).hidden = s !== src; });
  };
  const syncSrcTabs = bindTablist($('#batchSrcSeg'), '.seg-tab', el => selectBatchSrc(el.dataset.src));
  $$('#batchSrcSeg .seg-tab').forEach(b => b.addEventListener('click', () => {
    selectBatchSrc(b.dataset.src);
    syncSrcTabs();
  }));
  $('#batchOnlyIssues').addEventListener('change', renderBatchRows);

  loadInfo();
  ensureRegions().then(initFeeForm);  // 左侧「费用测算」节(无地区包时整体隐藏)
}

// -------------------------------- 编码版本(顶栏) --------------------------------

/** 顶栏「编码 国临版/医保版」:全局输入口径,单病例/批量/HIS 三条录入通道共用。
    首屏高亮由 <html data-codever> 在样式表加载前定好,这里只负责交互与同步。 */
function initCodeVersionSwitch() {
  restoreCodeVersion();
  const seg = $('#codeVerSeg');
  if (!seg) return;
  const items = $$('.cv-item', seg);
  const sync = () => {
    items.forEach(b => {
      const on = b.dataset.ver === codeVersion;
      b.classList.toggle('is-active', on);
      b.setAttribute('aria-checked', String(on));
      b.tabIndex = on ? 0 : -1;   // roving tabindex:整组只占一个 Tab 位
    });
  };
  const pick = b => { if (b.dataset.ver !== codeVersion) setCodeVersion(b.dataset.ver); sync(); };
  items.forEach(b => b.addEventListener('click', () => pick(b)));
  // 方向键互斥切换(与性别 seg 同一套交互,避免同一页面两种键盘语义)
  rovingArrows(seg, '.cv-item', pick);
  // 切换后:芯片按新版本重绘(名称/徽标口径跟随),在看的单病例用新口径重算一次。
  // 批量结果保持分组时的口径快照,不重算 —— 与区域切换同一约定。
  setCodeVersionChangedHook(() => {
    Object.values(fields).forEach(f => f.render());
    rerunLastCase();
  });
  sync();
  // 选项说明与两版字典规模来自 /api/info:前端不写死标签与数字(否则迟早与后端口径分家)
  ensureCodeVersions().then(options => {
    options.forEach(o => {
      const b = items.find(x => x.dataset.ver === o.id);
      if (!b) return;
      b.textContent = o.label || b.textContent;
      b.title = `${o.title || ''}（诊断 ${fmtInt(o.diagnoses)} / 手术操作 ${fmtInt(o.procedures)}）`;
    });
  });
}

// -------------------------------- 方案信息 --------------------------------

export async function loadInfo() {
  try {
    const info = await api.get('/api/info');
    const chip = $('#packChip');
    // 页头只显示官方数据批次(sourceDate):医保局对 3.0 只公开过一版配置信息,
    // 显示 rev N 会被读成"官方发过 N 版"。sourceDate 缺失时退回 revision,兼容不含该字段的旧包。
    const batch = info.sourceDate || `rev ${info.revision}`;
    chip.textContent = `${info.scheme} ${info.version} · ${batch}`;
    setHidden(chip, false);
    setBatchLimits(info.limits);   // 上传上限只在服务端定义一处,前端据此显示与预检
    const c = info.counts || {};
    const note = $('.foot-note');
    if (note) note.title =
      `分组规则源自国家医保局公开发布的 CHS-DRG 3.0 分组方案 · DRG 编码库（方案数据包）${info.scheme} ${info.version} ` +
      `(${batch}) · 诊断 ${fmtInt(c.diagnoses)} / 操作 ${fmtInt(c.procedures)} / DRG 组 ${fmtInt(c.groups)}`;
  } catch { /* 后端不可达时静默,界面仍可手工录入 */ }
}

// -------------------------------- 启动 ------------------------------------

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();
