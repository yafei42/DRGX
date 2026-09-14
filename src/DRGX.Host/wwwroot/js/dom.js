// -------------------------------- 工具 ------------------------------------

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

/** 轻量 createElement 封装:attrs 支持 class/text/on* 事件,children 支持字符串或节点。 */
export function el(tag, attrs = {}, children = []) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === 'class') n.className = v;
    else if (k === 'text') n.textContent = v;
    else if (k.startsWith('on') && typeof v === 'function') n.addEventListener(k.slice(2), v);
    else n.setAttribute(k, v === true ? '' : v);
  }
  for (const c of [].concat(children)) {
    if (c == null || c === false) continue;
    n.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
  }
  return n;
}

/** 读取响应;非 2xx 时尽量提取服务端 {error} 或 ProblemDetails {detail}/{title} 文本再抛出。 */
export async function readJson(r) {
  if (r.ok) return r.json().catch(() => ({}));
  let msg = `请求失败(${r.status})`;
  try {
    const j = await r.json();
    if (j && j.error) msg = j.error;
    else if (j && j.detail) msg = j.detail;
    else if (j && j.title) msg = j.title;
  } catch { /* 非 JSON 错误体,沿用状态码 */ }
  // 带上 HTTP 状态码:调用方按 e.status 分支,不要靠中文文案匹配(文案随本地化/改版即失效)
  const err = new Error(msg);
  err.status = r.status;
  throw err;
}

/** 过期响应守卫:连续异步操作时丢弃晚到的旧响应,防止旧数据盖到新结果上。
    统一四手写 seq(loadTableSeq/feeForm.seq/ChipField.seq/adrgDetailSeq)为同一习惯用法:
    const ticket = guard.next(); … if (!guard.is(ticket)) return; */
export function staleGuard() {
  let n = 0;
  return {
    next: () => ++n,       // 发起新请求前取号
    is: t => t === n,      // 响应回来验号,过期即丢弃
  };
}

// -------------------------- 轻交互反馈(复制 / 播报) --------------------------

/** 屏幕阅读器播报区(视觉隐藏):复制这类轻交互不能只靠视觉反馈。 */
let srRegion = null;
export function announce(msg) {
  if (!srRegion) {
    srRegion = el('p', { class: 'sr-only', role: 'status', 'aria-live': 'polite' });
    document.body.appendChild(srRegion);
  }
  // 同一文案连续播报时先清空一帧,否则读屏器可能不重读
  srRegion.textContent = '';
  setTimeout(() => { srRegion.textContent = msg; }, 30);
}

/** 降级复制:非安全上下文(内网 http 部署、部分 WebView2)下 navigator.clipboard 不可用,
    用临时 textarea + execCommand 兜底;成功返回 true。 */
function fallbackCopy(text) {
  const ta = el('textarea', { class: 'copy-shim', readonly: true });
  ta.value = text;
  document.body.appendChild(ta);
  ta.select();
  let ok = false;
  try { ok = document.execCommand('copy'); } catch { ok = false; }
  ta.remove();
  return ok;
}

/** 复制文本并就地反馈:成功切「已复制 ✓」,失败给可见兜底 —— 绝不静默吞错。
    传 btn 时按钮文案参与反馈(1.2s 后还原);不传则只播报。 */
export async function copyText(text, btn) {
  const label = btn ? btn.textContent : '';
  const flash = (txt, ok) => {
    if (!btn) return;
    clearTimeout(btn._copyTimer);
    btn.textContent = txt;
    btn.classList.toggle('is-copied', ok);
    btn._copyTimer = setTimeout(() => { btn.textContent = label; btn.classList.remove('is-copied'); }, 1200);
  };
  let ok = false;
  try {
    if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); ok = true; }
    else ok = fallbackCopy(text);
  } catch { ok = fallbackCopy(text); }
  if (ok) { flash('已复制 ✓', true); announce('已复制 ' + text); }
  else { flash('复制失败', false); announce('复制失败,请手动选择文本'); }
}

// -------------------------- 自有弹窗(confirm / prompt) --------------------------
// 原生 confirm()/prompt() 在 WebView2 等内嵌浏览器中可能被禁用,且样式与设计语言割裂。
// 统一改用页面内原生 <dialog>:复用 .whatif-dlg 的遮罩 / 焦点陷阱 / Esc / backdrop。

let dlgSeq = 0;

/** dialog 通用外壳:点遮罩或按 Esc 都关闭。遮罩判定走坐标比较(不依赖事件 target,
    因为点击落在 dialog 自身的 padding 上时 target 仍是 dialog)。Esc 要手动兜底 ——
    合成键盘事件(自动化/特殊输入环境)不触发原生 cancel;而 close 幂等,重复触发无副作用。 */
export function bindDialogChrome(dlg, returnValue) {
  dlg.addEventListener('click', e => {
    const r = dlg.getBoundingClientRect();
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom)
      dlg.close(returnValue);
  });
  dlg.addEventListener('keydown', e => { if (e.key === 'Escape') dlg.close(returnValue); });
}

/** 弹窗骨架:标题 + 内容 + 底部操作栏;backdrop 点击与 Esc 均按"取消"关闭。 */
function buildDlg({ title, bodyNodes, actions }) {
  const id = 'mdlg-' + (++dlgSeq);
  const titleId = id + '-title';
  const dlg = el('dialog', { class: 'whatif-dlg modal-dlg', id, 'aria-labelledby': titleId });
  dlg.append(
    el('div', { class: 'wi-head' }, [el('b', { id: titleId, text: title })]),
    el('div', { class: 'mdlg-body' }, bodyNodes),
    el('div', { class: 'mdlg-actions' }, actions),
  );
  bindDialogChrome(dlg, 'cancel');
  document.body.appendChild(dlg);
  dlg.addEventListener('close', () => dlg.remove());
  return dlg;
}

/** 页面内确认弹窗 → resolve(true/false);取消 / Esc / 遮罩均得 false。 */
export function confirmDlg({ title, body = '', confirmText = '确定', cancelText = '取消', danger = false }) {
  return new Promise(resolve => {
    let settled = false;
    const done = v => { if (!settled) { settled = true; resolve(v); } };
    const okBtn = el('button', { type: 'button', class: 'btn ' + (danger ? 'btn-danger' : 'btn-primary'), text: confirmText });
    const cancelBtn = el('button', { type: 'button', class: 'btn btn-ghost', text: cancelText });
    const dlg = buildDlg({ title, bodyNodes: [el('p', { class: 'mdlg-text', text: body })], actions: [cancelBtn, okBtn] });
    okBtn.addEventListener('click', () => { done(true); dlg.close(); });
    cancelBtn.addEventListener('click', () => { done(false); dlg.close(); });
    dlg.addEventListener('close', () => done(false));
    dlg.showModal();
    okBtn.focus();   // 确认键初始聚焦:键盘可直接 Enter
  });
}

export const api = {
  get(url, params) {
    const qs = params ? '?' + new URLSearchParams(params) : '';
    return fetch(url + qs, { headers: { 'Accept': 'application/json' } }).then(readJson);
  },
  post(url, body) {
    return fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
      body: JSON.stringify(body || {}),
    }).then(readJson);
  },
};

// -------------------------- 姓名脱敏(仅显示层) --------------------------
// 用途:演示 / 截图时不在屏幕上露出患者姓名。纯前端替换显示文本。
//
// 边界要说清楚:它挡的是「被人看到」,不是「被人取到」—— 真实姓名仍会随 API 响应
// 到达浏览器,开发者工具的网络面板里依然可见。需要真正的访问控制请前置认证代理(见 README「部署与安全」)。
const MASK_KEY = 'drg.maskName';
let maskOn = false;

export function maskNameEnabled() { return maskOn; }

/** 姓名:保留姓(首字),其余以 * 代替,最多 3 个(不泄漏名字长度);单字姓名返回 *。 */
export function maskName(name) {
  if (!maskOn) return name;
  const s = String(name ?? '').trim();
  if (s.length === 0) return name;
  if (s.length === 1) return '*';
  // 首字可能是代理对(部分少数民族姓名),按码元成对取,避免截出半个字符
  const first = /[\uD800-\uDBFF]/.test(s[0]) ? s.slice(0, 2) : s.slice(0, 1);
  return first + '*'.repeat(Math.min(s.length - first.length, 3));
}


/**
 * 绑定脱敏开关(可多处,状态共享)并恢复上次选择。
 * @param {() => void} onChange 开关变化后回调 —— 已渲染的内容不会自动改写,由调用方重绘。
 * @param {...string} selectors 各开关的选择器。
 */
export function initMaskToggle(onChange, ...selectors) {
  const boxes = selectors.map(s => $(s)).filter(Boolean);
  try { maskOn = localStorage.getItem(MASK_KEY) === '1'; } catch { /* localStorage 不可用(隐私模式) */ }
  const sync = () => boxes.forEach(b => { b.checked = maskOn; });
  boxes.forEach(b => {
    // 开关位于 <details> 的 <summary> 内时,点击会冒泡到 summary 并连带展开/收起整个折叠区。
    // 在 click 阶段阻断(早于 change),勾选就只是勾选。
    b.addEventListener('click', e => { if (b.closest('summary')) e.stopPropagation(); });
    b.addEventListener('change', () => {
      maskOn = b.checked;
      try { localStorage.setItem(MASK_KEY, maskOn ? '1' : '0'); } catch { /* 同上 */ }
      sync();
      onChange?.();
    });
  });
  sync();
}

export function fmtInt(n) { return (n || 0).toLocaleString('zh-CN'); }

/** 当前输入编码版本。真相源是 <html data-codever> —— state.js 负责写、样式表用它做首屏高亮、
    这里读它发给接口。刻意不 import state.js:dom 是叶子模块,反向依赖会成环。 */
export function currentCodeVersion() {
  return document.documentElement.dataset.codever === 'yibao' ? 'yibao' : 'guolin';
}

/** 结果区「编码标准化(国临码 → 医保码)」是否可见:只有国临版输入才有这一步。
    医保版输入不做任何转换,结果区再列一张"映射表"只是凭空多一层噪声。 */
export function mapDisplayVisible() { return currentCodeVersion() !== 'yibao'; }

/** 连续触发防抖:停顿 ms 后执行一次;包装函数自带 cancel,监听器场景直接换用,不再手工 clear/setTimeout。 */
export function debounce(fn, ms) {
  let t = null;
  const wrapped = (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
  wrapped.cancel = () => clearTimeout(t);
  return wrapped;
}
/** 防抖间隔:编码下拉搜索 120ms / 费用金额输入 350ms / 数据表筛选 200ms。 */
export const DEBOUNCE_CHIP_MS = 120;
export const DEBOUNCE_FEE_MS = 350;
export const DEBOUNCE_SEARCH_MS = 200;

/** 条件轨迹共享谓词:时间轴/证据面板/trace 日志三个消费方同一口径,防止谓词漂移。
    isCondTrace = 条件轨迹节点(带 result);isGateTrace = MDC 门控条件;
    isSubgroupTrace = 命中具体组的落位条件(gate 之外的码表条件)。 */
export function isCondTrace(s) { return s.result !== undefined && s.result !== null; }
/** isGateTrace = MDC 门控条件。门控是树(满足任一分支 → 分支内同时满足 N 项),引擎按调用点
   显式下发 scope(整棵子树同标),直接读它 —— 曾按路径子串判,文案/路径格式一改就整块错帧
   (实测踩过:子树漏判成非门控行,落进 ADRG 帧)。 */
export function isGateTrace(s) { return s.scope === 'Gate'; }
export function isSubgroupTrace(s) { return s.scope === 'DrgSplit'; }

/** ADRG 第 3 位编码标识组性质(CHS-DRG 编码规则:1 手术/2 操作/3 内科);非规范码返回 ''(调用方自定兜底)。 */
export function adrgKindOf(code) {
  const c = String(code || '');
  return c[2] === '1' ? '外科组' : c[2] === '2' ? '操作组' : c[2] === '3' ? '内科组' : '';
}

/** 编码标准化映射 chips(原始码 → 医保版码 + 类型徽标):竖轴与横链面板共用,解析失败原样展示字符串。 */
export function mapChips(maps) {
  const chips = el('div', { class: 'evi-facts map-facts' });
  // 后端下发结构化映射项 {orig,mapped,type},无需正则解析拼接串
  maps.forEach(({ orig, mapped, type }) => {
    chips.appendChild(el('span', { class: 'evi-fact map-chip' }, [
      el('span', { class: 'map-orig-code', text: orig }),
      el('span', { class: 'map-arrow', text: ' → ' }),
      el('span', { class: 'map-target-code', text: mapped }),
      badgeEl({ tone: 'neutral', text: type }),
    ]));
  });
  return chips;
}

/** 统一徽标生成:输入区/结果区共用同一套语义与 class,避免下拉、芯片、结果卡各自漂移。
    spec = [{ tone, text, title? }];tone 只取语义色,不承载具体业务名。 */
export function badgeEl(spec) {
  return el('span', {
    class: 'badge ' + (spec.tone || 'neutral'),
    title: spec.title || '',
    text: spec.text,
  });
}

/** 编码对象 → 徽标规格。blocked 具体文案由类型决定;其他条件按优先级返回。
 *
 *  @param code  /api/lookup 返回的字典项
 *  @param type  'diagnosis' | 'procedure'
 *  @param opts  { primary?: boolean } primary=true 表示这是主要诊断/主要手术
 *
 *  **主要诊断不显示 MCC/CC**:CHS-DRG 的并发症分档只针对「其他诊断」——
 *  主诊断的作用是定 MDC/ADRG,以及在排除表里以"同表不计"的身份参与(见
 *  GrouperEngine 的 exclusionGroup)。引擎自己判 MCC/CC 时也只在 otherDiagnoses
 *  上遍历,主诊断永远不进 mccList/ccList。在这块位置标 MCC/CC 等于告诉编码员
 *  "这个主诊断算并发症",与引擎口径相反。
 *  「不可作主诊断」(灰码)与「机器人」是主诊断位置上同样成立的判断,保留。 */
export function codeBadges(code, type, opts = {}) {
  const specs = [];
  if (code.unknown) specs.push({ tone: 'err', text: '未识别' });
  else {
    // 国临版独有、且编码映射表里没有出口的码:它选得出来、也提交得进去,但目标口径
    // 根本没这个码,结果只会是"未入组"。不点出来的话,用户分不清是选错了版本还是这例真分不了组。
    if (code.unmapped) specs.push({
      tone: 'err',
      text: '无医保对应',
      title: '该码在国临版目录内、但编码映射表无医保版对应码：分组将按原码判定，通常无法入组。可切换为医保版核对，或改选一个有对应码的国临版码',
    });
    if (code.blocked) specs.push({
      tone: 'warn',
      text: type === 'procedure' ? '不参与分组' : '不可作主诊断',
      title: '医保灰码：不参与分组',
    });
    else if (type === 'procedure' && code.valid) specs.push({ tone: 'ok', text: '有效', title: '该手术码在官方 9,514 有效操作清单内：触发歧义(QY)判定与特殊手术组条件' });
    // robot 与 blocked 并列(机器人 5 码同时是灰码):直赋资格与灰码身份是两个口径,需同时可见
    if (code.robot) specs.push({
      tone: 'info',
      text: '机器人',
      title: '机器人辅助手术码：病例全部手术包含此码时直赋机器人专组（脚注18）',
    });
    // 手术字典不带 mcc/cc(见 PackRuntime:ProcEntries 未传 mcc/cc),故 comp 只在诊断上出现。
    // MCC / CC 各自用专属色调:两者严重程度不同,旧版同用 info 蓝时只能逐字读;
    // tone 只是语义名,具体色值在 style.css 的 --comp-* token 里(见 .badge.mcc / .badge.cc)。
    else if (code.comp && !opts.primary) specs.push({ tone: code.comp === 'MCC' ? 'mcc' : 'cc', text: code.comp });
  }
  return specs;
}

export function showErr(sel, msg) {
  const e = $(sel);
  e.textContent = msg || '';
  e.toggleAttribute('hidden', !msg);
}

export function setHidden(elm, on) { if (elm) elm.hidden = !!on; }

/** CSV 单元格转义:含分隔符/引号/换行时用双引号包裹并转义内部引号。
    批量导出与数据一览导出共用同一口径,避免两处各写一份转义。 */
export function csvCell(v) {
  const s = String(v == null ? '' : v);
  return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
}

export function downloadCsv(name, lines) {
  const csv = '﻿' + lines.join('\r\n'); // BOM 让 Excel 正确识别中文
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  const a = el('a', { href: url, download: name });
  document.body.appendChild(a); a.click(); a.remove();
  URL.revokeObjectURL(url);
}

/** 命中事实里的编码就地补中文名称(J18.9 → J18.9 肺炎),一线不用再翻字典。
    codes:后端结构化下发的事实编码(trace[].codes);names:编码→名称映射(enrichNames 批量拉取)。
    文案改写(实际→存在/执行了)已上移后端 detail,此处只做精确的编码→码+名替换,无正则抠码。
    纯字符串函数,与 DOM 无关;放在叶子模块以免 evidence ↔ group 成环。 */
/* 词边界正则缓存:同一编码会在多行/多事实里反复替换,预先编译一次即可。
   词边界替换防前缀码误配(E11.9 不命中 E11.90);编码仅含 [\w.],转义 . 即可。 */
const nameReCache = new Map();
function nameRe(code) {
  let re = nameReCache.get(code);
  if (!re) {
    re = new RegExp(`\\b${code.replace(/\./g, '\\.')}\\b`, 'g');
    nameReCache.set(code, re);
  }
  return re;
}

export function factWithNames(fact, names, codes) {
  if (!fact) return fact;
  if (!names || !codes || !codes.length) return fact;
  let out = fact;
  codes.forEach(c => {
    if (!names[c]) return;
    out = out.replace(nameRe(c), `${c} ${names[c]}`);
  });
  return out;
}

/** seg 类单选组的 ←/→ 互斥切换:与 bindTablist 同一套键位语义,只差没有 Home/End,
    且"激活方式"由调用方给(性别组是 click、编码版本组是赋值)。 */
export function rovingArrows(container, itemSelector, activate) {
  container.addEventListener('keydown', e => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    const arr = $$(itemSelector, container);
    const i = arr.indexOf(document.activeElement);
    if (i < 0) return;
    e.preventDefault();
    const next = arr[(i + (e.key === 'ArrowRight' ? 1 : arr.length - 1)) % arr.length];
    activate(next);
    next.focus();
  });
}

/** tablist 通用键盘导航:←/→ 循环、Home/End 首尾,配合 roving tabindex(仅激活项在 Tab 序内)。
    只管键位、焦点与 tabindex;真正"切页/切面板"由调用方的 activate 完成。
    返回 sync(),供外部在激活项变化后重算 tabindex(如子标签重建后)。 */
export function bindTablist(navEl, itemSelector, activate) {
  const items = () => $$(itemSelector, navEl);
  const sync = () => items().forEach(el =>
    el.setAttribute('tabindex', el.classList.contains('is-active') ? '0' : '-1'));
  navEl.addEventListener('keydown', e => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight' && e.key !== 'Home' && e.key !== 'End') return;
    const arr = items();
    const i = arr.indexOf(document.activeElement);
    if (i < 0) return;
    e.preventDefault();
    const next = e.key === 'Home' ? arr[0]
      : e.key === 'End' ? arr[arr.length - 1]
      : arr[(i + (e.key === 'ArrowRight' ? 1 : arr.length - 1)) % arr.length];
    next.focus();
    activate(next);
    sync();
  });
  sync();
  return sync;
}

