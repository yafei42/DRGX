// ---- 域间依赖(拆分脚本自动生成) ----
import { $, $$, DEBOUNCE_SEARCH_MS, announce, api, bindTablist, csvCell, debounce, downloadCsv, el, fmtInt, setHidden, staleGuard } from './dom.js';
import { onDataTabEnter, switchTab } from './nav.js';

// ------------------------------ 数据一览(官方配置表,只读) -----------------------------
//
// 数据源唯一:data/packs/<pack>/official/ 下的官方配置信息工作簿,由 /api/official 原样回放
// (表格 = 工作簿的一张 sheet,行 = sheet 的一行,键 = 表头列名)。前端不做任何二次加工:
//   * 显示哪些列、每列怎么渲染,全部由后端下发的列定义决定(列数恒等于该 sheet 的逻辑列数);
//   * 不再并入编译后 DataPack 的派生数据,也不 join 地区费用包。
// 列定义里带 link 的是官方表之间的外键(如 ADRG.mdc → MDC.code),渲染为可下钻的链接。
//
// 唯一的例外是分组主干:MDC / ADRG / DRG 三张表彼此是父子关系,平铺成三个子标签读不出层级,
// 改由「分组主干树」呈现(服务端 /api/official/tree 按外键嵌好下发,前端只负责展开与检索)。
// 该树顶替了原先的三个子标签 id,故 tables 里不再出现 mdc/adrg/drg。

export const dataState = { tab: '', q: '', drill: null, offset: 0, limit: 50, total: 0, sort: '', dir: 'asc' };
export const loadTableGuard = staleGuard(); // 过期响应守卫:下钻/翻页会连续触发,晚到的旧响应必须丢弃

let tables = [];          // /api/official/tables 的表清单(含列定义与全表行数;首项为分组主干树)
let setIds = new Set();   // 官方集合编号:DSL 列内出现的集合号渲染为下钻链接
let drillStack = [];      // 下钻历史:[{tab, drill, q}],支撑「返回上级」
let bootPromise = null;   // 首屏装配(表清单 + 默认表)只跑一次,后续调用共用同一 Promise
let syncSubtabs = null;   // 子标签 roving tabindex 同步器(bindTablist 返回,重建后需重算)

/* ------------------------- 层级树(kind === 'tree',目前两棵) -------------------------
   「数据一览」里有两处数据是层级关系,平铺成宽表读不出结构,都由树呈现:
     * 分组主干 MDC → ADRG → DRG(表 id = tree):官方三张表按外键嵌成整树一次下发(约 90KB),
       展开态/检索/导出全在本地 —— 全树在内存,本地检索比往返服务端更快,且不受分页限制。
     * 编码集合 类型 → 集合(表 id = sets):只下发前两级,成员(106,950 行)由右栏按需分页拉,
       故它的检索走服务端(见 /api/official/setsearch)。
   树不参与分页与排序,与服务端分页表格共用同一条工具条,由 def.kind === 'tree' 分流。
   两棵树的状态各自独立(展开态 / 选中项 / 检索词 / 已加载成员),故按表 id 分仓存。 */
const TREE_TAB = 'tree';
/** 编码集合树的表 id(与 OfficialSetTree.Id 同名)。 */
const SET_TREE = 'sets';
/** 官方表 id → 树层级 id(结果卡跳转:原来按表下钻,现在按层级定位)。
    集合两表都落到「集合」层 —— 它们本就是一个集合的索引与明细两侧。 */
/** 集合树成员每批拉取的条数(右栏「加载更多」的步长,也是服务端上限)。 */
const SET_MEMBER_PAGE = 1000;
/** 集合树检索一次回传的命中上限(集合与成员各自)。 */
const SET_SEARCH_LIMIT = 300;

const trees = new Map();   // 表 id → 树状态(见 treeState)

function isTreeTab() { return (currentDef() || {}).kind === 'tree'; }
function treeKey(level, code) { return `${level}:${code}`; }

/** 按键取树上的节点:用属性比较代替 `[data-key="${key}"]` 拼选择器 —— 码值里只要出现引号或
    `]`,选择器就整个抛异常,而 key 一部分来自后端码值,前端不该对它的字符集做假设。 */
function findTreeItem(key) { return $$('#dataTreeList .tree-item').find(n => n.dataset.key === key); }

/** 一棵树的私有状态。懒建:只有进过该子标签的树才有状态。 */
function treeState(id) {
  let s = trees.get(id);
  if (!s) {
    s = {
      id,
      data: null,          // { levels:[{id,label,tag,count,zeroCount}], zeroTotal, memberTotal, nodes:[...] }
      promise: null,       // 首次拉树;失败即丢弃,下次进入重试(与其它接口同一习惯)
      index: new Map(),    // `${levelId}:${code}` → { node, levelIdx, levelId, parentKey, key }
      parentKeys: [],      // 有下级的节点 key(决定「全部展开 / 全部折叠」的判据)
      open: new Set(),     // 已展开节点的 key
      selected: '',        // 左树当前选中的节点 key(右侧详情随之切换)
      query: '',           // 树内检索词(主干树本地匹配,集合树交给服务端)
      member: null,        // 集合树:选中集合已加载的成员 { setId, rows, total, error }
      search: null,        // 集合树:检索结果 { q, sets, members, memberTotal, loading, error }
      memberGuard: staleGuard(),   // 成员分页与检索各用一条守卫:两者可能同时在飞,不能互相作废
      searchGuard: staleGuard(),
    };
    trees.set(id, s);
  }
  return s;
}

function currentTree() { return isTreeTab() ? treeState(dataState.tab) : null; }

/* DSL 折叠阈值:520px 宽 + 13px 等宽 ≈ 65 字/行,2 行约 130 字;短条件不产生多余「展开」控件 */
const DSL_CLAMP_CHARS = 130;

function currentDef() { return tables.find(t => t.id === dataState.tab) || null; }

function tableLabel(id) {
  const t = tables.find(x => x.id === id);
  return t ? t.label : id;
}

function colLabel(tableId, col) {
  const t = tables.find(x => x.id === tableId);
  const c = t && t.cols.find(x => x.key === col);
  return c ? c.label : col;
}

export function dataPages() { return Math.max(1, Math.ceil(dataState.total / dataState.limit)); }

/** 首次切到「数据一览」才装配与加载,避免拖慢首屏;返回 Promise 供跳转方等待。 */
export function ensureDataLoaded() {
  if (!bootPromise) bootPromise = bootData();
  return bootPromise;
}

// 本页自己登记「进入即装配」,而不是让导航层反向 import 本模块(那样 main ↔ data 会成环)。
onDataTabEnter(ensureDataLoaded);

async function bootData() {
  bindDataEvents();
  await loadTables();
  if (tables.length) buildSubtabs();
  if (tables.length) selectDataTab(tables[0].id);
}

async function loadTables() {
  try {
    const meta = await api.get('/api/official/tables');
    tables = meta.tables || [];
    setIds = new Set(meta.setIds || []);
    if (!tables.length) renderMissing(meta.dir || '');
  } catch (err) {
    renderMissing('', err.message);
  }
}

/** 官方目录缺失/接口异常:表格区直接给出原因,不留空白界面。 */
function renderMissing(dir, extra) {
  setHidden($('#dataDrillBar'), true);
  setHidden($('#dataTablePane'), true);
  setHidden($('#dataTreePane'), true);
  setHidden($('#dataPager'), true);
  $('#dataHead').textContent = '';
  const body = $('#dataBody');
  body.textContent = '';
  body.appendChild(el('tr', {}, [el('td', { colspan: 1, class: 'cell-muted', text: dir
    ? `未找到官方配置表目录:${dir}${extra ? ' · ' + extra : ''}`
    : `官方配置表加载失败:${extra || '未知错误'}` })]));
  $('#dataMeta').textContent = '';
  $('#dataPage').textContent = '';
}

export function buildSubtabs() {
  const nav = $('#dataSubtabs');
  nav.textContent = '';
  const active = t => t.id === dataState.tab;
  tables.forEach(t => nav.appendChild(el('button', {
    type: 'button',
    class: 'subtab' + (active(t) ? ' is-active' : ''),
    role: 'tab',
    'aria-selected': String(active(t)),
    'aria-controls': 'dataTableCard',
    'data-id': t.id,
    title: t.desc || '',
    onclick: () => selectDataTab(t.id),
  }, [
    document.createTextNode(t.label),
    el('span', { class: 'tab-count', text: fmtInt(t.total) }),
  ])));
}

function setActiveTab(id) {
  dataState.tab = id;
  $$('.subtab', $('#dataSubtabs')).forEach(b => {
    const on = b.dataset.id === id;
    b.classList.toggle('is-active', on);
    b.setAttribute('aria-selected', String(on));
  });
  if (syncSubtabs) syncSubtabs();   // 激活项变化后重算 roving tabindex
}

function clearSearch() {
  dataState.q = '';
  dataState.offset = 0;
  // 两棵树的检索词一并清掉:切表 / 下钻返回后回到的应是"这个视图的默认态",
  // 留着上一个视图的检索词只会让左树莫名其妙地少了一半节点。
  trees.forEach(s => { s.query = ''; s.search = null; s.searchGuard.next(); });
  const box = $('#dataSearch');
  if (box) box.value = '';
}

export function selectDataTab(id) {
  clearSearch();
  dataState.drill = null;
  drillStack.length = 0;
  setActiveTab(id);
  renderMeta();
  renderDrillBar();
  // 树与分页表格互斥:同一个卡片里换的是内容区(见 showDataPane)。
  // 判据用"这张表是不是树",而不是某个固定的树 id —— 现在有两棵树。
  if (isTreeTab()) loadTree(); else loadTable();
}

/** 清除排序(切表 / 下钻到另一张表时调用)。 */
function resetSort() { dataState.sort = ''; dataState.dir = 'asc'; }

/** 点击表头切换排序:同列切升/降,换列从升序开始。 */
function toggleSort(key) {
  if (!key) return;
  if (dataState.sort === key) dataState.dir = dataState.dir === 'desc' ? 'asc' : 'desc';
  else { dataState.sort = key; dataState.dir = 'asc'; }
  dataState.offset = 0;
  loadTable();
}

/** 当前表的公共查询参数(下钻 / 检索 / 排序):列表与导出共用同一口径。 */
function baseParams() {
  const def = currentDef();
  const p = { t: def ? def.id : '' };
  if (dataState.q) p.q = dataState.q;
  if (dataState.drill) { p.f = dataState.drill.col; p.v = dataState.drill.value; }
  if (dataState.sort) { p.sort = dataState.sort; p.sortDir = dataState.dir; }
  return p;
}

/** 外键下钻:切到目标表并按 col=value 过滤;来源位置入栈,可逐级返回。 */
export function drillTo(tableId, col, value) {
  if (!tables.some(t => t.id === tableId)) return;
  drillStack.push({ tab: dataState.tab, drill: dataState.drill, q: dataState.q });
  clearSearch();
  setActiveTab(tableId);
  resetSort();                 // 目标表与来源表不同,排序键不通用
  dataState.drill = { col, value: String(value || ''), label: colLabel(tableId, col) };
  renderMeta();
  renderDrillBar();
  loadTable();
}

export function drillBack() {
  const prev = drillStack.pop();
  if (!prev) { clearDrill(); return; }
  clearSearch();
  setActiveTab(prev.tab);
  dataState.drill = prev.drill;
  dataState.q = prev.q || '';
  if (dataState.q) $('#dataSearch').value = dataState.q;
  renderMeta();
  renderDrillBar();
  loadTable();
}

export function clearDrill() {
  dataState.drill = null;
  dataState.offset = 0;
  renderDrillBar();
  loadTable();
}

export function bindDataEvents() {
  const searchDebounced = debounce(v => { dataState.q = v.trim(); dataState.offset = 0; loadTable(); }, DEBOUNCE_SEARCH_MS);
  $('#dataSearch').addEventListener('input', e => {
    const s = currentTree();
    if (s) {
      // 树检索:主干树全树在内存 —— 即时本地过滤,不走防抖也不问服务端;
      // 集合树的成员在服务端(106,950 行),只能服务端检索,故走防抖 + 过期响应守卫。
      s.query = e.target.value.trim();
      if (s.id === SET_TREE) {
        if (!s.query) { s.search = null; s.searchGuard.next(); }   // 清空检索 = 立刻回到节点视图
        else setSearchDebounced(s, s.query);
      }
      renderTree();
      renderTreeDetail();
      return;
    }
    searchDebounced(e.target.value);
  });
  $('#dataPrev').addEventListener('click', () => {
    dataState.offset = Math.max(0, dataState.offset - dataState.limit); loadTable();
  });
  $('#dataNext').addEventListener('click', () => {
    if (dataState.offset + dataState.limit >= dataState.total) return;
    dataState.offset += dataState.limit; loadTable();
  });
  const jump = $('#dataJump');
  const jumpTo = () => {
    const p = Math.min(dataPages(), Math.max(1, parseInt(jump.value, 10) || 1));
    dataState.offset = (p - 1) * dataState.limit;
    loadTable();
  };
  jump.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); jumpTo(); } });
  jump.addEventListener('change', () => { if (jump.value) jumpTo(); });

  // 表头排序:整格是一个按钮,点击切升/降(键盘 Enter/Space 由 button 原生支持)
  $('#dataHead').addEventListener('click', e => {
    const btn = e.target.closest('.th-sort-btn');
    if (btn) toggleSort(btn.dataset.key);
  });
  // 每页条数:改变即回到第 1 页(偏移量在新页长下会指向错误位置)
  $('#dataPageSize').addEventListener('change', e => {
    dataState.limit = Math.max(1, parseInt(e.target.value, 10) || 50);
    dataState.offset = 0;
    loadTable();
  });
  $('#dataExport').addEventListener('click', exportDataCsv);
  $('#dataTreeExpand').addEventListener('click', toggleAllTree);
  // 左树交互集中委托在列表容器:千级节点逐行注册监听器既慢又难维护。
  // ▸ 箭头 = 只展开 / 折叠;点行其余部分 = 选中(右侧详情切换)。选中带下级的节点时顺手展开,
  //   这是目录树的主浏览动线:沿右侧「下级清单」或左树逐层往下点,不必先找箭头。
  const list = $('#dataTreeList');
  list.addEventListener('click', e => {
    const item = e.target.closest('.tree-item');
    if (!item) return;
    const key = item.dataset.key;
    if (e.target.closest('.tree-toggle')) { toggleTreeNode(key); return; }
    selectTreeNode(key);
  });
  // 键盘:treeitem 本体可聚焦,Enter/Space 选中;展开/折叠走 item 内的箭头按钮(原生 button)
  list.addEventListener('keydown', e => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    const item = e.target.closest('.tree-item');
    if (!item || e.target.closest('.tree-toggle')) return;
    e.preventDefault();
    selectTreeNode(item.dataset.key);
  });
  // 右侧详情的「下级清单」:点一个下级 = 在左树里选中它(先展开其祖先保证可见)
  $('#dataTreeDetail').addEventListener('click', e => {
    const dslBtn = e.target.closest('.dsl-toggle');
    if (dslBtn) { toggleDsl(dslBtn); return; }
    const kid = e.target.closest('.tree-kid');
    if (kid) selectTreeNode(kid.dataset.key);
  });
  // 子标签也是 tablist:方向键/Home/End + roving tabindex,与顶部页签同一套实现
  syncSubtabs = bindTablist($('#dataSubtabs'), '.subtab', el => selectDataTab(el.dataset.id));
}

/** 导出当前筛选 / 排序结果为 CSV。
    分页表格:服务端单次上限 500 行,故按页拉全表;设 2 万行上限防止超大表把浏览器拖死,截断时明确告知。
    层级树:导出的是"当前检索命中的全部节点"(与展开态无关 —— 导数据不该受折叠影响)。
      * 主干树 —— 层级 / 编码 / 名称 / 入组条件 / 下级数;
      * 集合树 —— 类型 / 集合号 / 成员数(成员清单另有「导出成员」,在右栏按集合导)。 */
async function exportDataCsv() {
  const def = currentDef();
  const btn = $('#dataExport');
  if (!def || btn.disabled) return;
  const label = btn.textContent;
  btn.disabled = true; btn.textContent = '导出中…';
  let doneMsg = '';
  try {
    if (def.kind === 'tree') {
      const s = treeState(def.id);
      if (s.id === SET_TREE) {
        const lines = ['集合类型,集合号,成员数'];
        const hit = setFilterOf(s);
        let n = 0;
        visibleRoots(s).forEach(root => kidsOf(root).forEach(n2 => {
          if (hit && !hit.has(n2.code)) return;
          lines.push([root.code, n2.code, n2.count == null ? '' : n2.count].map(csvCell).join(','));
          n++;
        }));
        downloadCsv('drg-code-sets.csv', lines);
        doneMsg = `已导出 ${fmtInt(n)} 个集合`;
        announce(doneMsg);
        return;
      }
      const lines = ['层级,编码,名称,入组条件(DSL),下级数'];
      const q = s.query.trim().toLowerCase();
      const picked = [];
      const walk = (nodes, level) => (nodes || []).forEach(n => {
        const kids = kidsOf(n);
        if (!q || nodeMatches(n, q) || subtreeMatches(kids, q)) picked.push([level, n]);
        walk(kids, level + 1);
      });
      walk(visibleRoots(s), 0);
      const tagOf = level => ((s.data?.levels || [])[level] || {}).tag || '';
      // 末列是节点名下挂的可见下级数(与屏上「N 个下级」同口径)
      picked.forEach(([level, n]) => lines.push([
        tagOf(level), n.code, n.name || '', n.dsl || '', kidsOf(n).length,
      ].map(csvCell).join(',')));
      downloadCsv('drg-mdc-adrg-drg.csv', lines);
      doneMsg = `已导出 ${fmtInt(picked.length)} 行`;
      announce(doneMsg);
      return;
    }
    const PAGE = 500, MAX_ROWS = 20000;
    const lines = [def.cols.map(c => csvCell(c.label)).join(',')];
    let off = 0, total = 0;
    do {
      const data = await api.get('/api/official/rows', { ...baseParams(), offset: off, limit: PAGE });
      total = data.total || 0;
      const rows = data.rows || [];
      rows.forEach(r => lines.push(def.cols.map(c => csvCell(r[c.key])).join(',')));
      off += rows.length;
      if (!rows.length || off >= MAX_ROWS) break;
    } while (off < total);
    downloadCsv(`drg-${def.id}.csv`, lines);
    doneMsg = `已导出 ${fmtInt(off)} 行`;
    announce(off < total ? `已导出前 ${fmtInt(off)} 行(超过单次导出上限,已截断)` : doneMsg);
  } catch (e) {
    doneMsg = '导出失败';
    announce('导出失败:' + e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = doneMsg || label;
    if (doneMsg) setTimeout(() => { btn.textContent = label; }, 1400);
  }
}

export async function loadTable() {
  const seq = loadTableGuard.next();
  const def = currentDef();
  if (!def) return;
  // 防御:层级树没有服务端分页语义,任何走到这里的调用都转到树视图
  // (排序/翻页/下钻控件在树模式下整体隐藏,正常路径不会到这)
  if (def.kind === 'tree') { loadTree(); return; }
  showDataPane(false);
  const head = $('#dataHead');
  const body = $('#dataBody');

  head.textContent = '';
  // 每列表头都是可排序按钮:行为在 #dataHead 的点击委托里(toggleSort),
  // 排序键与方向经 baseParams() 送往服务端(数值列按数值比、空值恒沉底)。
  def.cols.forEach(c => head.appendChild(sortableTh(c)));
  body.textContent = '';
  body.appendChild(placeholderRow(def.cols.length, '加载中…'));

  const params = { ...baseParams(), offset: dataState.offset, limit: dataState.limit };

  try {
    const data = await api.get('/api/official/rows', params);
    if (!loadTableGuard.is(seq)) return;   // 已有更新请求:丢弃过期响应,防止旧表覆盖新表
    dataState.total = data.total || 0;
    renderRows(def, data.rows || []);
  } catch (err) {
    if (!loadTableGuard.is(seq)) return;
    body.textContent = '';
    body.appendChild(placeholderRow(def.cols.length, '加载失败: ' + err.message));
  }
  renderPager();
}

// ============================ 层级树(分组主干 / 编码集合) ============================

/** 树与分页表格共用同一张卡片:切换的是内容区与随之失效的控件(分页 / 每页条数 / 树工具)。 */
function showDataPane(treeMode) {
  setHidden($('#dataTablePane'), treeMode);
  setHidden($('#dataTreePane'), !treeMode);
  setHidden($('#dataPager'), treeMode);          // 树不分页
  setHidden($('#dataPageSizeWrap'), treeMode);
  setHidden($('#dataTreeExpand'), !treeMode);
  if (!treeMode) {
    const box = $('#dataSearch');
    if (box) box.placeholder = '在本表全部列中检索…';
    return;
  }
  const sets = dataState.tab === SET_TREE;
  const side = $('#dataTreeList')?.parentElement;
  if (side) side.setAttribute('aria-label', sets ? '编码集合目录' : '分组主干目录');
  const box = $('#dataSearch');
  if (box) box.placeholder = sets
    ? '在集合号与成员编码、名称中检索…'     // 集合没有官方中文名,"按名找集合"实际是"按成员名找集合"
    : '在 MDC / ADRG / DRG 的编码、名称与入组条件中检索…';
}

/** 某棵树只拉一次:失败不缓存 Promise(下次进入重试),避免"启动瞬间后端未就绪"钉死整个会话。 */
function ensureTree(s) {
  if (!s.promise) {
    s.promise = api.get('/api/official/tree', { t: s.id })
      .then(d => {
        s.data = d;
        s.index = buildTreeIndex(s, d.nodes || []);
        return d;
      })
      .catch(err => { s.promise = null; throw err; });
  }
  return s.promise;
}

async function loadTree() {
  const s = currentTree();
  if (!s) return;
  const seq = loadTableGuard.next();
  showDataPane(true);
  const list = $('#dataTreeList');
  list.textContent = '';
  list.appendChild(el('div', { class: 'tree-note', text: '加载中…' }));
  $('#dataTreeDetail').textContent = '';
  try {
    await ensureTree(s);
  } catch (err) {
    if (!loadTableGuard.is(seq)) return;
    list.textContent = '';
    list.appendChild(el('div', { class: 'tree-note', text: '加载失败: ' + err.message }));
    return;
  }
  if (!loadTableGuard.is(seq)) return;
  computeVisibleParents(s);
  // 选中项失效(首次进入 / 检索把它滤掉了)时落到第一个可见根,详情区不留空白
  if (!s.selected || !isTreeRowVisible(s, s.selected)) s.selected = treeKey(treeLevelId(s, 0), visibleRoots(s)[0]?.code ?? '');
  if (s.selected) s.open.add(s.selected);   // 选中有下级时展开它:下级清单与左树同步
  renderTree();
  renderTreeDetail();
  const hit = s.index.get(s.selected);
  if (hit && hit.levelIdx === 1 && s.id === SET_TREE) ensureSetMembers(s, hit.node.code);
}

/** 层级 id 按 /api/official/tree 的 levels 顺序取
    (主干树与官方表 id 同名 mdc / adrg / drg;集合树为 settype / set)。 */
function treeLevelId(s, i) { return ((s.data?.levels || [])[i] || {}).id || String(i); }

function buildTreeIndex(s, nodes) {
  const idx = new Map();
  const walk = (list, i, parentKey) => (list || []).forEach(n => {
    const key = treeKey(treeLevelId(s, i), n.code);
    idx.set(key, { node: n, levelIdx: i, levelId: treeLevelId(s, i), parentKey, key });
    walk(n.children, i + 1, key);
  });
  walk(nodes, 0, '');
  return idx;
}

/** 下级清单(主干树的 00 类服务端已剔除,这里无需再过滤)。 */
function kidsOf(node) { return node?.children || []; }

/** 根节点清单(主干树的 MDC 0000 已被服务端剔除)。 */
function visibleRoots(s) { return s.data?.nodes || []; }

/** 可见层级里有下级的节点 key(「全部展开 / 全部折叠」的判据)。 */
function computeVisibleParents(s) {
  const parents = [];
  const walk = (nodes, levelIdx) => (nodes || []).forEach(n => {
    const kids = kidsOf(n);
    if (!kids.length) return;
    parents.push(treeKey(treeLevelId(s, levelIdx), n.code));
    walk(kids, levelIdx + 1);
  });
  if (s.data) walk(visibleRoots(s), 0);
  s.parentKeys = parents;
}

function nodeMatches(n, q) {
  return n.code.toLowerCase().includes(q)
    || (n.name || '').toLowerCase().includes(q)
    || (n.dsl || '').toLowerCase().includes(q);
}

function subtreeMatches(kids, q) {
  return (kids || []).some(k => nodeMatches(k, q) || subtreeMatches(k.children, q));
}

/** 集合树检索态下"左树该显示哪些集合":以服务端命中为准 ——
    集合号命中 ∪ 成员命中所属的集合。这样搜一个 ICD 编码也能落到拥有它的那些集合上。
    非检索态(或结果与当前检索词不一致,即还在飞)返回 null = 不过滤。 */
function setFilterOf(s) {
  if (s.id !== SET_TREE || !s.search || !s.search.q || s.search.q !== s.query) return null;
  const ids = new Set();
  (s.search.sets || []).forEach(r => ids.add(r.set_id));
  (s.search.members || []).forEach(r => ids.add(r.set_id));
  return ids;
}

/** 当前可见的树行(展开态 + 检索命中)。
    主干树检索激活时忽略手动折叠:命中节点的全路径自动展开,否则"搜到了却看不到"更令人困惑。 */
function visibleTreeRows(s) {
  if (!s.data) return [];
  const q = s.query.trim().toLowerCase();
  const hit = setFilterOf(s);
  const out = [];
  const walk = (nodes, levelIdx, parentKey) => {
    const levelId = treeLevelId(s, levelIdx);
    (nodes || []).forEach(n => {
      const kids = kidsOf(n);
      if (hit) {
        // 集合树检索态:只留命中集合;某类型下没有命中集合时连类型根一起收起
        if (levelIdx === 0 && !kids.some(k => hit.has(k.code))) return;
        if (levelIdx > 0 && !hit.has(n.code)) return;
      } else if (q && !nodeMatches(n, q) && !subtreeMatches(kids, q)) {
        return;
      }
      const key = treeKey(levelId, n.code);
      out.push({ node: n, levelIdx, levelId, key, parentKey });
      if (!s.data.levels[levelIdx + 1]) return;      // 叶子层,无下级
      if (!hit && !q && !s.open.has(key)) return;
      walk(kids, levelIdx + 1, key);
    });
  };
  walk(visibleRoots(s), 0, '');
  return out;
}

/** 选中节点当前是否在左树可见(未被检索滤掉、祖先都在展开路径上)。 */
function isTreeRowVisible(s, key) {
  return visibleTreeRows(s).some(r => r.key === key);
}

function renderTree() {
  const s = currentTree();
  const list = $('#dataTreeList');
  if (!s || !list) return;
  list.textContent = '';
  const rows = visibleTreeRows(s);
  if (!rows.length) {
    list.appendChild(el('div', { class: 'tree-note', text: s.query ? '无匹配数据' : '该表暂无数据' }));
    updateTreeExpandBtn(s);
    return;
  }
  const frag = document.createDocumentFragment();
  rows.forEach(r => frag.appendChild(treeItem(s, r)));
  list.appendChild(frag);
  updateTreeExpandBtn(s);
}

/** 左树一行:缩进 + 展开箭头 + 编码 + 名称(集合节点没有官方名,尾注换成成员数)。层级不再用徽标列
    —— 目录树的缩进本身就是层级,徽标挪到右侧详情头部(那里是"内容",需要明示这行是什么)。 */
function treeItem(s, r) {
  const { node, levelIdx, levelId, key } = r;
  const kids = kidsOf(node);
  const open = !!s.query.trim() || s.open.has(key);
  const item = el('div', {
    class: `tree-item lv-${levelId}` + (s.selected === key ? ' is-selected' : ''),
    'data-key': key,
    role: 'treeitem',
    'aria-expanded': kids.length ? String(open) : null,
    'aria-selected': String(s.selected === key),
    tabindex: '0',
  });
  const main = el('div', { class: 'tree-main', style: `--tree-level:${levelIdx}` }, [
    kids.length
      ? el('button', {
          type: 'button', class: 'tree-toggle', 'aria-expanded': String(open),
          title: open ? '收起下级' : `展开 ${fmtInt(kids.length)} 个下级`,
          text: open ? '▾' : '▸',
        })
      : el('span', { class: 'tree-toggle is-leaf', 'aria-hidden': 'true' }),
    el('span', { class: 'tree-code', text: node.code }),
  ]);
  if (node.name) main.appendChild(el('span', { class: 'tree-name', title: node.name, text: node.name }));
  // 集合节点的"内容"就是成员数:集合本身没有官方名称,空着不如把条数摆在行尾
  else if (node.count != null) main.appendChild(el('span', { class: 'tree-meta', text: `${fmtInt(node.count)} 条` }));
  item.appendChild(main);
  return item;
}

/** 选中节点:右栏详情随之切换;带下级时顺手展开,浏览动线不断。 */
function selectTreeNode(key, { expand = true } = {}) {
  const s = currentTree();
  if (!s) return;
  const hit = s.index.get(key);
  if (!hit) return;
  // 集合树检索态下点到一个集合 = "跳到那里":清掉检索,右栏回到它的成员清单。
  // 点类型根不算落点(它只是分层),保留检索继续看该类型下的命中。
  const leaveSearch = s.id === SET_TREE && hit.levelIdx > 0 && !!s.query;
  if (leaveSearch) {
    s.query = '';
    s.search = null;
    s.searchGuard.next();
    const box = $('#dataSearch');
    if (box) box.value = '';
  }
  s.selected = key;
  if (expand && kidsOf(hit.node).length) s.open.add(key);
  renderTree();
  renderTreeDetail();
  // 检索被清掉后左树从"命中集"回到全树,行位置会变:把选中行重新带回视野
  if (leaveSearch) findTreeItem(key)?.scrollIntoView({ block: 'nearest' });
  // 集合树:选中集合才拉成员(懒加载);切到别的节点就丢掉在途结果
  if (s.id === SET_TREE) {
    if (hit.levelIdx === 1) ensureSetMembers(s, hit.node.code);
    else { s.member = null; s.memberGuard.next(); }
  }
}

/** 右侧详情:选中节点的内容 —— 主干树是层级、编码名称、完整入组条件、下级清单;集合树是
    集合号、类型、成员清单(懒加载)。这里才是"数据一览"的阅读区:左树管导航,详情管内容。 */
function renderTreeDetail() {
  const s = currentTree();
  const pane = $('#dataTreeDetail');
  if (!s || !pane) return;
  pane.textContent = '';
  // 集合树检索态:右栏整体让给检索结果(左树只是命中的落点列表)
  if (setFilterOf(s)) { renderSetSearchHits(s, pane); return; }
  const hit = s.index.get(s.selected);
  if (!hit) {
    pane.appendChild(el('div', { class: 'tree-note', text: '在左侧选择一个节点查看内容' }));
    return;
  }
  const { node, levelIdx, levelId } = hit;
  const level = (s.data?.levels || [])[levelIdx] || {};

  // 头部:层级徽标 + 编码 + 名称
  pane.appendChild(el('div', { class: 'tree-detail-head' }, [
    el('span', { class: `lv-tag lv-${levelId}`, text: level.tag || levelId.toUpperCase() }),
    el('span', { class: 'tree-code', text: node.code }),
    el('span', { class: 'tree-detail-name', text: node.name || (node.count != null ? `${fmtInt(node.count)} 条成员` : '') }),
  ]));

  // 面包屑:MDC → ADRG 路径,给"这个节点挂在哪"一个一眼可见的答案
  const crumbs = [];
  for (let k = hit.parentKey; k; k = s.index.get(k)?.parentKey) {
    const p = s.index.get(k);
    if (p) crumbs.unshift(`${p.node.code} ${p.node.name || ''}`.trim());
  }
  if (crumbs.length) {
    pane.appendChild(el('div', { class: 'tree-detail-crumbs', text: crumbs.join(' → ') }));
  }

  // 集合节点:内容 = 成员清单(按需分页),不渲染入组条件(集合没有这条官方口径)
  if (s.id === SET_TREE && levelIdx > 0) {
    renderSetMembers(s, pane, node, s.index.get(hit.parentKey)?.node.code || '');
    return;
  }

  if (s.id === SET_TREE) {
    // 集合类型根(DI / OP):它没有入组条件,DSL 区块对它是噪音 —— 换成"这类集合是什么"的说明
    appendSetIntro(pane, node);
  } else {
    // 入组条件原文:与表格视图同一套折行 / 展开口径(见 .dsl-text / fillDslCell)
    const dslBox = el('div', { class: 'tree-detail-dsl' });
    dslBox.appendChild(el('div', { class: 'tree-detail-label', text: '入组条件' }));
    if (node.dsl) {
      const box = el('div', { class: 'cell-dsl' });
      fillDslCell(box, node.dsl);
      dslBox.appendChild(box);
    } else {
      dslBox.appendChild(el('div', { class: 'cell-muted', text: '—' }));
    }
    pane.appendChild(dslBox);
  }

  // 下级清单:点击即选中(左树自动展开祖先),不依赖左树逐层找箭头
  const kids = kidsOf(node);
  pane.appendChild(el('div', { class: 'tree-detail-label', text: kids.length ? `下级(${fmtInt(kids.length)})` : '下级' }));
  if (!kids.length) {
    pane.appendChild(el('div', { class: 'cell-muted', text: '无(叶子节点)' }));
    return;
  }
  const kidList = el('div', { class: 'tree-kids' });
  kids.forEach(k => {
    const kidKey = treeKey(treeLevelId(s, levelIdx + 1), k.code);
    // 不再挂 title="查看 xxx":编码与名称就在按钮上,点下去是什么结果一眼可见,多余的提示只是噪音
    kidList.appendChild(el('button', {
      type: 'button', class: 'tree-kid',
      'data-key': kidKey,
    }, [
      el('span', { class: 'tree-code', text: k.code }),
      el('span', { class: 'tree-name', text: k.name || (k.count != null ? `${fmtInt(k.count)} 条` : '') }),
    ]));
  });
  pane.appendChild(kidList);
}

// ------------------------------ 编码集合树:成员与检索 ------------------------------

/* 集合是本项目里唯一"没有中文名"的官方数据 —— 集合号即它的名字。一线用户看到 DI_B00 /
   OP_ALL 和一堆 ICD 编码,不知道这是什么、跟自己的病例有什么关系。故右栏除了成员清单,
   还要把"它是什么、编号怎么读、被谁引用"讲清楚:这几句官方表里写不出来,却决定用户能否看懂这一页。 */
const SET_ROLE_VARS = [
  ['ZYZD', '主要诊断'],
  ['QTZD', '其他诊断'],
  ['ZYSS', '主要手术操作'],
  ['QTSS', '其他手术操作'],
  ['in', '属于'],
  ['{A, B}', '任一满足'],
];

/** 集合类型根(DI / OP)的说明:这类集合是什么、编号怎么读、被谁引用。
    按官方 type 分支 —— DI 的成员是 ICD-10 疾病编码,OP 的是 ICD-9-CM-3 手术操作编码。 */
function appendSetIntro(pane, node) {
  const di = String(node.code).toUpperCase().startsWith('DI');
  pane.appendChild(el('div', { class: 'tree-detail-label', text: '这类集合是什么' }));
  const ul = el('ul', { class: 'tree-intro' });
  const li = text => ul.appendChild(el('li', { text }));
  li('集合 = 官方把分组要用的一批编码打成的包。ADRG 的入组条件不写具体编码,而是直接引用集合号 ——'
    + ` 例如「${di ? 'ZYZD in DI1_OF1' : 'ZYSS in OP_OF1'}」读作"对应字段的编码属于这个集合"。`);
  li(di
    ? '前缀 DI / DI1 / DI2 … 都属诊断类,成员是 ICD-10 疾病编码。编号里的 1 / 2 / 3 只用来区分同一个'
      + ' ADRG 下的多套集合(不同条件分支各用一套),不是"第几个诊断"。'
    : '前缀 OP / OP1 / OP2 … 都属手术操作类,成员是 ICD-9-CM-3 手术操作编码。编号里的 1 / 2 / 3 只用来'
      + '区分同一个 ADRG 下的多套集合(不同条件分支各用一套),不是"第几台手术"。');
  li('后缀多与 ADRG 编码同名(如 DI_B00 ↔ ADRG B00),便于按组查找,但不是官方归属关系:'
    + '同一个集合可被多个 ADRG 引用,也有集合没被任何组引用。');
  pane.appendChild(ul);

  // 入组条件里的写法:不摆出这张对照,DSL 原文对一线用户就是天书(主干树与结果卡里都会出现)
  pane.appendChild(el('div', { class: 'tree-detail-label', text: '入组条件里的写法' }));
  const vars = el('div', { class: 'set-vars' });
  SET_ROLE_VARS.forEach(([token, cn]) => vars.appendChild(el('span', { class: 'set-var' }, [
    el('span', { class: 'tree-code', text: token }),
    el('span', { text: cn }),
  ])));
  pane.appendChild(vars);
}

/** 服务端检索(集合号 + 成员编码/名称):集合树唯一需要往返的检索 —— 成员 106,950 行不在前端。 */
const setSearchDebounced = debounce((s, q) => runSetSearch(s, q), DEBOUNCE_SEARCH_MS);

async function runSetSearch(s, q) {
  const seq = s.searchGuard.next();
  s.search = { q, sets: [], members: [], memberTotal: 0, loading: true };
  renderTree();
  renderTreeDetail();
  try {
    const d = await api.get('/api/official/setsearch', { q, limit: SET_SEARCH_LIMIT });
    if (!s.searchGuard.is(seq) || s.query !== q) return;      // 已换词 / 已清空:丢弃过期响应
    s.search = { q, sets: d.sets || [], members: d.members || [], memberTotal: d.memberTotal || 0 };
  } catch (err) {
    if (!s.searchGuard.is(seq) || s.query !== q) return;
    s.search = { q, sets: [], members: [], memberTotal: 0, error: err.message };
  }
  renderTree();
  renderTreeDetail();
}

/** 成员清单懒加载:一次一批(默认 1,000 条,「加载更多」继续追加)。
    单个集合最多 9,629 条,整包下发会拖垮首屏 —— 故按需拉,且始终显示"已加载 / 共多少"。 */
async function ensureSetMembers(s, setId) {
  const cur = (s.member && s.member.setId === setId) ? s.member : { setId, rows: [], total: 0 };
  s.member = cur;
  if (cur.total && cur.rows.length >= cur.total) return;   // 已拉全:重复选中同一集合不再发空请求
  const seq = s.memberGuard.next();
  try {
    const d = await api.get('/api/official/setmembers', { set: setId, offset: cur.rows.length, limit: SET_MEMBER_PAGE });
    if (!s.memberGuard.is(seq) || s.member !== cur) return;   // 已切到别的集合:丢弃过期响应
    cur.rows.push(...(d.rows || []));
    cur.total = d.total || 0;
  } catch (err) {
    if (!s.memberGuard.is(seq) || s.member !== cur) return;
    cur.error = err.message;
  }
  renderTreeDetail();
}

/** 成员清单(右栏):一行说明 + 编码 / 名称的台账式列表,底部「加载更多」与「导出成员」。
    typeCode 是所属集合类型(DI / OP):两类成员用的是不同编码体系,说明里点明,
    免得用户以为诊断码与手术操作码是同一张码表。 */
function renderSetMembers(s, pane, node, typeCode = '') {
  const m = (s.member && s.member.setId === node.code) ? s.member : null;
  const total = m && m.total ? m.total : (node.count || 0);
  // 一句话说清"成员是什么":入组条件里写 DI_B00,判的就是下面这份清单
  pane.appendChild(el('div', {
    class: 'tree-note set-member-note',
    text: '成员 = 这个集合收录的全部'
      + (String(typeCode).toUpperCase().startsWith('OP') ? 'ICD-9-CM-3 手术操作编码' : 'ICD-10 疾病编码')
      + `。入组条件里写 ${node.code},就是"编码落在下面这份清单里"。`,
  }));
  const head = el('div', { class: 'member-head' }, [
    el('span', { class: 'tree-detail-label', text: `成员(${fmtInt(total)})` }),
  ]);
  head.appendChild(el('button', {
    type: 'button', class: 'btn btn-ghost btn-sm', text: '导出成员',
    title: `导出集合 ${node.code} 的全部成员(CSV)`,
    onclick: () => exportSetMembers(node.code),
  }));
  pane.appendChild(head);

  if (!m || (m.error && !m.rows.length)) {
    pane.appendChild(el('div', { class: 'tree-note', text: m?.error ? '加载失败: ' + m.error : '加载中…' }));
    return;
  }
  if (!m.rows.length) {
    pane.appendChild(el('div', { class: 'cell-muted', text: '该集合暂无成员' }));
    return;
  }
  const list = el('div', { class: 'member-list' });
  m.rows.forEach(r => list.appendChild(el('div', { class: 'member-row' }, [
    el('span', { class: 'tree-code', text: r.icd_code }),
    el('span', { class: 'tree-name', text: r.icd_name || '' }),
  ])));
  pane.appendChild(list);

  if (m.rows.length < total) {
    pane.appendChild(el('button', {
      type: 'button', class: 'btn btn-ghost btn-sm member-more',
      text: `加载更多(已 ${fmtInt(m.rows.length)} / 共 ${fmtInt(total)})`,
      onclick: () => ensureSetMembers(s, node.code),
    }));
  } else {
    pane.appendChild(el('div', { class: 'tree-note', text: `已全部加载(${fmtInt(m.rows.length)} 条)` }));
  }
}

/** 导出某个集合的全部成员:按批拉全(单集合最多 9,629 条,一次导完,不截断)。 */
async function exportSetMembers(setId) {
  const MAX_ROWS = 20000;    // 与表格导出同一护栏:防超大集合把浏览器拖死
  const lines = ['集合号,编码,名称'];
  let off = 0, total = 0;
  try {
    do {
      const d = await api.get('/api/official/setmembers', { set: setId, offset: off, limit: SET_MEMBER_PAGE });
      total = d.total || 0;
      const rows = d.rows || [];
      rows.forEach(r => lines.push([setId, r.icd_code, r.icd_name || ''].map(csvCell).join(',')));
      off += rows.length;
      if (!rows.length || off >= MAX_ROWS) break;
    } while (off < total);
    downloadCsv(`drg-set-${setId}.csv`, lines);
    announce(off < total ? `已导出前 ${fmtInt(off)} 条(超过单次导出上限,已截断)` : `已导出 ${fmtInt(off)} 条`);
  } catch (e) {
    announce('导出失败:' + e.message);
  }
}

/** 集合树检索态:右栏整体让给检索结果 —— 集合命中(可点选)+ 成员命中(可跳到所属集合)。
    点任一行都等于"跳到那里":清空检索并选中该集合,回到节点视图看它的成员。 */
function renderSetSearchHits(s, pane) {
  const r = s.search;
  pane.appendChild(el('div', { class: 'tree-detail-head' }, [
    el('span', { class: 'lv-tag', text: '检索' }),
    el('span', { class: 'tree-code', text: r.q }),
  ]));
  if (r.loading) {
    pane.appendChild(el('div', { class: 'tree-note', text: '检索中…' }));
    return;
  }
  if (r.error) {
    pane.appendChild(el('div', { class: 'tree-note', text: '检索失败: ' + r.error }));
    return;
  }

  // 两段命中各自标出"是否被上限截断" —— 只在真截断时提,别让每行都挂一句废话
  const cut = n => n >= SET_SEARCH_LIMIT ? ` · 只列前 ${fmtInt(SET_SEARCH_LIMIT)} 条` : '';
  pane.appendChild(el('div', { class: 'tree-detail-label', text: `集合命中(${fmtInt(r.sets.length)})${cut(r.sets.length)}` }));
  if (!r.sets.length) {
    pane.appendChild(el('div', { class: 'cell-muted', text: '—' }));
  } else {
    const list = el('div', { class: 'tree-kids' });
    r.sets.forEach(row => list.appendChild(el('button', {
      type: 'button', class: 'tree-hit',
      onclick: () => gotoSet(s, row.set_id),
    }, [
      el('span', { class: 'tree-code', text: row.set_id }),
      el('span', { class: 'tree-name', text: `${row.type || ''} · ${fmtInt(row.member_count)} 条成员`.replace(/^ · /, '') }),
    ])));
    pane.appendChild(list);
  }

  pane.appendChild(el('div', {
    class: 'tree-detail-label',
    text: `成员命中(${fmtInt(r.memberTotal)})${r.memberTotal > r.members.length ? ` · 只列前 ${fmtInt(r.members.length)} 条` : ''}`,
  }));
  if (!r.members.length) {
    pane.appendChild(el('div', { class: 'cell-muted', text: '—' }));
    return;
  }
  const mlist = el('div', { class: 'tree-kids' });
  r.members.forEach(row => mlist.appendChild(el('button', {
    type: 'button', class: 'tree-hit',
    title: `跳到集合 ${row.set_id}`,
    onclick: () => gotoSet(s, row.set_id),
  }, [
    el('span', { class: 'tree-setid', text: row.set_id }),
    el('span', { class: 'tree-code', text: row.icd_code }),
    el('span', { class: 'tree-name', text: row.icd_name || '' }),
  ])));
  pane.appendChild(mlist);
}

/** 跳到某个集合:清空检索 + 展开祖先 + 选中(右栏随即加载它的成员)。 */
function gotoSet(s, setId) {
  const key = treeKey(treeLevelId(s, 1), setId);   // 第 1 级即集合层
  if (!s.index.has(key)) return;
  s.query = '';
  s.search = null;
  s.searchGuard.next();
  const box = $('#dataSearch');
  if (box) box.value = '';
  for (let k = s.index.get(key).parentKey; k; k = s.index.get(k)?.parentKey) s.open.add(k);
  selectTreeNode(key);
  findTreeItem(key)?.scrollIntoView({ block: 'center' });
}

/** 展开 / 折叠一个节点:重绘后把焦点还给同一个箭头,键盘用户不至丢失焦点位置。 */
function toggleTreeNode(key) {
  const s = currentTree();
  if (!s) return;
  if (s.query.trim()) return;                                      // 检索态忽略手动折叠
  if (!kidsOf(s.index.get(key)?.node || {}).length) return;         // 叶子无可折叠内容
  if (s.open.has(key)) s.open.delete(key); else s.open.add(key);
  renderTree();
  findTreeItem(key)?.querySelector('.tree-toggle')?.focus({ preventScroll: true });
}

/** 顶部「全部展开 / 全部折叠」:未全展则展开(含全部层级),已全展则折回根层。 */
function toggleAllTree() {
  const s = currentTree();
  if (!s) return;
  const expand = s.open.size < s.parentKeys.length;
  s.open.clear();
  if (expand) s.parentKeys.forEach(k => s.open.add(k));
  renderTree();
}

/** 按"是否还有未展开的父节点"更新按钮文案 —— 按钮说的是动作,不是状态。 */
function updateTreeExpandBtn(s) {
  const btn = $('#dataTreeExpand');
  if (btn) btn.textContent = s.open.size < s.parentKeys.length ? '全部展开' : '全部折叠';
}

/** 定位到某个节点(结果卡 / 官方 DSL 里的集合编号跳转):展开其全部祖先并选中,右栏直接给出详情。
    层级按官方表 id 或层级 id 给出(mdc / adrg / drg / set),与 /api/official/tree 的 levels 同源。 */
async function locateTreeNode(s, levelId, code) {
  await ensureTree(s);
  const key = treeKey(levelId, code);
  const hit = s.index.get(key);
  if (!hit) { announce(`未在该层级树中找到 ${code}`); return false; }
  for (let k = hit.parentKey; k; k = s.index.get(k)?.parentKey) s.open.add(k);
  s.query = '';
  s.search = null;
  s.searchGuard.next();
  const box = $('#dataSearch');
  if (box) box.value = '';
  selectTreeNode(key);
  findTreeItem(key)?.scrollIntoView({ block: 'center' });
  return true;
}

function placeholderRow(colspan, text) {
  return el('tr', {}, [el('td', { colspan, class: 'cell-muted', text })]);
}

/** 可排序表头:整格包一个 button(WAI-ARIA 排序模式,键盘天然可达),
    排序态写进 th 的 aria-sort;caret 常驻(未排序时淡色),避免激活后列宽跳动。 */
function sortableTh(c) {
  const active = dataState.sort === c.key;
  const desc = active && dataState.dir === 'desc';
  const btn = el('button', {
    type: 'button', class: 'th-sort-btn', 'data-key': c.key,
    title: c.hint ? `${c.hint}(点击排序)` : `点击按「${c.label}」排序`,
  }, [
    el('span', { text: c.label }),
    el('span', { class: 'th-caret', 'aria-hidden': 'true', text: desc ? '▼' : active ? '▲' : '↕' }),
  ]);
  return el('th', {
    class: (c.format === 'num' ? 'num' : null) + (active ? ' is-sorted' : ''),
    'aria-sort': active ? (desc ? 'descending' : 'ascending') : 'none',
  }, [btn]);
}

function renderRows(def, rows) {
  const body = $('#dataBody');
  body.textContent = '';
  if (!rows.length) {
    body.appendChild(placeholderRow(def.cols.length, dataState.drill || dataState.q ? '无匹配数据' : '该表暂无数据'));
    return;
  }
  rows.forEach(r => body.appendChild(el('tr', {}, def.cols.map(c => renderCell(c, r)))));
}

/** 单单元格渲染:空值统一显示"—";flag/num 按列口径;带 link 的列渲染为下钻按钮;dsl 列内集合编号可钻。 */
function renderCell(c, row) {
  const raw = row[c.key];
  const cls = [c.mono ? 'mono' : '', c.dsl ? 'cell-dsl' : ''].filter(Boolean).join(' ') || null;
  const td = el('td', { class: cls });
  if (raw == null || raw === '') { td.classList.add('cell-muted'); td.textContent = '—'; return td; }

  if (c.dsl) {
    fillDslCell(td, raw);
    return td;
  }
  if (c.format === 'flag') {
    td.textContent = raw === '1' || raw === 'true' ? '是' : raw === '0' || raw === 'false' ? '否' : raw;
    return td;
  }
  if (c.format === 'num') {
    const n = Number(raw);
    td.classList.add('num');
    td.textContent = Number.isFinite(n) ? fmtInt(n) : raw;
    return td;
  }
  if (c.link) {
    td.appendChild(el('button', {
      type: 'button',
      class: 'cell-link',
      title: c.hint || `从${tableLabel(c.link.table)}查看 ${raw}`,
      text: raw,
      onclick: () => drillTo(c.link.table, c.link.col, raw),
    }));
    return td;
  }
  td.textContent = raw;
  return td;
}

/** 条件 DSL 单元格内容:默认折 2 行(见 style.css .dsl-text),仅超长条件给「展开/收起」开关。
    分页表格与分组主干树共用同一实现,两处的折行/展开行为不会各自漂移。 */
function fillDslCell(td, raw) {
  td.appendChild(el('span', { class: 'dsl-text' }, [dslNode(raw)]));
  if (raw.length <= DSL_CLAMP_CHARS) return;
  td.appendChild(el('button', {
    type: 'button', class: 'cell-link dsl-toggle', text: '展开',
    'aria-expanded': 'false',
    title: '展开完整入组条件',
  }));
}

/** 展开 / 收起一格入组条件。按钮随内容一起重建(表格换页、树详情重绘),故状态只写在 DOM 上。 */
function toggleDsl(btn) {
  const box = btn.closest('.cell-dsl');
  if (!box) return;
  const open = box.classList.toggle('is-open');
  btn.textContent = open ? '收起' : '展开';
  btn.setAttribute('aria-expanded', String(open));
  btn.title = open ? '收起到 2 行' : '展开完整入组条件';
}

/** DSL 表达式原样输出,内部出现的官方集合编号替换成下钻按钮(其余字符保持原样)。
    下钻落点是「编码集合」树里该集合的节点(成员清单在右栏按需加载),不再是集合成员平表。 */
function dslNode(text) {
  const frag = document.createDocumentFragment();
  const re = /[A-Za-z_][A-Za-z0-9_]*/g;
  let last = 0, m;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) frag.appendChild(document.createTextNode(text.slice(last, m.index)));
    const token = m[0];
    frag.appendChild(setIds.has(token)
      ? el('button', {
          type: 'button',
          class: 'cell-link dsl-set',
          title: `查看集合 ${token} 及其成员`,
          text: token,
          onclick: () => jumpToSet(token),
        })
      : document.createTextNode(token));
    last = m.index + token.length;
  }
  if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
  return frag;
}

function renderMeta() {
  const def = currentDef();
  const box = $('#dataMeta');
  if (!box) return;
  box.textContent = '';
  if (!def) return;
  box.appendChild(el('span', { class: 'data-meta-desc', text: def.desc || def.label }));
  if (def.kind === 'tree') {
    if (def.id === SET_TREE) {
      // 集合树:成员不在树里(右栏按需加载),故这里只说清"多少个集合、成员总量多少" ——
      // 成员总量取官方 member_count 合计,与右栏逐集合加载出来的条数同源。
      const types = (def.levels || [])[0]?.count || 0;
      const sets = (def.levels || [])[1]?.count || 0;
      box.appendChild(el('span', {
        class: 'cell-dim',
        text: `集合 ${fmtInt(sets)} 个 · 类型 ${fmtInt(types)} 类 · 成员 ${fmtInt(def.memberTotal || 0)} 条(选中集合后按需加载)`,
      }));
      return;
    }
    // 不写"来源 official/xxx.csv"(表名已在子标签上),也不写操作提示(界面上点一下就知道)——
    // 这里只留两件数不出来的事:各层条数(歧义组 XQY 与正常组分开标)与 00 类的剔除口径。
    const seg = l => {
      const n = l.count || 0;
      const qy = l.qyCount || 0;
      return qy > 0 ? `${l.tag} ${fmtInt(n - qy)} + QY ${fmtInt(qy)}` : `${l.tag} ${fmtInt(n)}`;
    };
    box.appendChild(el('span', {
      class: 'cell-dim',
      text: (def.levels || []).map(seg).join(' · ')
        + ` · 共 ${fmtInt((def.levels || []).reduce((s, l) => s + (l.count || 0), 0))} 个节点`
        + ` · 已剔除 00 类 ${fmtInt(def.zeroTotal || 0)} 条`,
    }));
    return;
  }
  box.appendChild(el('span', {
    class: 'cell-dim',
    text: `全表 ${fmtInt(def.total)} 行 · 全部 ${def.cols.length} 列`,
  }));
}

function renderDrillBar() {
  const bar = $('#dataDrillBar');
  const d = dataState.drill;
  bar.textContent = '';
  if (!d) { setHidden(bar, true); return; }
  setHidden(bar, false);
  if (drillStack.length) {
    bar.appendChild(el('button', {
      type: 'button', class: 'btn btn-ghost btn-sm', text: '← 返回',
      onclick: drillBack,
    }));
  }
  bar.appendChild(el('span', { class: 'drill-text' }, [
    el('b', { text: d.label }),
    document.createTextNode(' = ' + d.value),
  ]));
  bar.appendChild(el('span', { class: 'drill-spacer' }));
  bar.appendChild(el('button', { type: 'button', class: 'btn btn-ghost btn-sm', text: '清除筛选', onclick: clearDrill }));
}

export function renderPager() {
  const { total, offset, limit } = dataState;
  const page = Math.floor(offset / limit) + 1;
  $('#dataPage').textContent = total
    ? (dataState.drill || dataState.q ? `匹配 ${fmtInt(total)} 条 · 第 ${page} 页` : `共 ${fmtInt(total)} 条 · 第 ${page} 页`)
    : (dataState.drill || dataState.q ? '无匹配' : '共 0 条');
  $('#dataPrev').disabled = offset <= 0;
  $('#dataNext').disabled = offset + limit >= total;
  const jump = $('#dataJump');
  if (jump) {
    jump.max = String(dataPages());
    if (jump.value !== String(page)) jump.value = String(page);
  }
}

/** 切到某棵层级树并按层级定位到节点(结果卡与集合编号跳转的公共路径)。 */
async function gotoTreeTab(tabId, levelId, code) {
  clearSearch();
  drillStack.length = 0;
  setActiveTab(tabId);
  dataState.drill = null;
  dataState.offset = 0;
  renderMeta();
  renderDrillBar();
  showDataPane(true);
  $('#dataTreeList').textContent = '';
  $('#dataTreeDetail').textContent = '';
  const s = treeState(tabId);
  if (!code) { loadTree(); return; }        // 无落点:正常进树(含首次装配)
  await locateTreeNode(s, levelId, code);
}
export async function jumpToSet(setId) {
  const code = String(setId || '').trim();
  if (!code) return;
  switchTab('data');
  await ensureDataLoaded();
  if (tables.some(t => t.id === SET_TREE)) { await gotoTreeTab(SET_TREE, 'set', code); return; }
  if (tables.some(t => t.id === 'codesets')) drillTo('codesets', 'set_id', code);
}

