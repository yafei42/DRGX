// ---- 域间依赖(拆分脚本自动生成) ----
import { $, api, badgeEl, copyText, csvCell, downloadCsv, el, fmtInt, maskName, maskNameEnabled, setHidden, showErr } from './dom.js';
import { codeVersionParam, currentRegion, regionParam } from './state.js';

export let lastBatch = [];   // 最近一次批量分组结果(筛选/导出数据源)

/** 计数盘列数(与 .batch-summary 的 grid-template-columns 保持一致)。 */
const SUM_SLOTS = 4;

// -------------------------------- 批量 ------------------------------------

export function sumChip(label, val, cls) {
  return el('span', { class: 'sum-chip ' + (cls || '') }, [
    document.createTextNode(label + ' '),
    el('b', { text: String(val) }),
  ]);
}

export function renderBatch(data) {
  const out = $('#batchOut');
  setHidden(out, false);

  const summary = $('#batchSummary');
  summary.innerHTML = '';
  const total = data.total || 0, ok = data.ok || 0, fail = data.fail || 0, perr = data.parseErrors || 0;
  const chips = [
    sumChip('总计', total, ''),
    sumChip('入组', ok, 'ok'),
    sumChip('未入组/歧义', fail, fail ? 'err' : ''),
  ];
  if (perr) chips.push(sumChip('解析失败', perr, 'warn'));
  // skipped:服务端"静默跳过超限行"时代的遗留字段,现在超限是直接拒绝(413),
  // 该值恒为 0;留着只为兼容旧响应 —— 真要显示时它必须可见,否则用户会以为已全部处理
  if (data.skipped) chips.push(sumChip('超限跳过', data.skipped, 'warn'));
  // 计数盘固定 4 列(见 .batch-summary):把格数补齐到 4 的整数倍 ——
  // 否则 3 项会被 auto-fit 各拉成 1/3 宽,20px 数字浮在巨大空框里,且行数变化时列宽跳动。
  while (chips.length % SUM_SLOTS !== 0) {
    chips.push(el('div', { class: 'sum-chip is-empty', 'aria-hidden': 'true' }));
  }
  chips.forEach(c => summary.appendChild(c));

  lastBatch = data.rows || [];
  // 行号列表头随数据源切换:有就诊号时显示就诊号,否则显示行号
  const hasPatientId = lastBatch.some(r => r.patientId != null);
  $('#batchTable thead th:first-child').textContent = hasPatientId ? '就诊号' : '行';
  // 全部正常时「只看异常」无意义,隐藏开关;有异常则显示并默认保持上次状态
  setHidden($('#batchFilterWrap'), !lastBatch.some(isBatchIssue));
  renderBatchRows();
}

/** 异常行 = 解析失败 / 未入组 / 歧义(病案提取失败也计入)。 */
export function isBatchIssue(row) {
  return !!row.error || (!!row.status && row.status !== 'Success');
}

/** 分片渲染粒度:每片行数。行数大时一次性 append 会长时间阻塞主线程(页面假死)。 */
/**
 * 渲染分块数(不是"每块几行")。
 *
 * <b>为什么按总行数分块,而不是固定每块 400 行</b>:每 appendChild 一批,
 * 浏览器都要为**整张表**重算样式与布局。固定 400 行时,20,000 行会被切成 50 批,
 * 重排成本按批数累加 → 实测 <b>整表 37.9 秒</b>(60 万元素、每批都要把已渲染的行再排一遍)。
 * 这是典型的 O(n²):行数越多越糟,而块数固定后总重排次数恒定。
 * 取 6:实测 20,000 行(60 万元素)从 37.9s 降到 11.1s;试到 3 只再快 0.8s(10.3s,在噪声内),
 * 但首屏会更晚出现 —— 所以宁可多留一次 tick。
 * 剩余成本主要在建 60 万个元素本身;再往下要走窗口化渲染(只建可视区的行),
 * 那是另一件事,不是调参数能解决的。
 */
const CHUNK_TICKS = 6;
/** 渲染轮次令牌:切换「只看异常」/重新批量时会重入,过期的一轮必须立即停手。 */
let renderToken = 0;

/** 脱敏开关变化后重绘表格(单元格里的姓名摘要需按新状态重算);无批量结果时不动。 */
export function refreshBatchRows() {
  if (lastBatch.length) renderBatchRows();
}

/** 截断提示:表里不全时必须说清"不全在哪、全的怎么取" —— 否则用户会把预览当全量(与静默截断同罪)。
 *  两种口径要分开讲,因为它们的截断原因不同:
 *   - 预览:正常结果按位置截断(前 N 行),看全量走「导出 CSV」;
 *   - 异常:只有异常行多到撞上硬上限才截断,并给出实际条数。 */
function renderPreviewNote(hit, shown, only) {
  const box = $('#batchPreviewNote');
  if (!box) return;
  if (hit <= shown) { setHidden(box, true); box.textContent = ''; return; }
  setHidden(box, false);
  // 两个视图(全部 / 只看异常)用的是同一个上限,所以文案不再宣称"只看异常不受限制"
  // —— 那是旧口径(异常段曾放宽到 2 万行)留下的说法,再留着就是骗人。
  box.textContent = only
    ? `异常行 ${fmtInt(hit)} 条,表内显示前 ${fmtInt(shown)} 条(页面承载上限);完整结果请点右上「导出 CSV」`
    : `本次共 ${fmtInt(hit)} 行,表内显示前 ${fmtInt(shown)} 行(页面承载上限);`
      + `完整结果请点右上「导出 CSV」`;
}

/** 按「只看异常」开关渲染表格主体;新结果沿用当前开关状态。
 *
 *  两种模式的行数上限不同,因为诉求不同:
 *   - 正常模式:预览 detailRows 行(一行一份 DOM,不虚拟滚动)。
 *   - 只看异常:按 issueRows 全量渲染 —— 上传大文件要看的恰恰是"哪些没入组、为什么",
 *     把它截到预览那点行数等于把功能做废。
 *  两个数都只约束**浏览器建多少行 DOM**,与服务端回传无关:批量行已不携带判定明细
 *  (服务端 TrimDetail 一律裁掉 trace / 编码映射 / 排除表命中),逐行展开也已移除 ——
 *  要逐条分析请走「导出 CSV」。(两个数都由 /api/info 下发,见 setBatchLimits。)
 *
 *  行数 ≤ 一片时同步插完(调用方随后即可读到 DOM),超过一片分帧插入,每片让出主线程。 */
export function renderBatchRows() {
  const tbody = $('#batchTable tbody');
  const token = ++renderToken;
  tbody.innerHTML = '';
  const only = $('#batchOnlyIssues').checked;
  const hit = only ? lastBatch.filter(isBatchIssue) : lastBatch;   // 过滤/导出始终用全量
  const cap = only ? issueRows : detailRows;
  const rows = hit.length > cap ? hit.slice(0, cap) : hit;
  renderPreviewNote(hit.length, rows.length, only);
  // 过滤后空表给一行说明,避免"表格消失"的困惑
  if (only && !rows.length) {
    tbody.appendChild(el('tr', {}, [el('td', { colspan: batchColspan(), class: 'cell-dim', text: '无异常行' })]));
    return;
  }

  let i = 0;
  const step = () => {
    if (token !== renderToken) return; // 已有更新的一轮渲染接过表格
    const frag = document.createDocumentFragment();
    const end = Math.min(i + rows.length / CHUNK_TICKS, rows.length);
    for (; i < end; i++) frag.appendChild(batchRow(rows[i]));
    tbody.appendChild(frag);
    if (i < rows.length) setTimeout(step, 0);
  };
  step();
}

/** 批量表列数:直接读表头 <th> 数量,避免与列定义硬耦合(改列时无需同步改 colspan)。 */
function batchColspan() {
  const head = $('#batchTable thead tr');
  return head && head.children.length ? head.children.length : 10;
}

/** 编码明细单元格:列表一行放不下几十条码,故单行显示 + 悬停列出全部;
 *  完整清单在展开详情与导出 CSV 里 —— 那里才是逐条分析的地方。 */
function codeCell(cls, joined, count) {
  const codes = (joined || '').split(';').filter(Boolean);
  if (!codes.length) return el('td', { class: cls }, [el('span', { class: 'cell-dim', text: '—' })]);
  const text = codes.join(', ');
  return el('td', { class: cls }, [
    el('span', { class: 'code-line', text, title: `共 ${count != null ? count : codes.length} 条:\n${codes.join('\n')}` }),
  ]);
}

/** 批量结果行。列序:识别(行/病案号) → 病例(性别/年龄/主诊断/其他诊断/手术)
 *  → 分组结果(结果/MDC/ADRG/DRG/名称/RW/参考费用) → 说明。
 *  病例与结果各字段<b>分别成列、不合并</b> —— 合并成一句话就没法按列排序/筛选,
 *  也读不出"哪个诊断把这条顶到了这一档"。 */
export function batchRow(row) {
  const tr = el('tr');
  const isErr = !!row.error;
  const copyable = row.code ? String(row.code) : null;
  let stClass = 'ok', stText = row.statusText || '';
  if (isErr) { stClass = 'err'; stText = row.statusText || '解析失败'; }
  else if (row.status === 'Success') stClass = 'ok';
  else if (row.status === 'Ambiguous') stClass = 'warn';
  else stClass = 'err';

  const cells = [
    el('td', { class: 'col-line', text: row.patientId != null ? row.patientId : String(row.line) }),
    el('td', { class: 'col-recno', text: row.recordNo || '—', title: row.recordNo || '' }),
    // 姓名与证件号码是个人信息:屏幕一律过脱敏开关(证件号脱敏时只留前 6 后 4);
    // 导出 CSV 不脱敏 —— 用户已定"屏幕脱敏、文件不脱敏"。
    el('td', { class: 'col-name', text: row.patientName ? maskName(row.patientName) : '—' }),
    el('td', { class: 'col-idno', text: row.idNo ? (maskNameEnabled() ? row.idNo.slice(0, 6) + '********' + row.idNo.slice(-4) : row.idNo) : '—' }),
    el('td', { class: 'col-sex', text: row.gender === '1' ? '男' : row.gender === '2' ? '女' : '—' }),
    el('td', { class: 'col-age', text: row.age || '—' }),
    el('td', { class: 'col-dx', text: row.mainDx || '—', title: row.mainDx || '' }),
    codeCell('col-dxs', row.otherDx, row.otherDxCount),
    codeCell('col-proc', row.procs, row.procCount),
    el('td', { class: 'col-status' }, [el('span', { class: 'st ' + stClass, text: stText })]),
    el('td', { class: 'col-mdc', text: row.mdc || '—' }),
    el('td', { class: 'col-adrg', text: row.adrg || '—' }),
    // 直赋档在批量表里直接标出(与单病例同一判定):落位由特殊身份条件决定,不看合并症
    el('td', { class: 'col-drg' }, [el('span', { class: 'code-line', text: row.code || '—' }), row.drgDirect ? badgeEl({
      tone: 'info', text: row.drgDirect,
      title: '落位由特殊身份条件直赋:主诊断命中高危妊娠清单 / 手术操作命中机器人触发码,不按 MCC/CC 分档',
    }) : null]),
    el('td', { class: 'col-gname', text: row.name || '' }),
    el('td', { class: 'num col-rw', text: row.weight != null ? String(row.weight) : '' }),
    el('td', { class: 'num col-cost', text: row.cost != null ? String(row.cost) : '' }),
    // 费用测算明细:逐列摊开(与单病例「费用测算」同一个 FeeEstimate 结果,不是另算一遍)
    el('td', { class: 'num col-factor', text: row.factor || '' }),
    el('td', { class: 'num col-pv', text: row.pointValue || '' }),
    el('td', { class: 'num col-tp', text: row.totalPoint || '' }),
    el('td', { class: 'num col-std', text: row.standardFee || '' }),
    el('td', { class: 'num col-est', text: row.estimatedFee || '' }),
    // 倍率类型是文字(正常/高倍率/低倍率),阈值放进 tooltip,免得再占两列
    el('td', { class: 'col-rate', text: row.rateType || '', title: [row.lowRateFee ? '低倍率阈值 ' + row.lowRateFee : '', row.highRateFee ? '高倍率阈值 ' + row.highRateFee : ''].filter(Boolean).join('  ') }),
    el('td', { class: 'cell-dim row-reason', text: isErr ? row.error : (row.reason || '') }),
    el('td', { class: 'row-copy-cell' }, copyable ? [el('button', {
      type: 'button', class: 'copy-btn', text: '复制',
      title: '复制 DRG ' + copyable,
      onclick: e => { e.stopPropagation(); copyText(copyable, e.currentTarget); },
    })] : null),
  ];
  cells.forEach(c => tr.appendChild(c));

  return tr;
}

/** 导出完整结果(全量,不受预览/异常行渲染上限影响)。
 *
 *  <b>列与结果表逐列对应、一个都不少</b>:导出是"拿去做后续分析"的交付物,屏幕上有多少
 *  可用于分析的字段,文件里就得有多少 —— 少一列就要回原始文件里人工拼,而原始文件的列名是
 *  HQMS 字段代号(A48/C03C…),人工拼极易错位。所以这里宁可列宽也不合并。
 *
 *  <b>导出不做脱敏</b>:脱敏开关的定位是"别让路过的人从屏幕上看到姓名/证件号"
 *  (见 dom.js 的 maskName 注释:它挡的是「被人看到」,不是「被人取到」)。
 *  导出的 CSV 是"完整结果"这个交付物本身,交给做分析/对账的人,脱敏会直接毁掉可用性。
 *  换句话说:屏幕脱敏,文件不脱敏 —— 需要访问控制的场合请前置认证。 */
export function exportCsv() {
  if (!lastBatch.length) return;
  const hasHisFee = lastBatch.some(r => r.fee != null); // HIS 批量结果带 HIS 总费用
  const hasPatientId = lastBatch.some(r => r.patientId != null);
  const header = [
    hasPatientId ? '就诊号' : '行',
    '病案号', '姓名', '证件号码', '性别', '年龄', '入院时间', '出院时间', '住院次数',
    '主要诊断', '其他诊断', '手术操作',
    '结果', 'MDC', 'ADRG', 'DRG', 'DRG名称', 'RW(相对权重)', '参考费用(元)',
    '系数', '点值(元/点)', '点数', '支付标准(元)', '估算支付(元)', '倍率', '低倍率阈值(元)', '高倍率阈值(元)',
    ...(hasHisFee ? ['HIS总费用(元)'] : []),
    '说明', '直赋档',
  ];
  const lines = [header.join(',')];
  const s = v => (v == null ? '' : v);            // null/undefined → 空串(不写 "null" 混进分析)
  lastBatch.forEach(r => {
    const isErr = !!r.error;
    const stText = isErr ? (r.statusText || '解析失败') : (r.statusText || '');
    const cells = [
      r.patientId != null ? r.patientId : r.line,
      s(r.recordNo),
      s(r.patientName),          // 不脱敏:见上方说明
      s(r.idNo),
      r.gender === '1' ? '男' : r.gender === '2' ? '女' : '',
      s(r.age), s(r.admitAt), s(r.dischargeAt), s(r.visitNo),
      s(r.mainDx), s(r.otherDx), s(r.procs),
      stText, s(r.mdc), s(r.adrg), s(r.code), s(r.name), s(r.weight), s(r.cost),
      s(r.factor), s(r.pointValue), s(r.totalPoint), s(r.standardFee), s(r.estimatedFee),
      s(r.rateType), s(r.lowRateFee), s(r.highRateFee),
      ...(hasHisFee ? [s(r.fee)] : []),
      isErr ? r.error : (r.reason || ''),
      s(r.drgDirect),
    ];
    lines.push(cells.map(csvCell).join(','));
  });
  downloadCsv('drg-batch.csv', lines);
}

// -------------------------- 批量:HIS 按出院日期提取病案 --------------------------

export async function runHisBatch() {
  const from = $('#hisBatchFrom').value, to = $('#hisBatchTo').value;
  showErr('#hisBatchError');
  if (!from || !to) return showErr('#hisBatchError', '请选择出院日期区间(起止都要填)');
  if (from > to) return showErr('#hisBatchError', '开始日期不能晚于结束日期');
  const limit = Math.max(1, Math.min(hisIdsMax, parseInt($('#hisBatchLimit').value, 10) || hisIdsDefault));

  const btn = $('#hisBatchRun');
  btn.disabled = true; btn.textContent = '提取中…';
  try {
    const data = await api.get('/api/his/group-by-date', {
      from,
      to,
      limit,
      ...codeVersionParam(),
      ...regionParam(),
    });
    // HIS 行复用批量表格:patientId → line 作行号列;错误行带自定义状态文案
    renderBatch({
      total: data.count != null ? data.count : (data.rows || []).length,
      ok: data.ok || 0,
      fail: data.fail || 0,
      rows: (data.rows || []).map(r => ({
        ...r,
        line: r.patientId != null ? r.patientId : r.line,
        statusText: r.error ? '提取失败' : r.statusText,
      })),
    });
  } catch (e) {
    showErr('#hisBatchError', 'HIS 批量分组失败:' + e.message);
  } finally {
    btn.disabled = false; btn.textContent = '提取并分组';
  }
}

// -------------------------- 批量:CSV 上传(HQMS) --------------------------

/* 上传上限:数字只在服务端定义一处(BatchParsing.BatchLimits),经 /api/info 下发,
   前端不复制一份 —— 两边各写一个 20/64 迟早会对不上,而"前端说行、后端说不行"最费解。 */
let uploadLimit = { maxUploadMb: 0 };

/** 结果表渲染与逐行明细的行数上限:由 /api/info 注入(与服务端 BatchLimits.MaxDetailRows 同一个数)。
    初值只作兜底 —— /api/info 未到或请求失败时也必须有个能拦住 20 万行的数;
    宁可暂时比服务端小(少渲染几行),也不能不设限。 */
let detailRows = 1000;   // 与 BatchLimits.MaxDetailRows 同值;真实值由 /api/info 下发

/** 「只看异常」模式的渲染上限:同样由 /api/info 注入(对应 BatchLimits.MaxIssueRows)。
    与 detailRows 同值 —— 正常行与异常行看到的上限一致,用户不必记两套规则。
    它只约束浏览器建多少行 DOM,与服务端回传无关(见 renderBatchRows 的说明)。 */
let issueRows = 1000;    // 与 BatchLimits.MaxIssueRows 同值;真实值由 /api/info 下发

/** HIS 按日期提取的条数上限 / 默认值(对应 BatchLimits.MaxHisIds / DefaultHisIds)。
    同样是"只定义一处"的数字:HTML 里的 max 属性与这里的夹取都走注入值,
    不再各写一个 1000/100(两处各写一个,迟早漂移成"界面说行、服务端说不行")。 */
let hisIdsMax = 1000, hisIdsDefault = 100;

/** 由 main.js 在拿到 /api/info 后注入。 */
export function setBatchLimits(limits) {
  if (!limits) return;
  uploadLimit = { maxUploadMb: limits.maxUploadMb || 0 };
  if (limits.maxDetailRows > 0) detailRows = limits.maxDetailRows;
  if (limits.maxIssueRows > 0) issueRows = limits.maxIssueRows;
  if (limits.maxHisIds > 0) hisIdsMax = limits.maxHisIds;
  if (limits.defaultHisIds > 0) hisIdsDefault = limits.defaultHisIds;
  // HIS 条数上限的 max 与默认值也走注入:HTML 里这两个属性原本各写着一个 1000/100,
  // 与"数字只定义一处"是同一条约定的两个例外,一并收掉(空值时才填,别覆盖用户已输入的数)。
  const hisNode = $('#hisBatchLimit');
  if (hisNode) {
    hisNode.max = String(hisIdsMax);
    if (!hisNode.value) hisNode.value = String(hisIdsDefault);
  }
  // 面板上的上限文案:HTML 里只留占位符「—」,真实数字在这里填。
  // (原来 HTML 手抄着「64MB / 200,000 行」,服务端却是 128MB / 100,000 —— /api/info 返回前
  //  会先闪一个错的数字;而且每次调 BatchLimits 都得记得同步改 HTML,必然漂移。)
  const node = $('#batchFileLimit');
  if (node && uploadLimit.maxUploadMb) node.textContent = String(uploadLimit.maxUploadMb);
  const rowNode = $('#batchRowLimit');
  if (rowNode && limits.maxRows > 0) rowNode.textContent = fmtInt(limits.maxRows);
}

/** 体积文本:KB / MB 两档足够(上传上限本身以 MB 计)。 */
function fmtSize(bytes) {
  return bytes >= 1024 * 1024
    ? (bytes / 1024 / 1024).toFixed(1) + 'MB'
    : Math.max(1, Math.round(bytes / 1024)) + 'KB';
}

// HQMS 首页取用的列(列头可能为代码如 C03C/A12C,也可能为中文)。与后端别名保持一致。
export const CSV_TEMPLATES = {
  hqms: {
    label: 'HQMS 首页',
    // A16=日龄(天)、A17=新生儿入院体重(克);A18x01…为出生体重可作回退。末尾两列为新生儿专用
    headers: ['A12C', 'A14', 'C03C', 'C04N', 'C06x01C', 'C07x01N', 'C14x01C', 'C15x01N', 'C35x01C', 'C36x01N', 'A16', 'A17'],
    sample: ['2', '56', 'I50.900', '慢性心力衰竭', 'J44.900', '慢性阻塞性肺病', '00.3400', '心房颤动导管消融术', 'I10.000', '高血压门诊治疗', '', ''],
    cols: ['性别(A12C)', '年龄(A14)', '出院主要诊断编码(C03C)', '出院其他诊断编码(C06x01C…C06xNNC)', '主要手术操作编码(C14x01C)及其他手术操作编码(C35x01C…)', '日龄(A16,天,新生儿必填)', '新生儿入院体重(A17,克)'],
  },
};

export function renderColHelp(fmt) {
  const t = CSV_TEMPLATES[fmt];
  const box = $('#colHelp');
  if (!box || !t) return;
  box.innerHTML = '';
  box.appendChild(el('p', { class: 'cell-dim', text: `按《${t.label}》规范自动识别下列列头(代码或中文均可,大小写不敏感):` }));
  box.appendChild(el('ul', {}, t.cols.map(c => el('li', { text: c }))));
}

export async function uploadCsv() {
  const fileInput = $('#batchFile');
  const file = fileInput.files && fileInput.files[0];
  // 反馈出口必须是本面板(#bsrc-file)自己的错误位:写进其他面板隐藏的错误位
  // 等于什么都没显示(实测 413 时页面毫无反馈,只有控制台报错)。
  showErr('#batchFileError');
  if (!file) return showErr('#batchFileError', '请先选择 CSV 或 XLSX 文件');
  // 超大文件就地拦下:省掉一次注定失败的整包上传(内网上传大文件要等很久)
  if (uploadLimit.maxUploadMb && file.size > uploadLimit.maxUploadMb * 1024 * 1024) {
    return showErr('#batchFileError',
      `文件 ${fmtSize(file.size)} 超过上限 ${uploadLimit.maxUploadMb}MB;请拆分后分批上传`);
  }
  const btn = $('#batchCsvUpload');
  btn.disabled = true; btn.textContent = '上传并分组…';
  try {
    const data = await uploadWithProgress(file);
    if (data.matchedColumns === 0) {
      return showErr('#batchFileError', '未识别到任何规范列,请确认文件表头与所选格式一致(可点“下载模板”对照)');
    }
    renderBatch(data);
  } catch (e) {
    showErr('#batchFileError', '文件分组失败:' + e.message);
  } finally {
    btn.disabled = false; btn.textContent = '上传并分组';
    setHidden($('#batchProgress'), true);
  }
}

/** 带进度的上传+分组。
 *
 *  <b>为什么用 XHR 而不是 fetch</b>:两个阶段都要真实进度,而 fetch 两样都给不了 ——
 *  ① <b>上传阶段</b>:只有 XHR 的 <c>upload.onprogress</c> 能报已发送字节
 *     (108MB 的件在内网上传要好几秒,这段时间页面原本是完全没反馈的);
 *  ② <b>服务端分组阶段</b>:服务端以 NDJSON 分块写响应,通过读 <c>xhr.responseText</c>
 *     的增量部分解析进度行 —— fetch 虽然能用 body.getReader(),但要自己拼流解析,
 *     而且上传阶段的进度它照样没有。
 *
 *  <b>两阶段的百分比各自独立</b>,不合成一个"假装连续"的总进度:上传几秒、服务端分组
 *  几秒到十几秒,占比事先并不知道,硬拼出一条匀速条反而是在骗人。阶段文案里写清楚当前在做哪一段。 */
async function uploadWithProgress(file) {
  const pbox = $('#batchProgress'), pbar = $('#batchProgressBar'), ptext = $('#batchProgressText');
  const show = (pct, text) => {
    if (!pbox) return;
    setHidden(pbox, false);
    if (pbar) pbar.style.width = (pct == null ? 100 : pct).toFixed(1) + '%';
    if (pbar) pbar.classList.toggle('is-unknown', pct == null);
    if (ptext) ptext.textContent = text;
  };

  const fd = new FormData();
  fd.append('file', file);
  if (currentRegion) fd.append('region', currentRegion); // RW/参考费用按区域 join
  fd.append('version', codeVersionParam().version);      // 编码版本:与单病例/HIS 路径同一口径
  fd.append('progress', '1');                            // opt-in:服务端才走逐行进度

  // 上传阶段给不出百分比(fetch 没有上传进度事件,只有 XHR 有),所以这一小段用不确定态;
  // 真正要盯的是服务端分组那 3~15 秒 —— 那段有真实行数进度,见下。
  show(null, '上传中…');
  const res = await fetch('/api/group/batch/csv', { method: 'POST', body: fd });
  if (!res.ok) {
    let msg = `请求失败(${res.status})`;
    try {
      const j = await res.json();
      msg = j.detail || j.error || j.title || msg;
    } catch { /* 非 JSON 错误体,沿用状态码 */ }
    const err = new Error(msg);
    err.status = res.status;
    throw err;
  }
  // 服务端把进度逐行写在响应体里,这里边到边读。
  // **必须走 ReadableStream**(res.body.getReader()),不能用 XHR 读 responseText:
  // 实测 XHR 那条路拿不到中间块(进度会"攒到最后一起到"),而流式读取是可靠的。
  if (!res.body) return await res.json();   // 极端退化:环境不支持流
  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '', payload = null;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let nl;
    while ((nl = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, nl).trim();
      buf = buf.slice(nl + 1);
      if (!line) continue;
      let msg;
      try { msg = JSON.parse(line); } catch { continue; }   // 半行:留到下一块再解析
      if (msg.done) payload = msg.payload;
      else if (msg.phase === 'group' && msg.total) {
        show((msg.done / msg.total) * 100, `服务端分组中 ${fmtInt(msg.done)} / ${fmtInt(msg.total)} 行`);
      }
    }
  }
  if (payload) return payload;
  // 服务端没走进度流(老版本)→ 退化为整段 JSON,行为与改动前一致
  try { return JSON.parse(buf); } catch { throw new Error('响应无法解析'); }
}

export function downloadTemplate(fmt) {
  const t = CSV_TEMPLATES[fmt];
  if (!t) return;
  const lines = [t.headers.join(','), t.sample.join(',')];
  downloadCsv(`drg-${fmt}-template.csv`, lines);
}
