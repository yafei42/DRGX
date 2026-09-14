// ---- 域间依赖 ----
// 状态层只依赖叶子模块 dom.js。它需要触发的「上层动作」(重算单病例、重灌费用节)改为
// 回调注入 —— 若在此 import fee.js/group.js,而那两个模块又要读本模块的区域状态,就会成环。
import { $, api, badgeEl, el, setHidden } from './dom.js';

// -------------------------- 上层动作注入(打破循环依赖) --------------------------
// 依赖方向恒为:group.js / fee.js → state.js(注册) ;state.js 只持有函数引用,不 import 对方。
let rerunSingleHook = null;               // group.js 注入:区域切换后用当前病例重算
let feeFormHooks = { init: null, invalidate: null }; // fee.js 注入:重灌/失效左侧费用节
let codeVersionChanged = null;            // main.js 注入:编码版本切换后重绘输入区

export function setRerunSingleHook(fn) { rerunSingleHook = fn; }
export function setFeeFormHooks(hooks) { feeFormHooks = { ...feeFormHooks, ...hooks }; }
export function setCodeVersionChangedHook(fn) { codeVersionChanged = fn; }

/** 用当前病例按最新口径重算一次(区域/编码版本切换共用)。
    批量结果保持分组时的口径快照,不重算 —— 与区域切换同一约定。 */
export function rerunLastCase(extra = {}) {
  if (lastCase && !state.inFlight) rerunSingleHook?.({ ...lastCase.body, ...extra });
}

/** 结果卡头部“口径胶囊”:让每个结果都能一眼追溯到映射/区域/医保类型/权重口径。 */
export function buildScopePill() {
  const bits = [];
  // 编码版本是这一例"码是怎么来的"的第一手信息:国临版会转码,医保版不会
  bits.push(codeVersion === 'yibao' ? '编码·医保版' : '编码·国临版→医保版');
  if (currentRegion) {
    const r = regionList.find(x => x.key === currentRegion);
    bits.push(r ? `${r.regionName || r.key} ${r.dataVersion || ''}`.trim() : currentRegion);
  }
  const level = $('#feeLevelSel')?.value;
  if (level) bits.push(level);
  const type = $('#feeTypeSel')?.value;
  if (type) bits.push(type);
  return badgeEl({ tone: 'neutral', text: bits.join(' · '), title: '本次分组与费用测算使用的口径' });
}
// -------------------------------- 状态 ------------------------------------

export const state = { gender: null, inFlight: false };
export const fields = {};
export let lastCase = null;          // 最近一次单病例/HIS 的请求体与编码名称,供「换主诊断模拟」复用

// -------------------------- 编码版本(全局输入口径) --------------------------
// 国临版:输入按国临目录受理,分组前自动转医保版(两版之间**同码不同义**的 249 条只有
//   显式选版本才对得上 —— 早先"有映射就转"的无条件兜底会把医保版输入误转)。
// 医保版:输入即分组口径,不做任何转换。
// 单病例 / 批量文件 / HIS 三条录入通道共用一个选择,记忆在 localStorage。

export const CODE_VERSION_KEY = 'drg.code.version';
export let codeVersion = 'guolin';          // 'guolin' | 'yibao'
export let codeVersionLabel = '国临版';      // 展示用,由 /api/info 下发(前端不写死标签)

/** 版本形参:分组/检索接口统一用 version=guolin|yibao(服务端也接受旧 useCodeMap)。 */
export function codeVersionParam() { return { version: codeVersion }; }

/** 切换编码版本:记忆 → 顶栏高亮(含首屏用的 html[data-codever]) → 通知上层重绘与重算。 */
export function setCodeVersion(v) {
  codeVersion = v === 'yibao' ? 'yibao' : 'guolin';
  codeVersionLabel = (codeVersionOptions.find(o => o.id === codeVersion) || {}).label
    || (codeVersion === 'yibao' ? '医保版' : '国临版');
  try { localStorage.setItem(CODE_VERSION_KEY, codeVersion); } catch { /* localStorage 不可用 */ }
  document.documentElement.dataset.codever = codeVersion;
  codeVersionChanged?.();
}

/** 启动期恢复上次选择(顶栏高亮由 html[data-codever] 在样式表前定好,这里只同步 JS 状态)。 */
export function restoreCodeVersion() {
  let saved = '';
  try { saved = localStorage.getItem(CODE_VERSION_KEY) || ''; } catch { /* 同上 */ }
  codeVersion = saved === 'yibao' ? 'yibao' : 'guolin';
}

export let codeVersionOptions = [];   // [{ id, label, title, diagnoses, procedures }]
let codeVersionsLoaded = false;
/** 版本选项与各自字典规模取自 /api/info,不在前端抄一份(抄了迟早变成"界面写国临版、实际按医保版分")。 */
export async function ensureCodeVersions() {
  if (codeVersionsLoaded) return codeVersionOptions;
  try {
    const info = await api.get('/api/info');
    codeVersionOptions = info?.codeVersions?.options || [];
    if (codeVersionOptions.length) {
      codeVersionsLoaded = true;
      codeVersionLabel = (codeVersionOptions.find(o => o.id === codeVersion) || {}).label || codeVersionLabel;
    }
  } catch { codeVersionOptions = []; }
  return codeVersionOptions;
}

// -------------------------- 区域选择(来自 /api/fee/regions) --------------------------
// 费用数据(RW/参考费用)按区域费用包组织:服务端启动时扫描全部生效包,前端下拉选择,
// 请求级 region 参数决定用哪个包;选择记忆在 localStorage,跨会话恢复。

export let regionList = [];      // [{ key, regionName, dataVersion, isDefault }]
export let currentRegion = '';   // 当前选中 key;'' 表示无区域包(相关请求不带 region)
export let regionsLoaded = false;

export async function ensureRegions() {
  if (regionsLoaded) return regionList;
  try {
    regionList = (await api.get('/api/fee/regions')) || [];
    regionsLoaded = true; // 成功才置位:启动瞬间后端未就绪时下次调用仍会重试,不把空清单钉死整个会话
  } catch { regionList = []; }
  const wrap = $('#regionWrap');
  const sel = $('#regionSel');
  if (!regionList.length || !sel || !wrap) return regionList; // 无区域包:隐藏选择器,纯分组模式
  sel.textContent = '';
  regionList.forEach(r => sel.appendChild(el('option', {
    value: r.key,
    // reference 包(非本地公布值)与演示数据在下拉里就地标注,选择前即见
    text: (r.regionName || r.id || r.key) + ' · ' + r.dataVersion
      + (r.demo ? ' · 演示数据' : '')
      + (r.dataLevel === 'reference' ? ' · 非本地值' : ''),
  })));
  // 恢复上次选择;记忆失效(包已下线)时回退服务端默认
  let saved = '';
  try { saved = localStorage.getItem('drg.fee.region') || ''; } catch { /* localStorage 不可用 */ }
  currentRegion = regionList.some(r => r.key === saved) ? saved
    : (regionList.find(r => r.isDefault) || regionList[0]).key;
  sel.value = currentRegion;
  if (!sel.dataset.bound) {           // 热更新自愈会重入 ensureRegions,监听只绑一次
    sel.dataset.bound = '1';
    sel.addEventListener('change', onRegionChange);
  }
  setHidden(wrap, false);
  return regionList;
}

/** 当前区域参数:'' 时不带(服务端回退默认包),接口向后兼容。 */
export function regionParam() { return currentRegion ? { region: currentRegion } : {}; }

/** 切换区域:记忆 → 失效选项缓存 → 重载在看的区域相关数据 → 单病例结果自动重算。 */
export function onRegionChange() {
  currentRegion = $('#regionSel').value;
  try { localStorage.setItem('drg.fee.region', currentRegion); } catch { /* 忽略 */ }
  feeFormHooks.invalidate?.(); // 选项/地区名缓存按区域失效
  feeFormHooks.init?.();       // 左侧费用节的等级/类型选项随区域包重灌(重灌完自动重测)
  rerunLastCase({ region: currentRegion || undefined });
}

// -------------------------- 左侧「费用测算」节(index.html 第4节) --------------------------
// 系数/点值由地区包算法自动算,左侧只暴露 医院等级/医保类型/实际费用 三个选择性输入;
/** 区域 key 失效(换版关闭旧包/热更新)时自愈:重拉区域清单回退默认包,并重灌左侧费用节。 */
export async function refreshRegions() {
  try { localStorage.removeItem('drg.fee.region'); } catch { /* 忽略 */ }
  currentRegion = '';
  feeFormHooks.invalidate?.();
  regionsLoaded = false;
  regionList = [];
  await ensureRegions();
  feeFormHooks.init?.(); // 重灌左侧费用节选项,并重测在看的费用面板
}

export function setLastCase(v) { lastCase = v; }   // 跨域赋值入口(group/his);读取走 live binding

