// ---- 域间依赖(拆分脚本自动生成) ----
import { $, $$, api, el, maskName, setHidden, showErr } from './dom.js';
import { prefillFeeForm } from './fee.js';
import { buildGroupBody, clearForm, collectChipNames, renderOutcome, updateWhatIfToggle } from './group.js';
import { switchTab } from './nav.js';
import { codeVersionParam, fields, regionParam, setLastCase, state } from './state.js';

// ------------------------------ 病案提取(可选模块) ------------------------------

// 后端 --his 加载连接器后可用:GET /api/his/group?id= 提取→分组→回填一条龙;映射由引擎统一处理。
// 未加载(503)/提取失败(502)时 readJson 会提取 ProblemDetails 文本,错误直接展示在卡片内。

export async function runHisFetch() {
  const id = $('#hisIdInput').value.trim();
  showErr('#hisError');
  if (!id) return showErr('#hisError', '请输入就诊号');

  const btn = $('#hisFetch');
  btn.disabled = true; btn.textContent = '提取中…';
  try {
    const d = await api.get('/api/his/group', {
      id,
      ...codeVersionParam(),
      ...regionParam(),
    });
    switchTab('single');          // 提取条是全局的:成功后落到单病例视图看结果
    await renderHisResult(d);
  } catch (e) {
    showErr('#hisError', '提取失败:' + e.message);
  } finally {
    btn.disabled = false; btn.textContent = '提取并分组';
  }
}

/** 把 HIS 病案回填到单病例表单:性别/年龄/新生儿字段/诊断与手术芯片,便于人工修正后重新分组。
    编码区一律经 ChipField.addCodes 走 /api/lookup 解析(异步),调用方必须 await 后再提交分组。 */
export async function fillFormFromHis(d) {
  clearForm(); // 只清表单,保留结果区刚出的分组结果
  const p = d.patient || {};
  renderPickedBar(p, $('#hisIdInput').value.trim());
  if (p.gender === '1' || p.gender === '2') {
    state.gender = p.gender;
    $$('.seg-item').forEach(x => x.classList.toggle('is-active', x.dataset.gender === p.gender));
    $$('.seg-item').forEach(x => x.setAttribute('aria-checked', x.dataset.gender === p.gender));
  }
  // age=0 是新生儿的显式合法值(官方 MDCP 门控 NL=0 为等值判定,缺失/null 判不通过),
  // 不能用 >0 过滤,否则日龄字段虽填了但年龄为空,病例会流出 MDCP 落成人组。
  if (p.age != null && p.age >= 0) $('#ageInput').value = p.age;
  if (p.ageDay > 0 || p.weight > 0) {
    setHidden($('#neoFields'), false);
    $('#neoToggle').setAttribute('aria-expanded', 'true');
    if (p.ageDay > 0) $('#ageDayInput').value = p.ageDay;
    if (p.weight > 0) $('#weightInput').value = p.weight;
  }
  // 诊断/手术:首条进主栏(single 栏位天然只留一条),其余进其他栏
  const dx = d.diagnoses || [], op = d.operations || [];
  await Promise.all([
    fields.mainDx.addCodes(dx.slice(0, 1)),
    fields.otherDx.addCodes(dx.slice(1)),
    fields.mainProc.addCodes(op.slice(0, 1)),
    fields.otherProc.addCodes(op.slice(1)),
  ]);
  prefillFeeForm(d.patient); // 费用口径一并回填(费用节隐藏时自动跳过)
}

// ------------------------------ 示例病例 ------------------------------

// 典型测试病例(全部经 /api/group 实测核过落位):覆盖机器人直赋/高危妊娠直赋/合并症分档/QY 歧义/无法入组/新生儿。
// 结构对齐 fillFormFromHis 入参,点选即回填表单并自动分组;改分组规则后预期码可能漂移,以实际结果为准。
export const EXAMPLE_CASES = [
  { name: '机器人直赋', expect: 'EB10', desc: '肺叶切除配腹腔镜机器人码 17.4200,命中脚注18 直赋 0 档',
    patient: { gender: '1', age: 60 },
    diagnoses: [{ code: 'C34.000', name: '主支气管恶性肿瘤' }],
    operations: [{ code: '32.0901', name: '支气管纵隔切除术' }, { code: '17.4200', name: '腹腔镜机器人援助操作' }] },
  { name: '康复码不直赋', expect: 'EB19', desc: '同上换 17.4901 机器人辅助康复:灰码且无直赋资格,回落常规档',
    patient: { gender: '1', age: 60 },
    diagnoses: [{ code: 'C34.000', name: '主支气管恶性肿瘤' }],
    operations: [{ code: '32.0901', name: '支气管纵隔切除术' }, { code: '17.4901', name: '机器人辅助康复' }] },
  { name: '高危妊娠直赋', expect: 'OB11', desc: '剖宫产 + 妊娠糖尿病,主诊命中高危妊娠清单直赋 1 档',
    patient: { gender: '2', age: 30 },
    diagnoses: [{ code: 'O24.400', name: '妊娠发生的糖尿病' }],
    operations: [{ code: '74.1x01', name: '子宫下段剖宫产术' }, { code: '38.8609', name: '' }] },
  { name: '高危妊娠伴 MCC', expect: 'OB21', desc: '同上 + 心衰(I50.900):主诊仍在高危清单,直赋优先于 MCC 分档',
    patient: { gender: '2', age: 65 },
    diagnoses: [{ code: 'O24.400', name: '妊娠发生的糖尿病' }, { code: 'I50.900', name: '心力衰竭' }],
    operations: [{ code: '74.1x01', name: '子宫下段剖宫产术' }] },
  { name: '伴严重并发症(MCC)', expect: 'IR21', desc: '股骨骨折切开复位 + 心衰:常规 1 档(伴严重合并症或并发症)',
    patient: { gender: '1', age: 60 },
    diagnoses: [{ code: 'S72.000', name: '股骨颈骨折' }, { code: 'I50.900', name: '心力衰竭' }],
    operations: [{ code: '79.1500', name: '骨折切开复位内固定术' }] },
  { name: '伴一般并发症(CC)', expect: 'IR23', desc: '股骨骨折切开复位 + 糖尿病:常规 3 档(伴一般合并症或并发症)',
    patient: { gender: '1', age: 60 },
    diagnoses: [{ code: 'S72.000', name: '股骨颈骨折' }, { code: 'E14.100', name: '未特指的糖尿病' }],
    operations: [{ code: '79.1500', name: '骨折切开复位内固定术' }] },
  { name: '不伴并发症', expect: 'IR25', desc: '单纯股骨骨折手术,无合并症:常规 5 档',
    patient: { gender: '1', age: 60 },
    diagnoses: [{ code: 'S72.000', name: '股骨颈骨折' }],
    operations: [{ code: '79.1500', name: '骨折切开复位内固定术' }] },
  { name: 'QY 歧义', expect: 'IQY', desc: '股骨骨折主诊配剖宫产手术:诊断与手术不匹配,需人工复核',
    patient: { gender: '1', age: 30 },
    diagnoses: [{ code: 'S72.000', name: '股骨颈骨折' }],
    operations: [{ code: '74.1x01', name: '子宫下段剖宫产术' }] },
  { name: '无法入组', expect: '0000', desc: '高血压(I10.x00)无手术:不在任何 MDC 主诊表,官方 MDC 0000 全局兜底组',
    patient: { gender: '1', age: 60 },
    diagnoses: [{ code: 'I10.x00', name: '原发性高血压' }],
    operations: [] },
  { name: '新生儿', expect: 'PU35', desc: '肺炎新生儿(日龄 5 天/体重 3000g),按新生儿病组分组',
    patient: { gender: '2', age: 0, ageDay: 5, weight: 3000 },
    diagnoses: [{ code: 'J18.900', name: '肺炎' }],
    operations: [] },
];

/** 点选示例:与 HIS 提取同一条 fillFormFromHis 链,码表标志由 addCodes 统一查回(不写死进前端)。 */
export async function fillExample(c) {
  await fillFormFromHis({ patient: c.patient, diagnoses: c.diagnoses, operations: c.operations });
  $('#recordForm').requestSubmit();
}

/** 示例病例弹窗:每次打开按 EXAMPLE_CASES 重建(与换主诊/手术模拟同款原生 dialog 交互)。 */
export function openExampleDlg() {
  const dlg = $('#exampleDlg');
  const list = el('div', { class: 'wi-table' }, EXAMPLE_CASES.map(c => el('button', {
    type: 'button', class: 'ex-row',
    onclick: () => { dlg.close(); fillExample(c); },
  }, [
    el('span', { class: 'wi-kind', text: c.name }),
    el('span', { class: 'wi-name', text: c.desc, title: c.desc }),
    el('span', { class: 'ex-code', text: '预期 ' + c.expect }),
  ])));
  dlg.replaceChildren(
    el('div', { class: 'wi-head' }, [
      // id 与 #exampleDlg 的 aria-labelledby 对应,否则弹窗对读屏器无名(见 index.html 注释)
      el('b', { id: 'exampleDlgTitle', text: '示例病例' }),
      el('span', { class: 'ex-hint', text: EXAMPLE_CASES.length + ' 个典型场景,点击即填入并分组' }),
      el('button', { type: 'button', class: 'chip-x', text: '×', 'aria-label': '关闭', onclick: () => dlg.close() }),
    ]),
    list,
  );
  dlg.showModal();
}

/** 最近一次 HIS 提取:脱敏开关切换后据此重绘,避免屏幕上留着已渲染的真实姓名。 */
let lastPicked = null;

/** 左侧「病例信息」摘要条:提取后的身份字段置左;无字段时隐藏。
    姓名经 maskName 处理 —— 脱敏是显示层行为,开关见 dom.js。 */
export function renderPickedBar(p, patientId) {
  lastPicked = { p, patientId };
  const grid = $('#pickedGrid');
  grid.innerHTML = '';
  const items = [
    ['姓名', maskName(p.name)],
    ['就诊号', patientId],
    ['住院天数', p.inHospitalDays],
  ].filter(([, v]) => v != null && v !== '');
  items.forEach(([k, v]) => grid.appendChild(el('div', { class: 'pv' }, [
    el('span', { class: 'pv-k', text: k }),
    el('b', { class: 'pv-v', text: String(v) }),
  ])));
  $('#pickedBar').hidden = !items.length;
}

/** 脱敏开关变化后重绘「病例信息」条;尚未提取过则不动。 */
export function refreshPickedBar() {
  if (lastPicked) renderPickedBar(lastPicked.p, lastPicked.patientId);
}

export async function renderHisResult(d) {
  const pane = $('#resultPane');
  pane.innerHTML = '';

  // 提取到的内容直接回填左侧表单(患者/诊断/手术/费用口径),右侧只看分组与费用结果
  await fillFormFromHis(d);

  // 与手工录入共用同一张结果卡:BatchRow 字段名映射回 GroupOutcome 形态
  const g = d.grouping || {};
  // 表单即真实数据源:同步 lastCase,HIS 提取后无需再手动分组即可用换主诊/手术模拟
  setLastCase({
    body: buildGroupBody(), candNames: collectChipNames(),
    currentCode: g.code, currentName: g.name || '',
    currentRw: d.fee?.rw ?? null, currentStatusText: g.statusText || g.status,
  });
  updateWhatIfToggle();
  const outcome = {
    status: g.status, statusText: g.statusText, code: g.code,
    group: g.name ? { name: g.name } : null,
    mdc: g.mdc, adrg: g.adrg, reasonText: g.reason || '',
    mappings: d.mappings || [],
    excludedComplications: d.excludedComplications || [],
    trace: d.trace || [],
  };
  // fee: 服务端 /api/his/group 响应中内嵌的 RW/参考费用;结果卡装配与手工分组共用 renderOutcome
  await renderOutcome(pane, outcome, d.fee);
}
