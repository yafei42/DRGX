// ---- 域间依赖 ----
// 只依赖叶子/单向模块:原先从 group.js 取 factWithNames/loadAdrgDetail,
// 而 group.js 又要 buildTimeline → 两者成环。两个符号已分别下沉到 dom/data。
import { jumpToSet } from './data.js';
import { adrgKindOf, api, el, factWithNames, isCondTrace, isGateTrace, isSubgroupTrace, mapChips, mapDisplayVisible, staleGuard } from './dom.js';

// ---- ADRG 明细请求与过期守卫(原 adrg.js,已内联:仅本模块使用) ----
/** ADRG 明细请求守卫:连续分组时过期响应不得盖到新视图。 */
const adrgDetailGuard = staleGuard();

/**
 * 拉取该 ADRG 的明细,现在只为本组补 MDC/ADRG 官方中文名(原「入组明细」行已按需求移除,
 * origin 官方原文仍可在数据一览「ADRG 核心组」表的 dsl 列与 zhRefs 链路中可见)。
 * seq 守卫防止连续分组时过期响应覆盖。
 * zhRefs:竖轴/横链两视图的 MDC/ADRG 中文名占位元素数组,同一响应顺带补齐,"码"升级为"码+名"。
 */
async function loadAdrgDetail(adrgCode, seq, zhRefs = null) {
  try {
    const r = await api.get(`/api/adrg/${encodeURIComponent(adrgCode)}`);
    if (!adrgDetailGuard.is(seq)) return; // 已有更新的分组,丢弃过期响应
    const h = r && r.hit;
    if (zhRefs && h) {
      // _mdc/_adrg 记录已取到的名称:懒构建的后建视图(buildTimeline.ensure)据此回放补齐占位符
      if (h.mdcName) { zhRefs._mdc = h.mdcName; zhRefs.mdcName.forEach(n => { n.textContent = h.mdcName; }); }
      if (h.name) { zhRefs._adrg = h.name; zhRefs.adrgName.forEach(n => { n.textContent = h.name; }); }
    }
  } catch (e) { /* 加载失败静默忽略,不影响主结果 */ }
}

/* ---- 路径即导航:MDC › ADRG › DRG 各段既是落位信息又是按钮(一物多用),
   点击在下方统一面板看该段判定依据;「依据 / 未通过条件 / 引擎日志」是
   “为什么这么分组”的三个粒度,收进同一面板,不再各自占一条折叠区。 ---- */

/** 时间轴共享模型:竖轴(叙事)与横链(紧凑)两个视图消费同一份判定数据。
    evidence:命中条件的「原文(expression) → 事实(detail)」成对材料,事实编码就地补中文名称;
    步骤标签用「序号 + 动作短语」(按主要诊断归类/确定核心组/细分组落位),还原一线的判定叙事。 */
export function timelineModel(status, o, trace, names) {
  // 轨迹谓词用模块级共享版(isCondTrace/isGateTrace/isSubgroupTrace),此处不再各持一份
  // 噪音事实(无信息量的计数型命中,如「存在 N 项其他诊断」)按条件原语名过滤——
  // 原语名来自 ConditionKind 枚举,协议稳定;不按文案匹配(文案允许演进)
  const isNoiseFact = s => /^hasOtherDiagnosis\b/.test(s.expression || '');
  const pairsOf = list => list.filter(s => s.detail && !isNoiseFact(s))
    .map(s => ({
      expr: s.expression || '',
      // 命中事实行自带「命中事实」标签,去掉「命中主要诊断/主要手术」前缀,只留编码+名称
      fact: factWithNames(s.detail || '', names, s.codes).replace(/^命中(?:主要诊断|其他诊断|主要手术) /, ''),
    }));
  const hits = trace.filter(s => isCondTrace(s) && s.result === true);
  const lastTone = status === 'Success' ? 'ok' : status === 'Ambiguous' ? 'warn' : 'err';

  // ---- 详细日志:把原始判定轨迹按帧切片,与「依据」刻意不同源 ----
  // 依据(evidence)只留"命中且非噪音"的行,回答"满足了什么";日志保留全部行(含未命中与容器行),
  // 回答"引擎逐条判了什么、哪条没过"。两者粒度不同,故各切一份,而不是共用一份再过滤。
  // 归属规则(单一判据,不另立一套路径解析):条件行按引擎下发的 scope,流程行(无 result)按 stage 兜底。
  //   scope = Gate                  → MDC 帧(进入系统)
  //   scope = DrgSplit              → DRG 帧(细分组落位)
  //   其余条件行(AdrgEntry 等)       → ADRG 帧(入组路径)
  //   stage = MDC / 入组 / 并发症    → 依次同上三帧
  //   stage = 校验 / 结果            → DRG 帧:性别缺失、主诊断不在字典等是判定链终点的"为什么",
  //                                   而断点态(未入组/歧义)的落点正是 DRG 帧,放这里才与「下一步」同一处可读
  //   stage = 映射                  → 不进任何帧:内容已由"编码标准化"帧的映射 chips 表达,再列一遍是重复
  // 谓词各自留一份(挂到 step.logFilter):「显示详细日志」会按需再拉一份 verbose 轨迹,
  // 那份必须用**同一套归属规则**重新切片,否则展开后内容会落到别的帧去。
  const isFlow = stage => s => !isCondTrace(s) && s.stage === stage;
  /** 狭义门控行:仅门控本体(路径以 .gate 结尾),不含其内部分支行。isGateTrace 是宽判
      (整棵子树都算门控,供帧归属用);证据列表只收胜出 MDC 的门控命中行 —— 落选 MDC 分支的
      "半命中"叶(如主诊断命中但其他诊断未中)result=true,不能当本帧证据。 */
  const isGateHead = s => String(s.conditionPath || '').endsWith('.gate');
  const predMdc = s => (isCondTrace(s) && isGateTrace(s)) || isFlow('MDC')(s);
  const predAdrg = s => (isCondTrace(s) && !isGateTrace(s) && !isSubgroupTrace(s)) || isFlow('入组')(s);
  const predDrg = s => (isCondTrace(s) && isSubgroupTrace(s) && !isGateTrace(s))
    || isFlow('并发症')(s) || isFlow('校验')(s) || isFlow('结果')(s);
  const logMdc = trace.filter(predMdc), logAdrg = trace.filter(predAdrg), logDrg = trace.filter(predDrg);

  const steps = [];
  // 编码映射是分组链的起点:有映射时作为时间轴第一帧,叙事连贯;无映射时不占位
  const mappings = mapDisplayVisible() ? (o.mappings || []) : [];
  if (mappings.length) steps.push({ key: 'maps', tag: '', label: '编码标准化', tone: 'ok', maps: mappings });
  if (o.mdc) steps.push({
    key: 'mdc', tag: 'MDC', code: o.mdc, label: '按主要诊断归类', tone: 'ok',
    evidence: pairsOf(hits.filter(isGateHead)),
    log: logMdc, logFilter: predMdc,
  });
  if (o.adrg) steps.push({
    key: 'adrg', tag: 'ADRG', code: o.adrg, tone: 'ok', adrg: true,
    // log 挂在 ADRG 帧上而不是 adrgStep 事后补:evidence 会被下面的 origin 行覆盖重写,
    // log 不该跟着一起被重写(它是切片好的原始轨迹,与 origin 文案无关)
    log: logAdrg, logFilter: predAdrg,
    // 命中角色由后端结构化下发(trace[].role):主诊断/主手术编码直接取,不再正则抠 detail;
    // 提取不到时回退自然语言 adrgReason,不空转
    adrgDx: (hits.find(s => s.role === 'mainDiagnosis') || {}).codes?.[0] || null,
    adrgProc: (hits.find(s => s.role === 'mainProcedure') || {}).codes?.[0] || null,
    // adrgOrigin = rules 内该 ADRG 的 origin(官方入组条件原文),「条件内容」行首选它;
    // adrgReason 为兼容回退(旧响应/未命中明细时非空)
    adrgOrigin: o.adrgOrigin || null,
    adrgReason: o.adrgReason || null,
  });
  const adrgStep = steps.find(s => s.adrg);
  // 兜底病例(如全局兜底行 0000)没有 MDC/ADRG 落位码,o.mdc / o.adrg 为空,上面两个帧都不建;
  // 但引擎在这两层的逐条判定轨迹仍在(哪几个 MDC 没进、哪些核心组没中、为什么) —— 不给这些
  // 行一个帧,「为什么连 MDC 都没进」就无处可看,日志被整段丢掉。补两个只承载日志的帧,
  // 码位用「—」占位,标题直接说未命中,不打假落位。
  if (!o.mdc && logMdc.length) steps.push({
    key: 'mdc', tag: 'MDC', code: '—', tone: 'err',
    label: '按主要诊断归类（未命中）',
    evidence: pairsOf(hits.filter(isGateHead)),
    log: logMdc, logFilter: predMdc,
  });
  if (!o.adrg && logAdrg.length) steps.push({
    key: 'adrg', tag: 'ADRG', code: '—', tone: 'err',
    label: '确定核心组（未命中）',
    log: logAdrg, logFilter: predAdrg,
  });
  if (adrgStep) {
    adrgStep.label = `确定核心组(${adrgKindOf(o.adrg) || '核心组'}，${adrgStep.adrgProc ? '有手术' : '无手术'})`;
    adrgStep.evidence = [{
      expr: adrgStep.adrgOrigin || adrgStep.adrgReason || `入组路径 ${o.adrg}`,
      fact: `诊断 ${adrgStep.adrgDx || '—'} · 手术 ${adrgStep.adrgProc || '无'}`,
    }];
  }
  const mccCount = (o.majorComplications || []).length, ccCount = (o.minorComplications || []).length;
  // drgOrigin = rules 内 split.origin(合并症等级/年龄属性/特殊入组条件三列综合),「条件内容」首选
  const drgOrigin = o.drgOrigin || null;
  // drgDirect = 直赋档说明(高危妊娠直赋/机器人直赋):落位由特殊身份条件决定,与合并症分档无关,
  // 段标题优先于 MCC/CC 计数推断(直赋档 0 MCC/0 CC 时按计数会误标"不伴并发症")。
  // 高危妊娠由后端按主诊断命中 811 清单判定,有 MCC 也照样下发——MCC 只是同一 DRG 的另一条落位路径。
  const drgDirect = o.drgDirect || null;
  const drgPairs = pairsOf(hits.filter(isSubgroupTrace));
  const drgStep = {
    key: 'drg', tag: 'DRG', code: o.code || '—',
    label: status === 'Success'
      ? `细分组落位(${drgDirect || (mccCount > 0 ? '伴严重并发症' : ccCount > 0 ? '伴并发症' : '不伴并发症')})`
      : status === 'Ambiguous' ? '歧义病案 · 需人工复核' : '未能入组',
    tone: lastTone,
    evidence: drgPairs,
    log: logDrg, logFilter: predDrg,
  };
  // 命中明细保留(编码+名称),条件内容行优先展示官方 origin
  if (drgOrigin && drgStep.evidence.length) drgStep.evidence[0] = { expr: drgOrigin, fact: drgStep.evidence[0].fact };
  // 不伴并发症等负向命中:内层条件未命中被过滤,补一行总结事实,证据链不空转
  if (status === 'Success' && !drgStep.evidence.length) drgStep.evidence = [
    { expr: drgOrigin || `细分组 ${o.code}`, fact: '满足该细分组全部条件' },
  ];
  steps.push(drgStep);

  // ---- 每帧「判定结论」(依据展开区首行) ----
  // 只做一件事:把「判定依据」折叠区里最有信息量的一句提炼到展开区首行,点开依据第一眼
  // 先读到"为什么是这个 MDC / 这个 ADRG / 这个 DRG"的人话结论,再看规则原文与命中事实。
  // 全部由后端结构化事实拼装(门控命中码 / 入组命中角色 / 档位命中明细,均带 role/codes 下发),
  // 不在前端另写 DSL 词典;判定依据与详细日志两层各答各的,不重复(say it once)。
  const nameOf = c => c && names[c] ? `（${names[c]}）` : '';
  const stripHit = t => factWithNames(t.detail || '', names, t.codes)
    .replace(/^命中(?:主要诊断|其他诊断|主要手术) /, '');
  const mdcStep = steps.find(s => s.key === 'mdc');
  if (mdcStep) {
    const ghit = hits.find(s => isGateHead(s)
      && (s.role === 'mainDiagnosis' || s.role === 'mainProcedure') && (s.codes || []).length);
    if (ghit) {
      const hitFact = stripHit(ghit);
      const extras = (mdcStep.evidence || []).filter(p => p.fact !== hitFact).map(p => p.fact);
      // MDCA 等先期分组由主手术驱动;其余 MDC 由主诊断归类 —— 与官方分组叙事同口径
      mdcStep.why = ghit.role === 'mainProcedure'
        ? `主手术 ${hitFact} 命中 ${o.mdc} 先期分组`
        : `主诊断 ${hitFact} 属于 ${o.mdc} 的收治范围`;
      if (extras.length) mdcStep.why += `；另满足 ${extras.join('、')}`;
    } else {
      const facts = (mdcStep.evidence || []).map(p => p.fact);
      mdcStep.why = facts.length ? `满足 ${o.mdc} 的入组条件：${facts.join('；')}` : '';
    }
  }
  if (adrgStep) {
    const heads = [];
    if (adrgStep.adrgDx) heads.push(`主诊断 ${adrgStep.adrgDx}${nameOf(adrgStep.adrgDx)}`);
    if (adrgStep.adrgProc) heads.push(`主手术 ${adrgStep.adrgProc}${nameOf(adrgStep.adrgProc)}`);
    // 命中角色提取不到时留空:依据区的官方 origin 已覆盖,不为凑一行而重复
    adrgStep.why = heads.length ? `${heads.join(' + ')} 经入组路径进入核心组 ${o.adrg}` : '';
  }
  if (status === 'Success') drgStep.why = drgDirect
    ? `因「${drgDirect}」直接落位 ${o.code}`
    : drgPairs.length
      ? `档位判定：${drgPairs.map(p => p.fact).join('；')} → 落入 ${o.code}`
      : `满足 ${o.code} 的全部落位条件`;

  // ---- L4 断点态的「下一步」:由结构化原因与事实生成可执行建议 ----
  // 一线在断点态要的不是"哪条没过",而是"改什么"。三类来源全是结构化字段
  // (reason 枚举 / excludedComplications / 候选缺口),不复述原因文案。
  const next = [];
  if (status !== 'Success') {
    if (status === 'Ambiguous') next.push('核对主手术与主要诊断是否相关：手术编码或主诊断选择需人工复核后重提');
    else if (o.reason === 'MainDiagnosisNotGroupable') next.push('该主要诊断在官方「不作为分组规则」清单内，需更换主要诊断或申请特例评审');
    else if (o.reason === 'MainDiagnosisUnrecognized') next.push('核对主要诊断编码是否存在：院内码或已停用码无法参与分组');
    else if (o.reason === 'NoMdcMatched') next.push('核对主要诊断是否属于某个 MDC 的收治范围；若确有手术，确认主要手术编码在有效手术操作清单内');
    else if (o.reason === 'NoSubgroupMatched') next.push('已通过分类门控但无核心组命中：核对主要诊断与主要手术是否配套（可能属歧义病案）');
    else if (o.reasonText) next.push(o.reasonText);
  }
  // 排除表命中是"看着差一项、其实被吞掉"的头号原因:只给码才有可操作性,泛泛说"缺并发症"没用
  const excluded = o.excludedComplications || [];
  if (status !== 'Success' && excluded.length) {
    const shown = excluded.slice(0, 3).map(e => e.code).join('、');
    next.unshift(`本例 ${excluded.length} 项其他诊断与主要诊断同排除组、已剔除不计入并发症分档`
      + `（${shown}${excluded.length > 3 ? ' 等' : ''}），复核该诊断编码或用更特异的主要诊断`);
  }
  // 「最接近的候选」:按候选码归组条件行(owner 由引擎显式下发),取缺口最小者。
  // 默认轨迹只带命中路径、不含落选候选的条件行,此时为空 —— 不硬凑一句无依据的话。
  const byOwner = new Map();
  [...logAdrg, ...logDrg].forEach(s => {
    if (!isCondTrace(s) || !s.owner) return;
    if (!byOwner.has(s.owner)) byOwner.set(s.owner, []);
    byOwner.get(s.owner).push(s);
  });
  const gaps = [];
  byOwner.forEach((conds, code) => {
    const a = unitAnalysis(conds);
    if (a && a.total >= 2 && a.gap >= 1) gaps.push({ code, gap: a.gap, passed: a.passed, total: a.total });
  });
  if (status !== 'Success' && gaps.length) {
    const min = Math.min(...gaps.map(g => g.gap));
    const top = gaps.filter(g => g.gap === min).slice(0, 3);
    next.push(`最接近：${top.map(g => `${g.code} 差 ${g.gap} 项（已满足 ${g.passed}/${g.total}）`).join('，')}`);
  }

  // 序号发纯数字,圆环由 CSS 自绘(.tl-idx)。原先发 ①②③④ 圈码字符,而 --mono 的
  // IBM Plex Mono 没有这些字形 → 回退到系统字体:实测字宽只有 6.6px(全行最弱的元素
  // 却在承担「第几步」),且形状/宽度跨平台不一致。自绘后字号、字重、环径都由我们定。
  steps.forEach((s, i) => { s.idx = String(i + 1); });
  return { steps, hits, lastTone, next };
}

export function buildTimeline(status, o, trace, names, loadTrace) {
  // loadTrace:按需拉 verbose 轨迹的函数(见 group.js)。默认轨迹只保留命中路径与流程结论,
  // 「显示详细日志」展开时才补拉逐条未命中,所以两个视图都要把它透传到各自的 eviBlock。
  // 单病例视图:竖轴(逐步叙事)与横链(紧凑一屏)可切换,偏好记忆在 localStorage;
  // 两视图消费同一份模型,共享 zhRefs:MDC/ADRG 官方中文名一次请求同时补齐两视图。
  // 懒构建:用户通常只用一个视图,首次切到某视图才构造 DOM 并缓存,结果卡渲染成本减半
  const m = timelineModel(status, o, trace, names);
  const zhRefs = { mdcName: [], adrgName: [] };
  const acts = el('span', { class: 'rc-path-acts' });

  const shell = el('div', { class: 'rc-timeline-shell' });
  const sw = el('div', { class: 'tl-view-switch', role: 'group', 'aria-label': '时间轴视图切换' });
  const bAxis = el('button', { type: 'button', text: '竖轴', title: '竖向时间轴，逐步展示判定过程' });
  const bChain = el('button', { type: 'button', text: '横链', title: '紧凑横链，点击段查看判定依据' });
  const views = {};
  const meta = { el: shell, acts, zhRefs };
  // MDC/ADRG 官方中文名一次请求补齐两视图(响应只用于名称回填,不再渲染入组明细行)
  if (status === 'Success' && o.adrg) loadAdrgDetail(o.adrg, adrgDetailGuard.next(), zhRefs);
  const ensure = v => {
    if (views[v]) return views[v];
    views[v] = v === 'axis' ? buildAxisView(m, status, o, zhRefs, loadTrace) : buildChainView(m, status, o, zhRefs, loadTrace);
    // 若中文名响应先于本视图构建返回,回放补齐,避免后建视图只剩编码;
    // 反过来(响应晚于构建)则由 loadAdrgDetail 内的 forEach 直接覆盖,两个方向都闭环
    if (zhRefs._mdc) zhRefs.mdcName.forEach(n => { n.textContent = zhRefs._mdc; });
    if (zhRefs._adrg) zhRefs.adrgName.forEach(n => { n.textContent = zhRefs._adrg; });
    shell.append(views[v].el);
    return views[v];
  };
  const apply = v => {
    bAxis.classList.toggle('is-active', v === 'axis');
    bChain.classList.toggle('is-active', v === 'chain');
    ensure(v);
    if (views.axis) views.axis.el.hidden = v !== 'axis';
    if (views.chain) views.chain.el.hidden = v !== 'chain';
    try { localStorage.setItem('drg.timeline.view', v); } catch { /* localStorage 不可用 */ }
  };
  bAxis.addEventListener('click', () => apply('axis'));
  bChain.addEventListener('click', () => apply('chain'));
  sw.append(bAxis, bChain);
  // 头部行先落位(切换器居左,动作按钮如"换主诊断模拟"靠右),视图内容后追加——保证切换器始终在时间轴上方
  const head = el('div', { class: 'tl-shell-head' });
  head.append(sw, acts);
  shell.append(head);
  let pref = 'axis';
  try { if (localStorage.getItem('drg.timeline.view') === 'chain') pref = 'chain'; } catch { /* localStorage 不可用 */ }
  apply(pref);
  return meta;
}

/** 竖轴视图:序号动作短语(② 按主要诊断归类)+ 编码中文名 + 「原文 → 事实」证据链行,
    逐步还原判定过程;官方明细与规则抽屉挂在节点下,按需展开。 */
export function buildAxisView(m, status, o, zhRefs, loadTrace) {
  const wrap = el('div', { class: 'rc-timeline' });
  m.steps.forEach(s => {
    // 节点带层级 key 类(tl-node-mdc / -adrg / -drg):三级的字号、字重、圆点形状由它分级。
    // 结构上不能是三个等权重节点 —— 否则 MDC 大类、ADRG 核心组、DRG 细分组读起来一样重。
    // tone 上移到节点:.tl-idx 既是序号也是轨道站点灯(原先序号与 .tl-dot 两个圆各干一半,
    // 一条三步链上并排两个圆),状态色因此按节点选择,不再靠 dot 的兄弟选择器往下传。
    const node = el('div', { class: 'tl-node' + (s.key ? ' tl-node-' + s.key : '') + ' tone-' + s.tone });
    const idx = el('span', { class: 'tl-idx', text: s.idx });
    const body = el('div', { class: 'tl-body' });
    // 步骤头:层级芯片(MDC/ADRG/DRG) + 动作短语 + 依据入口,随内容自然流动。
    // 曾把层级标签钉在 4ch 定宽格里来对齐三帧的依据入口,代价是格内留白随层级长短变化、
    // 入口被 208px 列钉死;改芯片后头部紧凑,入口在三帧间不再同 x —— 接受这次交换。
    const head = el('div', { class: 'tl-head' }, [
      el('span', { class: 'tl-tag', text: s.tag || '' }),
      el('span', { class: 'tl-label', text: s.label }),
    ]);
    body.appendChild(head);
    if (s.maps) {
      // 编码标准化节点:映射箭头 chips,不显示编码
      body.appendChild(mapChips(s.maps));
    } else {
      // 编码 + 中文名:MDC/ADRG 官方名随明细异步补齐(zhRefs 注册占位);DRG 组名响应里就有,
      // 同步直填 —— 用户复核:只报编码不报名,读轴的人还得抬头看结果卡才能知道落在哪个组,
      // 「码+名」在一行内自洽。组名与结果卡 .rc-name 同屏两处是可接受代价(轴是过程叙述,卡是结论)。
      const titleRow = el('div', { class: 'tl-title' }, [el('span', { class: 'tl-code', text: s.code })]);
      if (s.key === 'mdc' || s.key === 'adrg') {
        const zh = el('span', { class: 'tl-zh' });
        (s.key === 'mdc' ? zhRefs.mdcName : zhRefs.adrgName).push(zh);
        titleRow.appendChild(zh);
      } else if (s.key === 'drg' && o.group && o.group.name) {
        titleRow.appendChild(el('span', { class: 'tl-zh', text: o.group.name }));
      }
      body.appendChild(titleRow);
      // 「落位」芯片:成功态 DRG 帧在编码旁点一枚状态章 —— 与实心站点灯同一语义(到站),
      // 全轴只此一枚;歧义/断点态不点,状态由 tone 色与「下一步」行表达。
      if (s.key === 'drg' && s.tone === 'ok') titleRow.appendChild(el('span', { class: 'tl-land', text: '落位' }));
      // 判定结论平铺在编码下:不点开依据就能读到"为什么入这一级"(后端结构化事实拼装的 s.why)。
      // 依据展开区不再重复这句 —— 同一句话只出现一次(横链视图仍在展开区首行读它)。
      if (s.why) body.appendChild(el('div', { class: 'tl-why', text: s.why }));
      // 入口紧跟步骤头(展开/收起都不挪动位置)。原先 margin-left:auto 顶到结果行最右,
      // 结果列固定 821px 时与内容末端空出 424~615px —— 三步判定只占左侧 30%,
      // 入口孤悬在右半边,与它要展开的内容隔着大半屏。
      // 依据为空但有日志(门控未产生命中事实的帧)时同样给入口:否则该帧的判定过程无处可看
      // 判定结论已平铺到标题行下,展开区直接进规则原文与命中事实,不再重复首行
      if ((s.evidence && s.evidence.length) || (s.log && s.log.length)) {
        const evi = eviBlock(s.evidence, { log: s.log, logFilter: s.logFilter, loadTrace });
        head.appendChild(evi.btn);
        body.appendChild(evi.rows);
      }
      // 断点态「下一步」:由结构化原因与事实生成可执行建议,一线拿到未入组先知道改什么
      if (s.key === 'drg' && m.next.length) {
        const box = el('div', { class: 'ev-next-box' });
        m.next.forEach(t => box.appendChild(el('div', { class: 'ev-next-line', text: t })));
        body.appendChild(el('div', { class: 'ev-rows' }, [el('div', { class: 'ev-row ev-next' }, [
          el('span', { class: 'ev-k', text: '下一步' }),
          box,
        ])]));
      }
    }
    node.append(idx, body);
    wrap.appendChild(node);
  });
  return { el: wrap };
}

/** 官方规则抽屉:MDC 展示门控条件树,ADRG 展示入组路径+细分组;竖轴/横链共用,toggle 时懒加载。 */
/** 判定依据:入口按钮固定在结果行最右(展开/收起都不移位),内容作为兄弟节点挂在结果行下方,
    展开时不再重复出现入口文案。
    opts.open = 初始展开:横链的段下面板本身就是"按需消费"(点段的动机就是看依据),进去了还要再点
    一次「判定依据」是多余的一步;竖轴三帧各自平行铺陈,仍保持收起。
    opts.conclusion = 判定结论(一句话白话,timelineModel 由后端结构化事实拼装):展开区首行,
    点开依据第一眼先读到"为什么入这一级"的人话结论,再看下面的规则原文与命中事实。
    opts.log = 该帧的引擎逐条日志(原始轨迹切片):作为依据区内的第二层入口,见下方注释。 */
export function eviBlock(list, opts = {}) {
  const log = opts.log || [];
  const canDeepen = !!(opts.loadTrace && opts.logFilter);
  const open0 = !!opts.open;
  const rows = el('div', { class: 'ev-rows evi-rows', hidden: !open0 }, [
    opts.conclusion ? el('div', { class: 'ev-row ev-concl' }, [
      el('span', { class: 'ev-k', text: '判定结论' }),
      el('span', { class: 'ev-fact ev-concl-text', text: opts.conclusion }),
    ]) : null,
    ...evRows(list),
  ].filter(Boolean));
  const btn = el('button', {
    type: 'button', class: 'tl-evi-btn' + (open0 ? ' is-open' : ''), 'aria-expanded': String(open0),
    title: '查看该步判定依据（条件内容 / 命中事实）',
    text: '判定依据',
  });
  // 详细日志入口落在依据展开区「内部」而非结果行上:依据回答"满足了什么",日志回答"引擎逐条判了什么、
  // 哪条没过"——只有先看过依据才有追问的动机,故收成第二层;且随依据区一起收起,
  // 不会出现"依据已收起、日志还挂在下面"的孤儿状态。
  if (log.length || canDeepen) {
    // 一律默认收起:日志是"点开才看"的第二层。曾试过"依据区没有别的行时直接铺开",
    // 结果在未入组病例上把可操作的「下一步」提示挤到了日志下面 —— 断点态最该先看到的
    // 是"改什么",不是"引擎逐条判了什么"。多一次点击换首屏秩序,值。
    const logBox = el('div', { class: 'evi-log', hidden: true });
    let logList = log, deepened = false;
    const paint = () => { logBox.innerHTML = ''; logBox.appendChild(evLog(logList)); };
    paint();
    const logBtn = el('button', {
      type: 'button', class: 'tl-evi-log-btn', 'aria-expanded': 'false',
      title: '显示该步的引擎逐条判定日志（含未通过的条件与流程行）',
      text: '显示详细日志',
    });
    logBtn.addEventListener('click', async () => {
      const open = logBox.hidden;
      // 首次展开时才补拉 verbose 轨迹:默认分组不带它(轨迹从 16 行长到 140+ 行),但只留命中路径的
      // "详细日志"名不副实。拉取失败就安静退回默认轨迹 —— 该帧仍有可读的结论行,不该报错。
      if (open && !deepened && canDeepen) {
        deepened = true;
        logBtn.disabled = true; logBtn.textContent = '加载详细日志…';
        const full = await opts.loadTrace();
        logBtn.disabled = false;
        if (full && full.length) { logList = full.filter(opts.logFilter); paint(); }
      }
      logBox.hidden = !open;
      logBtn.setAttribute('aria-expanded', String(open));
      logBtn.classList.toggle('is-open', open);
      logBtn.textContent = open ? '收起详细日志' : '显示详细日志';
    });
    rows.append(logBtn, logBox);
  }
  btn.addEventListener('click', () => {
    const open = rows.hidden;
    rows.hidden = !open;
    btn.setAttribute('aria-expanded', String(open));
    btn.classList.toggle('is-open', open);
  });
  return { btn, rows };
}

/** 详细日志:按「结论行 + 明细」块化还原引擎判定序列。
    - 流程行是判定单元的结论,紧随其后的条件行是该单元的明细;
    - 连续的「MDCx 未匹配到 YY1」按(所属 MDC + 主要失败原因)合并成一条:引擎逐个候选判一遍,
      一个 MDC 就能产出 40 行同因重复(全句只有码不同),平铺后真正的结论被淹掉;合并后
      "40 个核心组未命中(主要诊断不在码表内)"一句话就是答案,要逐个看再展开明细;
    - 容器条件(同时满足 N 项)保留:它标出层级,是明细树的结构,不是噪音。
    行内渲染复用 renderTraceStep(徽章/位置标签/条件短语/✓✗/事实),不另写一套排版:
    本处与路径面板「引擎日志」看起来必须是一套东西。verbose 与默认两套轨迹共用本函数。 */
export function evLog(list) {
  const box = el('div', { class: 'trace evi-log-body' });
  // 摘要行:先给体量预期再铺明细 —— 一条日志几十行,顶部一句"共 N 条:命中 X · 未通过 Y"
  // 让用户展开瞬间知道要看多少、大致结果,再决定逐条读还是只扫未通过
  const conds = (list || []).filter(isCondTrace);
  const ok = conds.filter(s => s.result === true).length;
  const miss = conds.length - ok;
  const flows = (list || []).length - conds.length;
  box.appendChild(el('div', { class: 'evi-log-sum' }, [
    el('span', { class: 'ev-k', text: '日志摘要' }),
    el('span', { class: 'ev-fact' }, `共 ${list.length} 条：命中 ${ok} · 未通过 ${miss}`
      + (flows ? ` · 过程 ${flows}` : '')),
    // 导读:用户第一次见到一列灰色 ✗ 行最容易问"这是出错了吗" —— 先答这一句
    miss ? el('span', { class: 'evi-log-hint', text: '灰色 = 未走通的候选，非报错；点灰色标题展开明细' }) : null,
  ].filter(Boolean)));
  const blocks = logBlocks(list);
  // 「距入组最近」:全部落选候选里缺口最小的几条 —— 编码员改一码就可能进组的候选,
  // 比逐块翻明细优先级高得多,一句话放在明细块之前当全局坐标
  const near = nearestSummary(blocks);
  if (near) box.appendChild(el('div', { class: 'evi-log-near' }, [
    el('span', { class: 'ev-k', text: '距入组最近' }),
    el('span', { class: 'ev-fact', text: near }),
  ]));
  blocks.forEach(b => box.appendChild(renderLogBlock(b)));
  return box;
}

/** 恒真门控:必然通过、不携带任何条件,显示出来只是白占一行。
    (容器行不在此列 —— 见 evLog 注释) */
const isNoInfoCond = s => isCondTrace(s) && s.nodeKind === 'True';

// ---- 明细压缩:同因重复行是"详细日志像报错墙"的最大来源 ----
// 引擎对每个候选组各判一遍,同一句"主手术 79.1500 不在码表内"能连排十几行,只有组码不同;
// 压缩规则:连续同 detail 的叶子行抽成一行「组码芯片 + 共同原因」,容器行(无 detail,是层级
// 结构)原样保留。组码优先取引擎下发的 owner(门控行除外 —— 其 owner 是 MDC 码而非组码,
// 由 condCodeOf 的字符串入参模式绕开),身份信息不丢,只是不再逐行抄同一句话。
/** 条件行归属的候选码:引擎已随行下发 owner(调用点直接标注),优先读它;
    旧响应回退到路径抠码(splits 优先于 adrgs —— 细分组带的才是落位码)。
    入参可为轨迹对象,也可为路径字符串(兼容既有调用点)。 */
const condCodeOf = s => {
  if (s && typeof s === 'object') {
    if (s.owner) return s.owner;
    s = s.conditionPath || '';
  }
  const p = String(s || '');
  const m = p.match(/splits\[([^\]]+)\]/) || p.match(/adrgs\[([^\]]+)\]/);
  return m ? m[1] : null;
};
function blockUnitCode(b, u, i) {
  if (b.type === 'gate') {
    const h = (b.heads || [])[i] || {};
    // 引擎已把 MDC 码结构化下发(head.code / head.args.mdc)
    const c = h.code || (h.args && h.args.mdc);
    return c ? 'MDC' + c : null;
  }
  return unitCodeOf(u);
}

/** 展示层措辞整理(不动后端原文):① 码表大小注记是引擎对账用的,对医生是纯噪音,每行都出现
    还吃掉一半行宽 —— 裁掉;② 容器条件的短语("同时满足以下 2 项条件/满足以下任一条件")脱离
    树结构读不懂,换成自然的指令式说法。只重写这几条固定短语、不做 DSL 解析(白话化的正确路径
    仍是后端 DescribeTree 短语表,此处是渲染层整形,单一渲染路径不会与引擎日志分叉)。 */
const friendlyExpr = t => {
  let s = String(t).replace(/（共 [\d,]+ 个编码）/g, '');
  s = s.replace(/^同时满足以下 (\d+) 项条件$/, '需同时满足以下 $1 项');
  s = s.replace(/^满足以下任一条件$/, '满足以下任一分支即可');
  s = s.replace(/^需满足任一$/, '满足以下任一分支即可');
  return s;
};

/** 条件行(叶子):未命中降调 + 按树深缩进;父容器是"同时满足 N 项"时子项带 ①② 序号,
    让"以下 N 项"与缩进在下的行一一对上。num = 在兄弟中的序号(1 起),0/超表不带序号。 */
function condRow(c, depth, num) {
  const n = num && num <= CIRCLED.length ? CIRCLED[num - 1] + ' ' : '';
  return el('div', {
    class: 'trow' + (c.result === false ? ' trow-miss' : ''),
    style: depth ? `margin-left:${depth * 14}px` : null,
  }, [renderTraceStep(c, n)]);
}

/** 组码芯片行:N 个组共享同一句原因时,芯片列码、后面跟原因;原因是码表原因时
    再补集合链接(原因散文本身不带编号 —— 编号随轨迹行下发,压缩时由 _leaf.setRef 带过来)。
    depth = 子项在树中的深度,容器内的芯片继承缩进,不浮到块顶。 */
function chipRow(codes, detail, depth, setRefs) {
  return el('div', { class: 'trow log-why', style: depth ? `margin-left:${depth * 14}px` : null }, [
    el('span', { class: 'log-codes' }, codes.map(c => el('span', { class: 'log-code mono', text: c }))),
    detail ? el('span', { class: 'tdetail', text: detail }) : null,
    ...setLinkEls(setRefs),
  ].filter(Boolean));
}

// ---- 条件树还原 + 明细压缩 ----
// 引擎按后序发条件轨迹:子条件在前,承载组合语义的容器行(「需同时满足以下 2 项」「满足任一」)
// 在其全部子项发完之后才出现。平铺照抄的话,「以下 2 项」指的其实是它上面两行 —— 用户无论
// 怎么读都会困惑。展示层必须还原成「容器在前、子项缩进在后」的树:依据 conditionPath 前缀
// 包含(子路径 = 父路径 + '.xxx')与后序保证(父行出现时其子树已完整),一次扫描即可挂树。
// 同因重复仍压成芯片行(这是"详细日志像报错墙"的最大来源),但只压"同因、无结构"的叶子串,
// 不再为压缩牺牲树形。

/** 容器条件:无命中事实、只承载组合语义(同时满足/任一/取反…)的行。
    引擎随每行下发 nodeKind(ConditionKind 名,冒烟实测 100% 覆盖),直接读它。 */
const CONTAINER_KINDS = ['All', 'Any', 'Not'];
const isContainerCond = s => isCondTrace(s) && CONTAINER_KINDS.includes(s.nodeKind);

const CIRCLED = ['①', '②', '③', '④', '⑤', '⑥', '⑦', '⑧', '⑨', '⑩', '⑪', '⑫'];

/** 后序条件行序列 → 树。pending 是"已建成、等父挂载"的子树;容器行出现时,从尾部把路径以
    它为前缀的连续子树收为直接子项(孙已被各自父收走;单元之间靠 adrgs[x]/MDCx 前缀区分,
    不会跨单元误收)。total 记原始子项数,剪枝后进度注记的分母仍是它。 */
function condTree(conds) {
  const pending = [];
  (conds || []).forEach(s => {
    if (!isCondTrace(s)) return;
    const node = { s, kids: [], total: 0 };
    const p = s.conditionPath || '';
    let i = pending.length;
    while (i > 0 && (pending[i - 1].s.conditionPath || '').startsWith(p + '.')) i--;
    node.kids = pending.splice(i);
    node.total = node.kids.length;
    pending.push(node);
  });
  return { roots: pending };
}

/** 剪掉与块标题同因、又提不出组码的叶子(标题已给过答案);子项被剪空的容器一并剪掉 ——
    只剩结构没有内容的容器行是纯噪音。返回 true = 本节点整体可剪。 */
function pruneNode(node, suppressDetail) {
  if (isContainerCond(node.s)) {
    node.kids = node.kids.filter(k => !pruneNode(k, suppressDetail));
    return !node.kids.length;
  }
  return !!suppressDetail && !condCodeOf(node.s)
    && node.s.detail === suppressDetail;
}

/** 容器行(结构小标题):子项带序号之外,不通过时再给进度注记「已满足 x/N」,把"差在哪、
    差多少"直接给出来;通过的容器子项全是 ✓,不再注记。分母/已满足数由引擎下发
    (AND 首败即停,后面的项没参与判定、没有轨迹行,数实际子项分母会对不上)。 */
function contRow(node, depth) {
  const s = node.s;
  let progress = '';
  if (s.result === false && (s.nodeKind === 'All' || s.nodeKind === 'Any')) {
    const total = s.total != null ? s.total : node.total;
    if (total) {
      const pass = s.passed != null ? s.passed : node.kids.filter(k => k.s.result === true).length;
      progress = s.nodeKind === 'All'
        ? `（已满足 ${pass}/${total}）`
        : `（${pass}/${total} 个分支满足）`;
      // 短路跳过、从未求值的子项:与"已判定但未通过"是两种语义,必须点出来
      if (s.unjudged > 0) progress += `；其中 ${s.unjudged} 项未参与判定`;
    }
  }
  const row = el('div', {
    class: 'trow trow-cont' + (s.result === false ? ' trow-miss' : ''),
    style: depth ? `margin-left:${depth * 14}px` : null,
  });
  const stepEl = renderTraceStep(s);
  if (progress) {
    const mark = stepEl.querySelector('.t-yes,.t-no');
    const span = el('span', { class: 'tdetail', text: progress });
    if (mark) mark.after(span); else stepEl.appendChild(span);
  }
  row.appendChild(stepEl);
  return row;
}

/** 树 → 行序(容器在前、子项缩进在后)。叶子行带 _leaf 标记(含父容器引用与深度),
    交给 mergeChipRuns 做同父归组。 */
function treeRows(node, depth, num, out, parent) {
  if (isContainerCond(node.s)) {
    out.push(contRow(node, depth));
    node.kids.forEach((k, i) => treeRows(k, depth + 1, i + 1, out, node));
    return;
  }
  const row = condRow(node.s, depth, num);
  row._leaf = {
    detail: node.s.detail || '',
    code: condCodeOf(node.s),
    setRef: node.s.setRef || null,
    parent: parent || null,
    depth,
  };
  out.push(row);
}

/** 同父同因叶子串 → 芯片行。合并范围限同一父容器(或同为根级):跨容器抽走子项会留下
    "0/1"空壳容器,树形也就散了;根级叶子没有容器约束,跨单元合并(合并块几十个组码
    压一行)靠它。容器行/异因行/换父打断归组。只剩一行的"串"保留原样(序号、缩进、✓✗
    都在),不为合并牺牲结构。 */
function mergeChipRuns(rows) {
  const out = [];
  let run = null;
  const flush = () => {
    if (!run) return;
    if (run.rows.length === 1) { out.push(run.rows[0]); }
    else {
      const codes = [], setRefs = [];
      run.rows.forEach(r => {
        const c = r._leaf.code; if (c && !codes.includes(c)) codes.push(c);
        const sr = r._leaf.setRef; if (sr && !setRefs.includes(sr)) setRefs.push(sr);
      });
      out.push(chipRow(codes, run.detail, run.rows[0]._leaf.depth,
        isSetReason(run.detail) ? setRefs : []));
    }
    run = null;
  };
  rows.forEach(r => {
    const leaf = r._leaf;
    if (!leaf || !leaf.detail) { flush(); out.push(r); return; }
    if (run && (run.detail !== leaf.detail || run.parent !== leaf.parent)) flush();
    if (!run) run = { detail: leaf.detail, parent: leaf.parent, rows: [] };
    run.rows.push(r);
  });
  flush();
  return out;
}

/** 一批判定单元的条件行 → 展示行。suppressDetail = 合并块标题已给出的原因:与它同句、
    又提不出组码的叶子不再重复显示(8 个分支逐个报"主要诊断不在码表内",标题已给过答案,
    行里只留结构),剪空的双亲容器随之消失。 */
function condUnitRows(units, suppressDetail) {
  const rows = [];
  (units || []).forEach(u => {
    let roots = condTree(u).roots;
    if (suppressDetail) roots = roots.filter(r => !pruneNode(r, suppressDetail));
    roots.forEach(r => treeRows(r, 0, 0, rows, null));
  });
  return mergeChipRuns(rows);
}

// ---- 判定清单:结构语言退场,直接回答「要什么、差什么、差多少」 ----
// 条件树还原解决的是「顺序错乱」,但树本身仍是评价器的结构(「满足任一」「同时满足 N 项」
// 是分组器的思考方式,不是医生的)。一线用户读日志只想要三个答案:规则要什么、我卡在哪、
// 哪条路最可惜。此处把每个落选候选翻译成清单:头部「差 N 项」,下面是最近一条路径的
// 逐项勾叉,OR 的其余分支一句话带过。只消费轨迹行自带的短语与结果,不解析 DSL。

/** 节点判定分析:total = 应满足项数(AND 取条件项数,OR 取分支数),passed = 已满足数,
    gap = 还差几项,leaves = 「最近一条路径」的叶子清单(引擎评估顺序)。
    分母/已满足数由引擎随容器行下发(AND 首败即停,未判项没有轨迹行,数行会得出错分母)。
    AND(同时满足)直接展开子项;OR(任一)失败时全分支已评估,取缺口最小的分支作最近路径,
    其余分支的失败原因取多数(过半才说)作脚注 —— 局部原因不冒充普遍原因。 */
function analyzeNode(node) {
  const s = node.s;
  if (!isContainerCond(s)) {
    const okFlag = s.result === true;
    return { total: 1, passed: okFlag ? 1 : 0, gap: okFlag ? 0 : 1, kind: 'leaf',
      leaves: [{ ok: okFlag, expr: s.expression || '', detail: s.detail || '', setRef: s.setRef || null }] };
  }
  const subs = node.kids.map(analyzeNode);
  if (s.nodeKind === 'All') {
    const total = s.total != null ? s.total : node.total;
    const passed = s.passed != null ? s.passed : node.kids.filter(k => k.s.result === true).length;
    return { total, passed, gap: Math.max(total - passed, 0), kind: 'and', unjudged: s.unjudged || 0,
      leaves: subs.flatMap(a => a.leaves) };
  }
  const sorted = subs.slice().sort((a, b) => a.gap - b.gap);
  const best = sorted[0] || { gap: 1, leaves: [] };
  const others = subs.filter(a => a !== best);
  const reasons = others
    .map(a => { const f = a.leaves.find(l => !l.ok && l.detail); return f && f.detail; })
    .filter(Boolean);
  let otherReason = null;
  if (reasons.length) {
    const cnt = new Map();
    reasons.forEach(d => cnt.set(d, (cnt.get(d) || 0) + 1));
    const top = [...cnt.entries()].sort((a, b) => b[1] - a[1])[0];
    if (top[1] * 2 >= reasons.length) otherReason = top[0];
  }
  return { total: s.total != null ? s.total : subs.length,
    passed: s.passed != null ? s.passed : 0, gap: best.gap, kind: 'or',
    unjudged: s.unjudged || 0,
    branchCount: subs.length, leaves: best.leaves, otherReason };
}

/** 判定单元 → 分析结果:取缺口最小的失败根(单元通常单根;多个根时只统计未通过的)。 */
function unitAnalysis(conds) {
  let best = null;
  condTree(conds).roots.forEach(r => {
    if (r.s.result !== false) return;
    const a = analyzeNode(r);
    if (!best || a.gap < best.gap) best = a;
  });
  return best;
}

/** 单元的候选码:优先取引擎显式下发的 owner;旧响应回退路径抠码。
    MDC 门控单元无组码路径,由流程行消息补。 */
const unitCodeOf = u => {
  for (const s of u) { const c = condCodeOf(s); if (c) return c; }
  return null;
};

/** 清单行:✓/✗ + 条件短语(引擎原文,去码表注记)+ 本例事实。
    码表条件的短语带集合链接(编号在短语内就地处链接,语义名短语则编号缀后),跳数据一览对账。 */
function checklistRow(l) {
  const expr = friendlyExpr(l.expr) || '（条件）';
  return el('div', { class: 'vrow ' + (l.ok ? 'vrow-ok' : 'vrow-miss') }, [
    el('span', { class: 'vmark', text: l.ok ? '✓' : '✗' }),
    l.setRef ? setExprSpan(expr, l.setRef, 'vexpr')
      : el('span', { class: 'vexpr', text: expr }),
    l.detail ? el('span', { class: 'vdetail', text: l.detail }) : null,
  ].filter(Boolean));
}

/** 判定清单卡:一行头(候选码 + 差几项 + 路径数)+ 最近路径的逐项勾叉 + 其余路径脚注。
    「需同时满足 N 项 / 满足任一」等容器措辞不再出现 —— 用户读清单,不读结构。 */
function verdictCard(code, a) {
  const card = el('div', { class: 'vcard' }, [
    el('div', { class: 'vcard-head' }, [
      code ? el('span', { class: 'log-code mono', text: code }) : null,
      el('span', { class: 'vcard-gap', text: `差 ${a.gap} 项` }),
      a.branchCount > 1 ? el('span', { class: 'vcard-paths', text: `${a.branchCount} 条路径取其一` }) : null,
    ].filter(Boolean)),
    ...a.leaves.map(checklistRow),
  ]);
  if (a.branchCount > 1) {
    const rest = a.branchCount - 1;
    card.appendChild(el('div', { class: 'vcard-foot' }, a.otherReason
      ? `其余 ${rest} 条路径均止步于「${a.otherReason}」`
      : `其余 ${rest} 条路径均未走通`));
  }
  return card;
}

/** 单元是否出清单卡:最近路径有多项、且结果有区分度(有 ✓,或失败原因不止一种)——
    单因单叶的候选一句话说得清,留给芯片行,不为一句话撑一张卡。 */
const isCardUnit = a => !!a && a.leaves.length >= 2
  && (a.leaves.some(l => l.ok) || new Set(a.leaves.map(l => l.detail)).size > 1);

/** 单因候选的叶子拍平:一句话说得清的候选,容器行(「满足任一(0/2)」「同时满足 2 项(0/2)」)
    是纯结构噪音;拍成根级叶子行后,同因叶子才能跨单元合并成一行芯片(14 个组压回一行)。
    suppressDetail = 块标题已给出的原因:与它同句、又提不出组码的叶子不重复显示。 */
function flatUnitLeaves(u, out, suppressDetail) {
  condTree(u).roots.forEach(r => {
    (function walk(n) {
      if (isContainerCond(n.s)) { n.kids.forEach(walk); return; }
      if (suppressDetail && !condCodeOf(n.s)
        && n.s.detail === suppressDetail) return;
      const row = condRow(n.s, 0, 0);
      row._leaf = {
        detail: n.s.detail || '',
        code: condCodeOf(n.s.conditionPath || ''),
        setRef: n.s.setRef || null,
        parent: null, depth: 0,
      };
      out.push(row);
    })(r);
  });
}

/** 合并块明细 → 判定清单。复杂候选出清单卡(卡片在前,最可惜的先读),单因候选拍平成
    叶子行做芯片合并(与块标题同因的裸叶子仍被剪掉,不为重复答案撑行)。 */
function verdictRows(b, suppressDetail) {
  const cards = [], simple = [];
  (b.units || []).forEach((u, i) => {
    const a = unitAnalysis(u);
    if (isCardUnit(a)) { cards.push(verdictCard(blockUnitCode(b, u, i), a)); return; }
    flatUnitLeaves(u, simple, suppressDetail);
  });
  return [...cards, ...mergeChipRuns(simple)];
}

/** 「距入组最近」摘要:半命中候选(路径含 ≥2 项条件、且没全过)里缺口最小的几条。
    单条件候选人人都是「差 1 项」,列出来没有区分度;真正值得先看的是"已满足一部分、
    再对上一项就进组"的路径(如 MDCZ 满足 1/2)。无此类候选时不显示。 */
function nearestSummary(blocks) {
  const items = [];
  (blocks || []).forEach(b => {
    if (b.type !== 'miss' && b.type !== 'gate') return;
    (b.units || []).forEach((u, i) => {
      const a = unitAnalysis(u);
      const code = blockUnitCode(b, u, i);
      if (a && a.gap >= 1 && a.total >= 2 && code) {
        items.push({ code, gap: a.gap, passed: a.passed, total: a.total, kind: a.kind });
      }
    });
  });
  if (!items.length) return null;
  const min = Math.min(...items.map(i => i.gap));
  const sameMin = items.filter(i => i.gap === min);
  const rest = items.length - sameMin.length;
  // OR 与 AND 的分母语义不同:OR 的 total 是分支数(「0/3 条路径走通」),AND 是条件项数
  // (「已满足 1/2」)—— 混用同一句式会把"3 个分支"读成"3 个条件"
  const fmt = i => i.kind === 'or'
    ? `${i.code} 差 ${i.gap} 项（${i.passed}/${i.total} 条路径走通）`
    : `${i.code} 差 ${i.gap} 项（已满足 ${i.passed}/${i.total}）`;
  let t = sameMin.slice(0, 3).map(fmt).join(' · ');
  if (sameMin.length > 3) t += ` 等 ${sameMin.length} 条`;
  if (rest > 0) t += ` · 另有 ${rest} 条差 ≥ ${min + 1} 项`;
  return t;
}

/** 一组条件里占多数的失败原因(需过半),作为合并块的标题。原因分散时返回 null ——
    宁可只报数量,不可把局部原因说成普遍原因。 */
function dominantReason(conds) {
  const fails = (conds || []).filter(s => s.result === false && s.detail);
  if (!fails.length) return null;
  const cnt = new Map();
  fails.forEach(s => cnt.set(s.detail, (cnt.get(s.detail) || 0) + 1));
  const top = [...cnt.entries()].sort((a, b) => b[1] - a[1])[0];
  return top[1] * 2 >= fails.length ? top[0] : null;
}

/** 合并块标题的集合链接:块内码表原因叶子的 setRef 去重 —— 标题的"不在码表内"
    要与码表编号同现,展开前后都能跳数据一览对账。非码表原因不带链接。 */
const blockSetRefs = b => {
  const refs = [];
  if (!isSetReason(b.reason)) return refs;
  b.units.forEach(u => u.forEach(s => {
    if (s.setRef && isSetReason(s.detail || '') && !refs.includes(s.setRef)) refs.push(s.setRef);
  }));
  return refs;
};

function logBlocks(list) {
  const units = [];
  let cur = null;
  (list || []).forEach(s => {
    if (isNoInfoCond(s)) return;
    if (isCondTrace(s)) {
      if (!cur) { cur = { head: null, conds: [] }; units.push(cur); }
      cur.conds.push(s);
      return;
    }
    cur = { head: s, conds: [] };
    units.push(cur);
  });

  const out = [], merged = new Map();
  // 块分类:引擎已下发 stepCode(机读分类键)+ code/args,直接读它。
  // 原先三条正则把块类型绑在中文句式的形状上 —— 文案一改(多个空格、换标点)整块归类就失效,
  // 明细全落成互不相干的散块,且不报错。
  const classify = h => {
    if (!h) return null;
    const args = h.args || {};
    switch (h.stepCode) {
      case 'adrgNotMatched': return { kind: 'miss', mdc: args.mdc || '', code: h.code || '' };
      case 'mdcGateFailed': return { kind: 'gate', mdc: h.code || '', reason: args.why || null };
      case 'adrgSkippedMedical': return { kind: 'skip', mdc: args.mdc || '', code: h.code || '' };
      default: return null;
    }
  };
  units.forEach(u => {
    const c = classify(u.head);
    // ① verbose:「MDCx 未匹配到 YY1」逐候选一行,按(所属 MDC + 主要失败原因)合并
    if (c && c.kind === 'miss') {
      const reason = dominantReason(u.conds);
      const key = `M|${c.mdc}|${reason || ''}`;
      let b = merged.get(key);
      if (!b) { b = { type: 'miss', mdc: c.mdc, reason, count: 0, units: [] }; merged.set(key, b); out.push(b); }
      b.count++;
      b.units.push(u.conds);
      return;
    }
    // ② 默认轨迹:「MDCx 未进入：<原因>」——8 个 MDC 因同一句"主要诊断不在码表内"被排除时,
    //    列 8 行等于把同一句话抄 8 遍,按原因合并成"8 个 MDC 未进入：<原因>",展开才看是哪几个
    if (c && c.kind === 'gate') {
      const reason = c.reason || '';
      const key = `G|${reason}`;
      let b = merged.get(key);
      if (!b) { b = { type: 'gate', reason, count: 0, codes: new Set(), heads: [], units: [] }; merged.set(key, b); out.push(b); }
      b.count++;
      if (c.mdc) b.codes.add(c.mdc);
      b.heads.push(u.head);
      b.units.push(u.conds);
      return;
    }
    // ③ 手术病例逐个内科组跳过:「MDCx 手术病例,跳过内科组 YY1」逐组一行,同因不同码,
    //    与 ①② 同病 —— 按所属 MDC 合并成一行芯片,展开才看是哪几组
    if (c && c.kind === 'skip') {
      const key = `S|${c.mdc}`;
      let b = merged.get(key);
      if (!b) { b = { type: 'skip', mdc: c.mdc, count: 0, codes: new Set(), units: [] }; merged.set(key, b); out.push(b); }
      b.count++;
      if (c.code) b.codes.add(c.code);
      b.units.push(u.conds);
      return;
    }
    out.push({ type: 'unit', head: u.head, conds: u.conds });
  });
  return out;
}

/** 可折叠结论块:一行结论(徽章 + 数量 + 原因)+ 展开才看的明细。两类合并块共用。
    徽章取中性色(t-cond)而非语义红:被排除的候选是分组过程的常态,不是报错 —— 染红会让
    用户以为系统出了问题,而这里要传达的是"这条路没走通,原因如下"。 */
function collapseBlock(badgeText, title, reason, detailRows, setRefs) {
  const detail = el('div', { class: 'logunit-detail' }, detailRows);
  detail.hidden = true;
  const head = el('button', {
    type: 'button', class: 'logmiss-head', 'aria-expanded': 'false',
    title: '展开看逐个判定明细',
  }, [
    el('span', { class: 'tbadge t-cond', text: badgeText }),
    el('span', { class: 'ttext', text: title }),
    reason ? el('span', { class: 'tdetail', text: reason }) : null,
    ...setLinkEls(setRefs),
    el('span', { class: 'logmiss-caret', text: '▸' }),
  ].filter(Boolean));
  head.addEventListener('click', () => {
    const open = detail.hidden;
    detail.hidden = !open;
    head.setAttribute('aria-expanded', String(open));
    head.classList.toggle('is-open', open);
  });
  return el('div', { class: 'logunit logmiss' }, [head, detail]);
}

function renderLogBlock(b) {
  if (b.type === 'unit') {
    const wrap = el('div', { class: 'logunit' });
    if (b.head) wrap.appendChild(el('div', { class: 'trow' }, [renderTraceStep(b.head)]));
    if (b.conds.length) wrap.appendChild(el('div', { class: 'logunit-detail' }, condUnitRows([b.conds])));
    return wrap;
  }
  if (b.type === 'miss') {
    // 标题点名 MDC:裸字母"I：1 个核心组"会被读成"1: 1 个" —— 补 MDC 前缀
    return collapseBlock('未命中', `MDC${b.mdc}：${b.count} 个核心组`, b.reason,
      verdictRows(b, b.reason), blockSetRefs(b));
  }
  if (b.type === 'skip') {
    return collapseBlock('跳过', `MDC${b.mdc}：${b.count} 个内科组`, '按手术路径分组，内科组不适用',
      [chipRow([...b.codes]), ...condUnitRows(b.units)]);
  }
  // 门控块:首行列出是哪几个 MDC,其后是判定清单 —— "主诊断命中但其他诊断未中"
  // 这类分支半命中故事是这层的核心信息,只给码会把它藏掉
  return collapseBlock('未进入', `${b.count} 个 MDC`, b.reason,
    [chipRow([...b.codes]), ...verdictRows(b, b.reason)], blockSetRefs(b));
}

/** 证据链行:每条依据合并为单行「规则短语 → 本病例事实 ✓」。
    原先拆成"条件内容/命中事实"两行(各带左侧标签),一条依据占两行、标签重复出现,
    5 条依据就是 10 行,视觉很碎 —— 一线反馈"看不懂在说什么"。合并后规则短语走 mono 次级色、
    事实走主色,一行内仍有"要什么(规则) → 有什么(事实)"的因果读法,但不再有标签噪音。 */
export function evRows(list) {
  return (list || []).map(p => el('div', { class: 'ev-row ev-pair' }, [
    p.expr ? el('span', { class: 'ev-expr', text: p.expr }) : null,
    p.expr ? el('span', { class: 'ev-arrow', text: '→' }) : null,
    el('span', { class: 'ev-fact' }, [el('span', { text: p.fact }), el('span', { class: 'ev-ck', text: ' ✓' })]),
  ].filter(Boolean)));
}

/** 横链视图:分组链横向一屏(编码对照 › MDC › ADRG › DRG),段即按钮,
    判定依据(原文→事实证据链行、官方规则抽屉)收进段下展开面板,按需消费。 */
export function buildChainView(m, status, o, zhRefs, loadTrace) {
  const wrap = el('div', { class: 'tl-chain-wrap' });
  const chain = el('div', { class: 'tl-chain', role: 'group', 'aria-label': '分组链，点击各段查看判定依据' });
  const panelHost = el('div', {});
  const segEls = {};
  let activeKey = null;
  // 横链不排"层级标签列",标签直接拼回短语(竖轴那边的定宽标签只为三层对齐服务)
  const labelOf = s => (s.tag ? s.tag + ' ' : '') + s.label;

  const stateOf = s => s.tone === 'ok' ? '✓' : s.tone === 'warn' ? '!' : '✗';
  const zhOf = s => {
    if (s.maps) return '编码对照';
    if (s.key === 'mdc' || s.key === 'adrg') return s.code === '—' ? '' : ''; // 幽灵帧的副标题由 subOf 兜底,zh 留空;正常帧中文名由官方明细异步补齐
    if (status === 'Ambiguous') return '歧义病案';
    if (status !== 'Success') return '未能入组';
    return (o.group && o.group.name) || '';
  };
  const subOf = s => {
    if (s.maps) return `${s.maps.length} 项映射`;
    if (s.key === 'mdc') {
      // 事实行已去「命中主要诊断」前缀,直接取首条(编码+名称)作副标题;幽灵帧(码「—」)没有命中事实
      const dx = (s.evidence || []).map(p => p.fact).find(Boolean);
      return dx ? `命中 ${dx}` : (s.tone === 'ok' ? '主诊断命中' : '未命中');
    }
    if (s.key === 'adrg') {
      if (s.code === '—') return '未命中';
      const k = adrgKindOf(s.code);
      return s.adrgProc ? `${k} · 手术 ${s.adrgProc}` : (k ? `${k} · 无手术` : '');
    }
    if (status === 'Success') {
      const mcc = (o.majorComplications || []).length, cc = (o.minorComplications || []).length;
      return mcc || cc ? `${mcc} 项MCC · ${cc} 项CC` : '无并发症';
    }
    return status === 'Ambiguous' ? '主手术与主诊断无关' : (o.reasonText || '未入组');
  };

  m.steps.forEach((s, i) => {
    if (i) chain.appendChild(el('span', { class: 'tl-seg-arrow', text: '›' }));
    const hasCode = s.code && s.code !== '—';
    // 段同样带层级 key 类:状态色只染落点段,途经段保持墨色(与竖轴同一套三级分级)
    const seg = el('button', { type: 'button', class: 'tl-seg tl-seg-' + s.key + ' tone-' + s.tone, title: labelOf(s) });
    const top = el('span', { class: 'tl-seg-top' });
    if (hasCode) top.appendChild(el('span', { class: 'tl-seg-code', text: s.code }));
    const zhText = zhOf(s);
    const zh = el('span', { class: 'tl-seg-zh', text: zhText, title: zhText });
    if (s.key === 'mdc') zhRefs.mdcName.push(zh);
    if (s.key === 'adrg') zhRefs.adrgName.push(zh);
    top.appendChild(zh);
    top.appendChild(el('span', { class: 'tl-seg-state', text: stateOf(s) }));
    seg.append(top, el('span', { class: 'tl-seg-sub', text: subOf(s) }));
    seg.addEventListener('click', () => toggle(s.key));
    segEls[s.key] = seg;
    chain.appendChild(seg);
  });

  const buildPanel = s => {
    const hasCode = s.code && s.code !== '—';
    const p = el('div', { class: 'tl-seg-panel' });
    const ph = el('div', { class: 'tl-panel-head' }, [
      hasCode ? el('span', { class: 'tl-panel-code mono', text: s.code }) : null,
      el('span', { class: 'tl-panel-title', text: labelOf(s) }),
    ]);
    p.appendChild(ph);
    // 与竖轴一致:入口右对齐钉在面板头(编码+动作)行尾,内容落在面板头下方。
    // 依据默认展开 —— 点横链的段就是为了看依据,段下面板本身就是"按需消费"的落点,
    // 进来还得再点一次「判定依据」是多余一步(竖轴三帧各自平行铺陈,保持收起)。
    // 详细日志仍保持收起:它是依据区内的第二层,默认铺开会把"段下面板"变成日志墙。
    if ((s.evidence && s.evidence.length) || (s.log && s.log.length)) {
      const evi = eviBlock(s.evidence, { open: true, log: s.log, logFilter: s.logFilter, loadTrace, conclusion: s.why });
      ph.appendChild(evi.btn);
      p.appendChild(evi.rows);
    }
    if (s.maps) {
      p.appendChild(mapChips(s.maps));
    }
    if (s.key === 'drg' && m.next.length) {
      const box = el('div', { class: 'ev-next-box' });
      m.next.forEach(t => box.appendChild(el('div', { class: 'ev-next-line', text: t })));
      p.appendChild(el('div', { class: 'ev-row ev-next' }, [
        el('span', { class: 'ev-k', text: '下一步' }),
        box,
      ]));
    }
    return p;
  };

  const toggle = key => {
    if (activeKey === key) {
      panelHost.textContent = '';
      activeKey = null;
    } else {
      panelHost.textContent = '';
      const s = m.steps.find(x => x.key === key);
      if (s) panelHost.appendChild(buildPanel(s));
      activeKey = key;
    }
    Object.entries(segEls).forEach(([k, node]) => node.classList.toggle('is-active', k === activeKey));
  };

  wrap.append(chain, panelHost);
  return { el: wrap };
}

/** 码表条件的表达式 + 集合下钻:引擎随条件下发的官方集合编号(setRef)渲染成跳
    「数据一览 → 编码集合」的链接 —— 短语给语义,链接给对账入口。未标注集合的短语
    本身带集合编号(如「主要手术在码表 OP1_AA1 内」),就地包链接不重复出编号。 */
function setExprSpan(text, setRef, cls = 'texpr') {
  const span = el('span', { class: cls });
  const link = () => el('button', {
    type: 'button', class: 'tset-link', text: setRef,
    title: `查看集合 ${setRef} 及其成员（跳转数据一览）`,
    onclick: () => jumpToSet(setRef),
  });
  const at = text.indexOf(setRef);
  if (at < 0) {
    span.appendChild(document.createTextNode(text));
    span.appendChild(document.createTextNode('\u00A0'));
    span.appendChild(link());
  } else {
    span.appendChild(document.createTextNode(text.slice(0, at)));
    span.appendChild(link());
    span.appendChild(document.createTextNode(text.slice(at + setRef.length)));
  }
  return span;
}

/** 码表原因的集合链接:合并芯片行与合并块标题只保留了原因散文("…不在码表内"),
   集合编号在压缩中丢了 —— 补成可点的链接,跳「数据一览 → 编码集合」对账。
   只在原因确实是码表原因时出现;同一行多个候选各带各的码表时去重,超过 3 个折叠。 */
const isSetReason = t => /码表/.test(t || '');
function setLinkEls(setRefs, cap = 3) {
  const refs = [...new Set((setRefs || []).filter(Boolean))];
  if (!refs.length) return [];
  const els = refs.slice(0, cap).map(r => el('button', {
    type: 'button', class: 'tset-link', text: r,
    title: `查看集合 ${r} 及其成员（跳转数据一览）`,
    onclick: () => jumpToSet(r),
  }));
  if (refs.length > cap) els.push(el('span', { class: 'tdetail', text: `等 ${refs.length} 个码表` }));
  return els;
}

export function renderTraceStep(step, numPrefix) {
  const isCond = isCondTrace(step);
  // 徽章术语与时间轴三级对齐:引擎 stage 的"入组/并发症/映射"是内部视角,
  // 用户语言里这三层叫"核心组(ADRG)/定档(DRG 档位)/编码"——同一行日志读完不换脑
  let badgeClass = 't-cond', label = step.stage;
  if (step.stage === 'MDC') { badgeClass = 't-mdc'; label = 'MDC'; }
  else if (step.stage === '入组') { badgeClass = 't-place'; label = '核心组'; }
  else if (step.stage === '并发症') { badgeClass = 't-cc'; label = '定档'; }
  else if (step.stage === '校验') { badgeClass = 't-check'; }
  else if (step.stage === '映射') { badgeClass = 't-check'; label = '编码'; }

  const tstep = el('div', { class: 'tstep' });
  tstep.appendChild(el('span', { class: 'tbadge ' + badgeClass, text: label }));
  if (isCond) {
    // 原始条件路径翻译成医生可读的位置标签:引擎随行下发 scope/owner(调用点显式标注来源)
    const loc = traceLoc(step);
    if (loc) tstep.appendChild(el('span', { class: 'tpath', text: loc }));
    if (step.expression) {
      const exprText = (numPrefix || '') + friendlyExpr(step.expression);
      tstep.appendChild(step.setRef ? setExprSpan(exprText, step.setRef) : el('span', { class: 'texpr', text: exprText }));
    }
    tstep.appendChild(el('span', { class: step.result ? 't-yes' : 't-no', text: step.result ? '✓' : '✗' }));
    if (step.detail) tstep.appendChild(el('span', { class: 'tdetail', text: step.detail }));
  } else if (step.message) {
    tstep.appendChild(el('span', { class: 'ttext', text: step.message }));
  }
  return tstep;
}

/** 把条件轨迹翻译成用户可读的位置标签(医生不需索引细节)。
    数据源是引擎随行下发的 scope(判定来源层)+ owner(所属码,调用点显式标注):
    Gate/QyGate 的 owner 是 MDC 码,AdrgEntry 是核心组码,DrgSplit 是细分组(档位)码 ——
    逐档试过、哪档没过是详细日志的核心信息,码必须点名。原先 5 组路径正则已整体退役。 */
export function traceLoc(step) {
  const own = step.owner || '';
  switch (step.scope) {
    case 'Gate': return own ? `MDC${own} 入组条件` : 'MDC 入组条件';
    case 'QyGate': return own ? `MDC${own} 歧义组条件` : '歧义组条件';
    case 'AdrgEntry': return own ? `${own} 入组条件` : '入组条件';
    case 'DrgSplit': return own ? `${own} 细分组条件` : '细分组条件';
    default: return '';
  }
}
