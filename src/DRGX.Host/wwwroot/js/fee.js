// ---- 域间依赖(拆分脚本自动生成) ----
import { $, DEBOUNCE_FEE_MS, api, debounce, el, fmtInt, setHidden, staleGuard } from './dom.js';
import { refreshRegions, regionParam, setFeeFormHooks } from './state.js';

export const feeForm = { bound: false, guard: staleGuard(), debouncedEstimate: null };

/** 重灌左侧费用节的选项;无类型口径时整节隐藏。区域切换会重入(重灌选项保现选)。
    levels 可为空(如周口未公布等级系数):隐藏等级下拉,系数走 defaultFactor,不阻碍测算。 */
export async function initFeeForm() {
  const sec = $('#feeSec');
  if (!sec) return;
  const opts = await ensureFeeOptions();
  if (!opts.types.length) { setHidden(sec, true); return; }
  setHidden($('#feeLevelField'), !opts.levels.length);
  // 现选值(区域切换重入时)优先,其次 localStorage 记忆,最后落首个选项
  const fill = (sel, list, storeKey) => {
    const cur = sel.value;
    sel.textContent = '';
    list.forEach(v => sel.appendChild(el('option', { value: v, text: v })));
    let saved = '';
    try { saved = localStorage.getItem(storeKey) || ''; } catch { /* localStorage 不可用 */ }
    sel.value = list.includes(cur) ? cur : list.includes(saved) ? saved : list[0];
  };
  fill($('#feeLevelSel'), opts.levels, 'drg.fee.level');
  fill($('#feeTypeSel'), opts.types, 'drg.fee.type');
  setHidden(sec, false);
  if (feeForm.bound) { refreshFeeEstimate(); return; } // 区域重入:选项已变,重测在看的费用面板
  feeForm.bound = true;
  const persist = (key, v) => { try { localStorage.setItem(key, v); } catch { /* 忽略 */ } };
  $('#feeLevelSel').addEventListener('change', e => { persist('drg.fee.level', e.target.value); refreshFeeEstimate(); });
  $('#feeTypeSel').addEventListener('change', e => { persist('drg.fee.type', e.target.value); refreshFeeEstimate(); });
  // 连续输入只在停顿后测算一次;包装挂在 feeForm 上,供直接触发路径(下拉切换/区域重入)取消待发
  feeForm.debouncedEstimate = debounce(runFeeEstimate, DEBOUNCE_FEE_MS);
  $('#feeAmountInput').addEventListener('input', () => feeForm.debouncedEstimate());
  // change 兜底:自动填充/程序化清空等不派发 input 的场景,失焦提交后补一次测算
  $('#feeAmountInput').addEventListener('change', refreshFeeEstimate);
}

/** 左侧费用口径:节隐藏(无地区包)返回 null;实际费用为空时 fee=null(只测标准,不做倍率判定)。 */
export function feeFormValues() {
  const sec = $('#feeSec');
  if (!sec || sec.hidden) return null;
  const feeRaw = $('#feeAmountInput').value.trim();
  const feeNum = Number(feeRaw);
  return {
    level: $('#feeLevelSel').value,
    type: $('#feeTypeSel').value,
    // 非数字("abc")与负数一律按"未填"算:NaN 的比较恒为 false,只写 `Number(x) < 0`
    // 会把 NaN 原样发给接口,服务端再当字符串解析就得到一笔并不存在的费用。
    fee: feeRaw !== '' && Number.isFinite(feeNum) && feeNum >= 0 ? feeNum : null,
  };
}

// -------------------------- 费用计算选项(来自区域费用包 /api/fee/options) --------------------------

// 费用数据(RW/参考费用)已内嵌区域费用包,由服务端在分组结果与组索引中直接下发,
// 前端不再单独拉取权重库;此缓存仅服务左侧「费用测算」节与结果卡费用面板的选项(医院等级 / 医保类型),按区域失效。
export let feeOptions = null;
export let feeRegionsHealing = false; // 区域自愈重入保护
export async function ensureFeeOptions() {
  if (feeOptions) return feeOptions;
  try {
    feeOptions = await api.get('/api/fee/options', regionParam());
  } catch (e) {
    // 热更新/换版后当前区域 key 可能已下线(后端 BadRequest 400):重拉区域清单回退默认包,再试一次。
    // 判据首选状态码;文案匹配仅作兜底(旧后端/网关改状态码时仍可自愈)
    const unknownRegion = e && (e.status === 400 || String(e.message || '').includes('未知区域'));
    if (unknownRegion && !feeRegionsHealing) {
      feeRegionsHealing = true;
      try { await refreshRegions(); }
      finally { feeRegionsHealing = false; }
      return ensureFeeOptions();
    }
    // 失败不缓存:本次降级空选项(费用节隐藏),下次调用重试,不把"启动瞬间后端未就绪"钉死整个会话
    return { levels: [], types: [], regionName: '' };
  }
  return feeOptions;
}

/* ---- 费用测算面板(纯输出):口径来自左侧「费用测算」节,本面板不含任何输入 ----
    结论行(倍率区间+当前费用+差额) → 费用位置条 → 参数行,一屏到底。 */
export let feePanelState = null;

export function feePanel(container, drgCode) {
  ensureFeeOptions().then(opts => {
    if (!opts.types.length) return;
    const isRef = opts.dataLevel === 'reference';
    const isDemo = opts.demo === true;
    const d = el('details', { class: 'rc-details fee-panel', open: true });
    d.appendChild(el('summary', {
      text: '费用测算'
        + (opts.regionName ? ' · ' + opts.regionName : '')
        + (opts.dataVersion ? ' · ' + opts.dataVersion : '')
        + (isDemo ? ' · 演示数据' : '')
        + (isRef ? ' · 非本地值' : ''),
    }));
    const db = el('div', { class: 'details-body' });
    // 演示数据必须出现在数字旁边,而不只是下拉里 —— 看数的人未必回到选择器上核对
    if (isDemo && !isRef) {
      db.appendChild(el('p', {
        class: 'fee-demo-note',
        text: '⚠ 本地区包为演示数据,数字仅用于验证测算链路,不得用于结算、审核或对账。',
      }));
    } else if (isDemo && isRef) {
      db.appendChild(el('p', {
        class: 'fee-demo-note',
        text: '⚠ 本地区包为演示数据,且权重非当地公布值 —— 不可作为地方结算依据。',
      }));
    }
    const outBox = el('div', { class: 'fee-out' });
    const barBox = el('div', { class: 'fee-bar', hidden: true });
    const concBox = el('p', { class: 'fee-conc', hidden: true });
    const paramBox = el('div', { class: 'fee-params-box' });
    db.append(outBox, barBox, concBox, paramBox);
    d.appendChild(db);
    container.appendChild(d);
    feePanelState = { drg: drgCode, outBox, barBox, concBox, paramBox };
    runFeeEstimate();
  });
}

/** 按左侧口径调 /api/fee/estimate 并渲染;面板已随结果卡重建/清空(不在文档中)时静默。 */
export async function runFeeEstimate() {
  const st = feePanelState;
  if (!st || !st.drg || !st.outBox.isConnected) return;
  const f = feeFormValues();
  if (!f) return;
  const seq = feeForm.guard.next();
  st.outBox.textContent = '';
  st.outBox.appendChild(el('p', { class: 'fee-loading', text: '测算中…' }));
  st.barBox.hidden = true;
  st.concBox.hidden = true;
  const q = { drg: st.drg, level: f.level, type: f.type, ...regionParam() };
  if (f.fee != null) q.fee = f.fee;
  try {
    const r = await api.get('/api/fee/estimate', q);
    if (!feeForm.guard.is(seq)) return;           // 口径又变了,丢弃过期响应
    renderFeeOut(st, r, f.fee);
  } catch (e) {
    if (!feeForm.guard.is(seq)) return;
    st.outBox.textContent = '';
    st.outBox.appendChild(el('p', { class: 'fee-err', text: e.message || '计算失败' }));
  }
}

/** 左侧口径变化入口:select change / 费用输入停顿后触发;无面板时自然 no-op。
    直接触发时取消待发的防抖测算(后发先至,口径取最新一次)。 */
export function refreshFeeEstimate() {
  feeForm.debouncedEstimate?.cancel();
  runFeeEstimate();
}

export function renderFeeOut(st, r, fee) {
  st.outBox.textContent = '';

  const curFee = fee != null ? Number(fee) : null;
  // ── 参数区:支付标准为主视觉,其余参数两列网格排列 ──
  st.paramBox.textContent = '';
  const grid = el('div', { class: 'fee-params' });

  // ── 主视觉:支付标准 vs 实际费用 并排对比 + 差额 ──
  const hero = el('div', { class: 'fee-param-hero' });
  const heroRow = el('div', { class: 'fee-param-hero-row' });

  // 左列:支付标准
  const heroStd = el('div', { class: 'fee-param-hero-col' });
  heroStd.appendChild(el('span', { class: 'fee-param-hero-label', text: '支付标准' }));
  heroStd.appendChild(el('span', { class: 'fee-param-hero-value', text: fmtMoney(r.standardFee) || '—' }));
  heroRow.appendChild(heroStd);

  // 右列:实际费用(有值时显示;否则占位保持两列对齐)
  const hasFee = curFee != null;
  const heroCur = el('div', { class: 'fee-param-hero-col' + (hasFee ? '' : ' is-empty') });
  heroCur.appendChild(el('span', { class: 'fee-param-hero-label', text: '实际费用' }));
  heroCur.appendChild(el('span', { class: 'fee-param-hero-value', text: hasFee ? fmtMoney(curFee) : '待填' }));
  heroRow.appendChild(heroCur);
  hero.appendChild(heroRow);

  // 差额行:实际费用有值且支付标准有效时显示。
  // 金额取自服务端 standardGap(已按地区口径量化),不在前端用 double 重算 —— 那是展示给经办人员的金额。
  if (hasFee && r.standardGap != null) {
    const diff = Number(r.standardGap);
    const isOver = diff > 0;
    const stdNum = Number(r.standardFee);
    const diffEl = el('div', { class: 'fee-param-hero-diff ' + (isOver ? 'is-over' : 'is-under') });
    diffEl.appendChild(el('span', { class: 'fee-param-hero-diff-label', text: isOver ? '超出支付标准' : '低于支付标准' }));
    diffEl.appendChild(el('span', { class: 'fee-param-hero-diff-value', text: fmtMoney(Math.abs(diff)) }));
    // 百分比是派生比例(非金额),允许前端按 1 位小数展示
    if (stdNum > 0) diffEl.appendChild(el('span', { class: 'fee-param-hero-diff-pct', text: (Math.abs(diff) / stdNum * 100).toFixed(1) + '%' }));
    hero.appendChild(diffEl);
  }
  grid.appendChild(hero);

  // ── 次级参数:三列紧凑网格 ──
  // 可空字段先判空再入列:cells 的渲染过滤只认"值本身为空",不认格式化结果
  // (fmtInt 会把 null 显示成 0 —— 那在费用参数上是错的信息,不是缺省)。
  const cells = [
    ['权重', r.weight],
    ['系数', fmtInt(r.factor)],
  ];
  if (r.ratio != null) cells.push(['倍率', fmtInt(r.ratio)]);
  if (r.totalPoint != null) cells.push(['总点数', fmtInt(r.totalPoint)]);
  if (r.pointValue != null) cells.push(['点值', r.pointValue]);
  const cellWrap = el('div', { class: 'fee-param-grid' });
  cells.forEach(([label, val]) => {
    if (val == null || val === '') return;
    const cell = el('div', { class: 'fee-param-cell' });
    cell.appendChild(el('span', { class: 'fee-param-k', text: label }));
    cell.appendChild(el('span', { class: 'fee-param-v', text: String(val) }));
    cellWrap.appendChild(cell);
  });
  grid.appendChild(cellWrap);
  st.paramBox.appendChild(grid);

  renderFeeBar(st, r, fee);
}

/* 费用位置条:同一标尺上标出 低倍临界/支付标准/高倍临界 与 当前费用 的相对位置,
   一眼看出倍率区间归属;未提供实际费用(只测标准)或临界值缺失时整条隐藏。 */
export function renderFeeBar(st, r, fee) {
  const { barBox, concBox } = st;
  if (r.lowRateThreshold == null || r.highRateThreshold == null || !r.standardFee) {
    barBox.hidden = true;
    concBox.hidden = true;
    return;
  }
  const low = Number(r.lowRateThreshold), std = Number(r.standardFee);
  const high = Number(r.highRateThreshold), cur = Number(fee);
  const max = Math.max(low, std, high, cur) * 1.06;   // 留 6% 余量,费用再高钉子也不出界
  const pct = v => Math.min(100, Math.max(0, v / max * 100));
  const hasFee = fee != null;
  const zoneCls = !hasFee ? null : cur < low ? 'is-low' : cur > high ? 'is-high' : 'is-ok';

  barBox.hidden = false;
  barBox.textContent = '';
  const track = el('div', { class: 'fee-bar-track' });
  // 贴边(<9% / >91%)的刻度与钉标加 at-start/at-end,标签内收不溢出卡片
  const edge = p => p < 9 ? ' at-start' : p > 91 ? ' at-end' : '';
  track.appendChild(el('span', { class: 'fee-bar-zone is-low', style: `width:${pct(low)}%` }));
  track.appendChild(el('span', { class: 'fee-bar-zone is-mid', style: `left:${pct(low)}%;width:${pct(high) - pct(low)}%` }));
  track.appendChild(el('span', { class: 'fee-bar-zone is-high', style: `left:${pct(high)}%` }));
  [['fee-bar-low', low, '低倍临界'], ['fee-bar-std', std, '支付标准'], ['fee-bar-high', high, '高倍临界']]
    .forEach(([cls, v, label]) => track.appendChild(el('span', {
      class: 'fee-bar-tick ' + cls + edge(pct(v)), style: `left:${pct(v)}%`,
    }, [el('span', { class: 'fee-bar-tick-label', text: `${label} ${fmtMoney(v)}` })])));
  if (hasFee) {
    track.appendChild(el('span', { class: 'fee-bar-pin ' + zoneCls + edge(pct(cur)), style: `left:${pct(cur)}%` },
      [el('span', { class: 'fee-bar-pin-label', text: '当前费用 ' + fmtMoney(fee) })]));
  }
  barBox.appendChild(track);

  // 倍率结论:如实陈述费用与临界值的位置关系(占支付标准的百分比),不构成任何支付建议
  const pctStd = Math.round(cur / std * 1000) / 10;
  const conc = !hasFee
    ? '填写实际费用后查看当前费用与倍率临界值的位置关系'
    : r.rateType === '低倍率'
      ? `费用低于低倍率临界值（占支付标准的 ${pctStd}%），按实际费用折算点数`
      : r.rateType === '高倍率'
        ? `费用超过高倍率临界值（占支付标准的 ${pctStd}%），超出段按规则扣减`
        : `费用处于正常倍率区间（占支付标准的 ${pctStd}%）`;
  concBox.hidden = false;
  concBox.className = 'fee-conc ' + (zoneCls === 'is-ok' ? 'ok' : zoneCls ? 'warn' : 'muted');
  concBox.textContent = '';
  concBox.appendChild(el('b', { text: hasFee ? (r.rateType || '—') : '待填费用' }));
  concBox.appendChild(document.createTextNode(' · ' + conc));
}

export function fmtMoney(v) { return v == null || v === '' ? null : '¥' + Number(v).toLocaleString('zh-CN', { maximumFractionDigits: 2 }); }

/** HIS 病案的费用口径预填:总费用→实际费用输入;险种与点值键精确匹配时选中医保类型。
    无地区包(费用节隐藏)时静默跳过。 */
export function prefillFeeForm(p) {
  const sec = $('#feeSec');
  if (!sec || sec.hidden || !p) return;
  if (p.totalFee != null) $('#feeAmountInput').value = p.totalFee;
  const typeSel = $('#feeTypeSel');
  if (p.insuranceType && [...typeSel.options].some(o => o.value === p.insuranceType)) {
    typeSel.value = p.insuranceType;
    try { localStorage.setItem('drg.fee.type', typeSel.value); } catch { /* 忽略 */ }
  }
}

/** 区域失效/热更新时清空费用选项缓存(由 state.js 的区域逻辑调用)。 */
export function invalidateFeeOptions() { feeOptions = null; }

// 把「重灌/失效费用节」注入 state.js,由它按区域变化触发。
// 反过来 import 会让 state ↔ fee 成环(费用节本身要读区域状态)。
setFeeFormHooks({ init: initFeeForm, invalidate: invalidateFeeOptions });
