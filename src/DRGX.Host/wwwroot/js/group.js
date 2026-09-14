// ---- 域间依赖 ----
import { $, $$, api, badgeEl, copyText, el, setHidden, showErr, staleGuard } from './dom.js';
import { buildTimeline } from './evidence.js';
import { feePanel } from './fee.js';
import { buildScopePill, codeVersionParam, currentRegion, fields, lastCase, setLastCase, setRerunSingleHook, state } from './state.js';

// ---------------- 基层病组(/api/primary-groups,DRG 子集) ----------------
let primaryGroups = null, primaryGroupsP = null;
async function ensurePrimaryGroups() {
  if (primaryGroups) return primaryGroups;
  if (primaryGroupsP) return primaryGroupsP;
  primaryGroupsP = (async () => {
    try {
      const list = await api.get('/api/primary-groups');
      primaryGroups = new Map((list || []).map(x => [x.code, x]));
    } catch { return new Map(); } // 请求失败不缓存,下次分组时重试;接口正常但确实为空(无基层病组)仍缓存空 Map 降级
    return primaryGroups;
  })();
  try { return await primaryGroupsP; } finally { primaryGroupsP = null; }
}

export let placeholderHtml = '';   // 结果区空态模板(结果卡重画前后的还原基准)

/** 捕获结果区空态 DOM(init 期调用一次;声明处拿不到 DOM,时序在 init)。 */
export function stashResultPlaceholder() { placeholderHtml = $('#resultPane').innerHTML; }

export function numOrNull(id) {
  const v = $(id).value.trim();
  return v === '' ? null : Number(v);
}

export async function runSingle() {
  if (state.inFlight) return;           // 全局提交锁:防点击/连按并发
  state.inFlight = true;
  try { await runSingleInner(); } finally { state.inFlight = false; }
}

export async function runSingleInner() {
  showErr('#formError');
  $('#whatIfDlg').close();       // 重新分组后旧模拟数据失效,先关闭
  if (!state.gender) return showErr('#formError', '请选择患者性别');
  if (!fields.mainDx.getCodes().length) return showErr('#formError', '请填写主要诊断');
  await runSingleBody(buildGroupBody());
}

/** 从当前表单收集分组请求体(手工分组与 HIS 提取后的模拟同步共用此口径)。 */
export function buildGroupBody() {
  const neoOn = !$('#neoFields').hidden;
  return {
    gender: state.gender,
    age: numOrNull('#ageInput'),
    ageDay: neoOn ? numOrNull('#ageDayInput') : null,
    weight: neoOn ? numOrNull('#weightInput') : null,
    mainDiagnosis: fields.mainDx.getCodes()[0],
    otherDiagnoses: fields.otherDx.getCodes(),
    mainProcedure: fields.mainProc.getCodes()[0] || '',
    otherProcedures: fields.otherProc.getCodes(),
    verbose: false,                  // 主请求恒不带 verbose(16 行 vs 140+ 行),展开「详细日志」时才补拉
    ...codeVersionParam(),               // 顶栏「编码 国临版/医保版」:四条录入通道同一口径
    region: currentRegion || undefined, // 区域切换重算时也走这里,保持口径一致
  };
}

/** 单病例请求过期守卫:区域连切 / 表单连点会并发触发,晚到的旧响应必须丢弃,
    否则会把新病例的结果卡覆盖成旧病例的(同 adrgDetailGuard / feeForm.guard 的口径)。 */
const singleGuard = staleGuard();

/* 提交按钮文案取常量:进行中临时改写,结束后按常量还原。
   注意必须用显式 id 精确选取——历史上用 `#recordForm .btn-primary` 会命中 DOM 顺序更靠前的
   HIS「提取并分组」按钮(index.html 的 #hisFetch 在提交按钮之前),导致分组一次就改错入口文案。 */
const SUBMIT_LABEL = '分组';
const SUBMIT_BUSY_LABEL = '分组中…';

/** 发送单病例分组请求并渲染结果卡;区域切换的自动重算复用此路径(body 已带 region)。 */
export async function runSingleBody(body) {
  const seq = singleGuard.next();
  const btn = $('#submitBtn');
  btn.disabled = true; btn.textContent = SUBMIT_BUSY_LABEL;
  const pane = $('#resultPane');
  pane.innerHTML = '<div class="result-slot"><div class="loading-card"><span class="loading-spinner"></span>分组中…</div></div>';
  try {
    setLastCase({ body, candNames: collectChipNames() }); // 供「换主诊/手术模拟」复用
    const outcome = await api.post('/api/group', body);
    if (!singleGuard.is(seq)) return;                     // 已有更新的分组请求,本次过期:不渲染、不污染 lastCase
    if (!lastCase) return;                                // await 期间点了「清空」:lastCase 已被置 null,别再写回
    Object.assign(lastCase, {                             // 「同当前组」判定基准 + 当前行展示摘要
      currentCode: outcome.code,
      currentName: outcome.group?.name || '',
      currentRw: outcome.fee?.rw ?? null,
      currentStatusText: outcome.statusText || outcome.status,
    });
    updateWhatIfToggle();
    await renderResult(outcome);
  } catch (e) {
    if (!singleGuard.is(seq)) return;
    pane.innerHTML = '';
    showErr('#formError', '分组请求失败:' + e.message);
  } finally {
    btn.disabled = false; btn.textContent = SUBMIT_LABEL;
  }
}

/** 收集表单四个芯片字段的 编码→名称 映射,供候选对比展示。 */
export function collectChipNames() {
  const map = {};
  Object.values(fields).forEach(f => f.items.forEach(c => { if (c.code && !map[c.code]) map[c.code] = c.name || ''; }));
  return map;
}

export async function enrichNames(o, trace) {
  // 事实编码由后端结构化下发(trace[].codes),不再从 detail 文案正则抠码
  const codes = [
    ...(o.majorComplications || []),
    ...(o.minorComplications || []),
    ...(o.validProcedures || []),
  ];
  (trace || []).forEach(s => (s.codes || []).forEach(c => codes.push(c)));
  const uniq = [...new Set(codes)];
  if (!uniq.length) return {};
  try {
    const r = await api.post('/api/names', { codes: uniq });
    return r.names || {};
  } catch { return {}; }
}

/** 结果卡装配(手工分组/HIS 提取两条路径共用):异步补名称与主组表 → 结果卡 → 成功时追加费用卡。
    结果卡与费用卡同嵌在一道凹槽(.result-slot)里,读作「这一例被分拣进格位」的整块产出。
    fee: 服务端响应内嵌的 RW/参考费用(地区包未加载或未命中时为 undefined)。 */
/* 详细日志的按需 verbose 轨迹:主请求不带 verbose(轨迹 16 行 vs 140+ 行,结果卡装配成本差一个量级),
   只有用户真展开某帧的「显示详细日志」时才补拉一次。缓存键取请求体序列化 —— 同一病例反复展开不重发,
   换病例 / 换区域(body 变)自然失效。失败静默返回 null,由调用方退回默认轨迹。 */
let vTrace = { key: null, promise: null };
export function loadVerboseTrace() {
  const body = lastCase && lastCase.body;
  if (!body) return Promise.resolve(null);
  const key = JSON.stringify(body);
  if (vTrace.key === key) return vTrace.promise;
  vTrace = {
    key,
    promise: api.post('/api/group', { ...body, verbose: true })
      .then(r => r.trace || null).catch(() => null),
  };
  return vTrace.promise;
}

export async function renderOutcome(pane, outcome, fee) {
  const [names, primary] = await Promise.all([
    enrichNames(outcome, outcome.trace || []),
    outcome.status === 'Success' && outcome.code ? ensurePrimaryGroups() : Promise.resolve(new Map()),
  ]);
  const settled = outcome.status === 'Success' && outcome.code;
  const slot = el('div', { class: 'result-slot' });
  slot.appendChild(buildResultCard(outcome, names,
    settled ? fee || null : null,
    settled ? primary.get(outcome.code) || null : null,
    // 懒加载入口:同一病例反复展开不重发(见 loadVerboseTrace)
    loadVerboseTrace));
  pane.appendChild(slot);
  if (settled) {
    const fc = el('div', { class: 'fee-card' });
    slot.appendChild(fc);
    feePanel(fc, outcome.code);
  }
}

export async function renderResult(o) {
  const pane = $('#resultPane');
  pane.innerHTML = '';
  await renderOutcome(pane, o, o.fee);
}

/* ---- 换主诊/手术模拟(集成在左侧输入区):逐个把其他诊断/手术提为主项,并发调 /api/group 对比;
   每行提供「更换」按钮,确认后交换芯片并自动重新分组。
   合规红线:只如实展示各候选的分组后果,供核对编码准确性,不做任何"推荐高分值组"的排序暗示。 */
export function whatIfCandidates() {
  if (!lastCase) return [];
  const body = lastCase.body || {}, candNames = lastCase.candNames || {};
  const cands = [];
  (body.otherDiagnoses || []).forEach(c => cands.push({ kind: '换为主诊断', code: c, name: candNames[c] || '' }));
  (body.otherProcedures || []).forEach(c => cands.push({ kind: '换为主手术', code: c, name: candNames[c] || '' }));
  return cands;
}

export function updateWhatIfToggle() {
  $('#whatIfToggle').disabled = !whatIfCandidates().length;
}

export function toggleWhatIfPanel() {
  const dlg = $('#whatIfDlg');
  if (dlg.open) { dlg.close(); return; }
  const cands = whatIfCandidates();
  if (!cands.length) return;
  renderWhatIfDlg(dlg, cands);
  dlg.showModal();
}

export function renderWhatIfDlg(dlg, cands) {
  dlg.innerHTML = '';
  // 标题按候选构成生成:纯诊断病例不再出现「/主手术」字样,少一分噪音
  const hasDx = cands.some(c => c.kind === '换为主诊断');
  const hasProc = cands.some(c => c.kind === '换为主手术');
  const title = hasDx && hasProc ? '换主诊断/主手术模拟' : hasDx ? '换主诊断模拟' : '换主手术模拟';
  dlg.appendChild(el('div', { class: 'wi-head' }, [
    el('b', { id: 'whatIfDlgTitle', text: `${title}(${cands.length} 个候选)` }),
    el('button', { type: 'button', class: 'chip-x', text: '×', 'aria-label': '关闭',
      onclick: () => dlg.close() }),
  ]));
  dlg.appendChild(el('p', { class: 'cell-dim whatif-note',
    text: '仅供核对编码准确性:展示各候选作为主项时的分组结果,主诊断/主手术的选择须以临床事实为准。' }));

  const wrap = el('div', { class: 'wi-table' });
  dlg.appendChild(wrap);

  // 当前行(对比基准):当前主诊断/主手术及其分组结果,不提供更换
  const candNames = lastCase.candNames || {};
  const curBody = lastCase.body || {};
  const curRow = (kind, code, name) => {
    const res = el('span', { class: 'wi-res' });
    if (lastCase.currentCode) {
      res.appendChild(el('b', { class: 'wi-drg mono', text: lastCase.currentCode }));
      if (lastCase.currentRw != null) res.appendChild(el('span', { class: 'wi-rw', text: ` RW ${lastCase.currentRw}` }));
      if (lastCase.currentName) res.appendChild(el('span', { class: 'wi-gname', text: ' ' + lastCase.currentName }));
    } else {
      res.classList.add('cell-dim');
      res.textContent = lastCase.currentStatusText || '未入组';
    }
    return el('div', { class: 'wi-row wi-row-cur' }, [
      el('span', { class: 'wi-kind', text: kind }),
      el('span', { class: 'wi-code mono', text: code }),
      el('span', { class: 'wi-name', text: name, title: name || code }),
      res,
      el('span', { class: 'wi-cur-tag', text: '当前' }),
    ]);
  };
  wrap.appendChild(curRow('当前主诊断', curBody.mainDiagnosis || '—', candNames[curBody.mainDiagnosis] || ''));
  if (curBody.mainProcedure || (curBody.otherProcedures || []).length)
    wrap.appendChild(curRow('当前主手术', curBody.mainProcedure || '无',
      curBody.mainProcedure ? (candNames[curBody.mainProcedure] || '') : ''));

  const rows = cands.map(c => {
    const resCell = el('span', { class: 'wi-res cell-dim', text: '分组中…' });
    const applyBtn = el('button', { type: 'button', class: 'wi-apply', text: '更换', disabled: true,
      title: '把该项设为主项,原主项移回该位置,并自动重新分组' });
    const row = el('div', { class: 'wi-row' }, [
      el('span', { class: 'wi-kind', text: c.kind }),
      el('span', { class: 'wi-code mono', text: c.code }),
      el('span', { class: 'wi-name', text: c.name, title: c.name || c.code }),
      resCell,
      applyBtn,
    ]);
    wrap.appendChild(row);
    applyBtn.addEventListener('click', () => applySwap(c));
    return { resCell, row, applyBtn };
  });

  const body = lastCase.body || {};
  Promise.all(cands.map(async (c, i) => {
    const req = { ...body, verbose: false };
    // 与 applySwap 的「原位替换」语义保持一致:预览结果即实际更换后的分组
    if (c.kind === '换为主诊断') {
      req.mainDiagnosis = c.code;
      req.otherDiagnoses = (body.otherDiagnoses || []).map(x => x === c.code ? body.mainDiagnosis : x).filter(Boolean);
    } else {
      req.mainProcedure = c.code;
      req.otherProcedures = (body.otherProcedures || []).map(x => x === c.code ? body.mainProcedure : x).filter(Boolean);
    }
    const cell = rows[i].resCell;
    cell.textContent = '';
    cell.classList.remove('cell-dim');
    try {
      const r = await api.post('/api/group', req);
      rows[i].applyBtn.disabled = false;
      if (r.status === 'Success') {
        cell.appendChild(el('b', { class: 'wi-drg mono', text: r.code }));
        if (r.fee && r.fee.rw != null) cell.appendChild(el('span', { class: 'wi-rw', text: ` RW ${r.fee.rw}` }));
        if (r.code === lastCase.currentCode) {
          // 同当前组:整行弱化退后,把扫读注意力留给有差异的候选(降噪,不涉价值排序)
          rows[i].row.classList.add('is-same');
          cell.appendChild(el('span', { class: 'wi-same', text: '同当前组' }));
        } else if (r.group && r.group.name) cell.appendChild(el('span', { class: 'wi-gname', text: ' ' + r.group.name }));
      } else {
        cell.classList.add('cell-dim');
        cell.textContent = r.statusText || r.status || '未入组';
      }
    } catch (e) {
      cell.classList.add('cell-dim');
      cell.textContent = '请求失败:' + e.message;
    }
  }));
}

/** 把其他诊断/手术与主项交换(chip 原位替换),随后自动重新分组。 */
export function applySwap(c) {
  const isDx = c.kind === '换为主诊断';
  const main = isDx ? fields.mainDx : fields.mainProc;
  const other = isDx ? fields.otherDx : fields.otherProc;
  const idx = other.items.findIndex(x => (x.sourceCode || x.code) === c.code);
  if (idx < 0) return;
  const promoted = other.items.splice(idx, 1)[0];
  const demoted = main.items[0] || null;
  main.items = [promoted];
  if (demoted) other.items.splice(idx, 0, demoted); // 原主项放回该候选原位置
  main.render(); other.render();
  $('#whatIfDlg').close();
  runSingle();
}

/** 管线灯珠:解析 → 编码 → 分组 → 费用。
    把「这一例走到哪一步、哪一步卡住」压成可扫读的物理信号,与批量表结果列的灯珠同一语汇。
    灯珠只报事实,不做价值判断:编码灯亮=本次确有编码映射发生,灭=按原始码直分组。 */
export function buildLamps(o, fee) {
  const st = o.status;
  const items = [
    ['解析', 'on'],
    ['编码', (o.mappings || []).length ? 'on' : 'off'],
    ['分组', st === 'Success' ? 'on' : st === 'Ambiguous' ? 'warn' : 'err'],
    ['费用', fee && fee.rw != null ? 'on' : 'off'],
  ];
  return el('span', {
    class: 'rc-lamps',
    title: '管线状态:解析 → 编码转换 → 分组 → 费用测算',
  }, [
    el('span', { class: 'lamp-labels' }, items.map(([label, tone]) =>
      el('span', { class: 'lamp-item' }, [
        el('span', { class: 'lamp ' + tone }),
        el('span', { text: label }),
      ]))),
  ]);
}

export function buildResultCard(o, names, fee, primary, loadTrace) {
  const status = o.status;
  // 状态条用中性底承载“流程完成”,语义色只给灯珠与结论,避免整卡染色的疲劳
  const headClass = status === 'Success' ? 'neutral' : status === 'Ambiguous' ? 'warn' : 'err';
  const card = el('div', { class: 'result-card' });
  const head = el('div', { class: 'rc-head ' + headClass }, [
    el('span', { text: o.statusText || status }),
    buildLamps(o, fee),
    buildScopePill(),
  ]);
  card.appendChild(head);
  const body = el('div', { class: 'rc-body' });
  card.appendChild(body);

  // ---- 路径即导航:原 rc-hero 右栏 / 决策链 / 原始日志三者合并为「路径条 + 统一依据面板」 ----
  // 排除表命中是「为什么并发症没算进分组」的直接答案,结果级异常信号:
  // 常显在组名下方,不进折叠区(原先为「点此核对」按钮 + 明细折叠区,折叠区已移除)
  const excluded = o.excludedComplications || [];
  const pe = buildTimeline(status, o, o.trace || [], names, loadTrace);

  if (status === 'Success') {
    body.appendChild(el('div', { class: 'rc-code-row' }, [
      el('div', { class: 'rc-code rc-code-land', text: o.code }),
      primary ? badgeEl({ tone: 'info', text: '基层病组' }) : null,
      el('button', { type: 'button', class: 'copy-btn', text: '复制', onclick: () => copyText(o.code) }),
    ]));
    if (o.group && o.group.name) body.appendChild(el('div', { class: 'rc-name', text: o.group.name }));
    // 排除表命中:码 + 所属排除组,直接列出(后端下发结构化 {code,kind,group},无需解析拼接串)
    if (excluded.length) {
      const sec = el('div', { class: 'rc-excluded' });
      sec.appendChild(el('h4', { class: 'rc-excluded-h', text: `主诊断排除表命中(${excluded.length} 项未计入分组)` }));
      const chips = el('div', { class: 'evi-facts excluded-strip' });
      excluded.forEach(item => {
        chips.appendChild(el('span', { class: 'evi-fact excluded-chip' }, [
          el('b', { class: 'mono', text: item.code }),
          item.kind ? badgeEl({ tone: 'warn', text: `${item.kind} 组${item.group}`, title: '与主诊断排除组一致,未计入并发症' }) : null,
        ]));
      });
      sec.appendChild(chips);
      body.appendChild(sec);
    }
  } else if (status === 'Ambiguous') {
    body.appendChild(el('div', { class: 'rc-code-row' }, [
      el('div', { class: 'rc-code', text: o.code }),
      el('button', { type: 'button', class: 'copy-btn', text: '复制', onclick: e => copyText(o.code, e.currentTarget) }),
    ]));
    body.appendChild(el('p', { class: 'rc-hint', text: o.reasonText || '主手术与主要诊断无关,归入歧义病案' }));
  } else {
    body.appendChild(el('p', { class: 'rc-reason', text: o.reasonText || '无法分组' }));
  }

  body.appendChild(pe.el);

  // ---- 费用测算已移至独立卡片,此处不再挂载 ----

  // 并发症 / 操作 / 编码映射折叠区已移除:
  //   MCC/CC 计数与编码映射在「路径依据面板」里已有(依据面板的编码标准化节点);
  //   排除表命中已提升为组名下的常显块(见上)。
  //   代价:具体是哪些码算作 MCC/CC、以及有效操作清单,界面上不再提供。

  // 竖轴的 ADRG 入组明细已随 buildTimeline 懒构建内部触发,这里不再重复拉取

  return card;
}


/** 仅清空表单(性别/年龄/新生儿字段/芯片/实际费用),不动结果区。
    等级/类型是口径记忆,跨病例保留;实际费用属于病例数据,随清空丢弃。 */
export function clearForm() {
  state.gender = null;
  setHidden($('#pickedBar'), true);
  $$('.seg-item').forEach(x => { x.classList.remove('is-active'); x.setAttribute('aria-checked', 'false'); });
  $('#ageInput').value = '';
  setHidden($('#neoFields'), true);
  $('#neoToggle').setAttribute('aria-expanded', 'false');
  $('#ageDayInput').value = '';
  $('#weightInput').value = '';
  Object.values(fields).forEach(f => f.clear());
  const feeInput = $('#feeAmountInput');
  if (feeInput) feeInput.value = '';
  showErr('#formError');
  setLastCase(null);                       // 表单已空,旧模拟数据随之失效
  $('#whatIfDlg').close();
  updateWhatIfToggle();
}

export function resetForm() {
  clearForm();
  $('#resultPane').innerHTML = placeholderHtml;
}

// 把「区域切换后重算单病例」注入 state.js(它持有 currentRegion 与 lastCase)。
// 反过来 import 会让 state ↔ group 成环。
setRerunSingleHook(body => runSingleBody(body));

