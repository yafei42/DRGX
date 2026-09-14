// ---- 域间依赖(拆分脚本自动生成) ----
import { $, $$, DEBOUNCE_CHIP_MS, announce, api, badgeEl, codeBadges, currentCodeVersion, debounce, el, mapDisplayVisible, staleGuard } from './dom.js';

// 模块级单例:同一时刻只会有一个下拉打开,故用一组全局监听广播给"当前打开的下拉",
// 而不是每个 ChipField 实例各注册一组 scroll(capture)/resize(4 个字段即 4 组)。
let openField = null;
function closeOpenDropdown() {
  if (openField) { openField.removeDropdown(); openField = null; }
}
window.addEventListener('scroll', closeOpenDropdown, true);
window.addEventListener('resize', closeOpenDropdown);

// ----------------------------- 编码芯片字段 --------------------------------

export class ChipField {
  static uid = 0;
  /**
   * @param {HTMLElement} boxEl  含 .chipbox 的容器
   * @param {'diagnosis'|'procedure'} type
   * @param {{single?:boolean, primary?:boolean}} [opts]
   *        single  = 只保留一个芯片(主要诊断/主要操作)
   *        primary = 该字段是「主要」诊断/手术。主要诊断不显示 MCC/CC 徽章 ——
   *                  CHS-DRG 的并发症分档只针对其他诊断,详见 dom.js 的 codeBadges。
   */
  constructor(boxEl, type, opts = {}) {
    this.box = boxEl;
    this.type = type;
    this.single = !!opts.single;
    this.primary = !!opts.primary;
    this.items = [];          // {code,sourceCode,name,blocked,valid,robot,comp,unmapped,unknown}
    this.matches = [];
    this.activeIdx = -1;
    // 待删除位:空输入框里按退格先「选中」末位芯片(armedIdx = 下标),再按一次才真删。
    // 一按即删的老行为在那条"刚把文字删干净"的路径上,一次误按就抹掉一整条诊断且不可撤销。
    this.armedIdx = -1;
    this.dropdown = null;
    this.uid = ++ChipField.uid;
    this.listId = 'cf-list-' + this.uid;
    this.guard = staleGuard(); // 检索请求守卫:丢弃过期响应,避免快速输入时结果错乱
    this.input = el('input', {
      type: 'text', class: 'chip-input', autocomplete: 'off',
      placeholder: this.single ? '录入编码或名称…' : '录入后回车添加…',
      role: 'combobox', 'aria-autocomplete': 'list', 'aria-haspopup': 'listbox',
      'aria-expanded': 'false',
      // aria-controls 不在此处声明:下拉尚未创建时指向不存在的 id 属"断链",
      // 改由 renderDropdown 挂载、removeDropdown 摘除(见下)。
    });
    boxEl.appendChild(this.input);
    this.bind();
  }

  bind() {
    const inp = this.input;
    // 防抖:连续输入只在停顿后发一次请求
    this.debouncedSearch = debounce(() => this.search(), DEBOUNCE_CHIP_MS);
    // 重新输入即撤掉待删除态(用户改主意了)
    inp.addEventListener('input', () => { this.disarmDelete(); this.debouncedSearch(); });
    inp.addEventListener('focus', () => { if (inp.value.trim()) this.search(); });
    inp.addEventListener('keydown', e => this.onKey(e));
    // 失焦延迟处理:与关闭下拉同一个定时器。必须延迟 —— 若在 blur 里同步
    // disarmDelete() → render() 会重建芯片节点,把 mousedown 命中的那个「×」按钮
    // 从 DOM 里摘掉,紧随其后的 click 就落不到它身上,点 × 删除会失效。
    // 延迟到 120ms 后,× 的 removeAt 早已把 armedIdx 清零,disarmDelete 自然空跑。
    inp.addEventListener('blur', () => setTimeout(() => {
      this.closeDropdown();
      if (document.activeElement !== inp) this.disarmDelete();
    }, 120));
    this.box.addEventListener('click', e => {
      if (e.target === this.box || e.target.closest('.chip')) this.input.focus();
    });
    // 下拉为 fixed 定位,页面滚动/尺寸变化时关闭(由模块级监听广播,见文件顶部)
  }

  onKey(e) {
    if (e.key === 'Enter') {
      e.preventDefault();
      if (this.dropdown && this.activeIdx >= 0 && this.matches[this.activeIdx]) this.pick(this.matches[this.activeIdx]);
      else this.commit(this.input.value);
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (this.matches.length) this.highlight(Math.min(this.activeIdx + 1, this.matches.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (this.matches.length) this.highlight(Math.max(this.activeIdx - 1, 0));
    } else if (e.key === 'Escape') {
      this.disarmDelete();
      this.closeDropdown();
    } else if (e.key === 'Backspace' && this.input.value === '') {
      // 输入框已空:退格是两段式 —— 第一次只把末位芯片置为「待删除」,
      // 第二次才真的删。原实现一按即删,编码员把文字退干净后再按一次,
      // 整条诊断就没了(且没有撤销),代价远大于省下的那一次按键。
      e.preventDefault();
      if (!this.items.length) return;
      if (this.armedIdx >= 0) this.removeAt(this.armedIdx);
      else this.armDelete(this.items.length - 1);
    }
  }

  /** 把第 idx 个芯片置为「待删除」:加环 + 播报,不删任何东西。 */
  armDelete(idx) {
    if (idx < 0 || idx >= this.items.length) return this.disarmDelete();
    if (this.armedIdx === idx) return;
    this.armedIdx = idx;
    this.render();
    const c = this.items[idx];
    announce(`已选中 ${c.sourceCode || c.code}，再按一次退格删除`);
  }

  /** 撤掉待删除态(重新输入 / 失焦 / 已删除 / 清空时调用)。
      刻意不做"删完自动选中前一个":一次按键只做一件事,连按不会一路剃掉整排芯片。 */
  disarmDelete() {
    if (this.armedIdx === -1) return this;
    this.armedIdx = -1;
    this.render();
    return this;
  }

  async search() {
    const q = this.input.value.trim();
    if (!q) return this.closeDropdown();
    const seq = this.guard.next();
    try {
      const data = await api.get('/api/search', { type: this.type, q, limit: 8, version: currentCodeVersion() });
      if (!this.guard.is(seq)) return;         // 已有更新的输入,丢弃本次过期响应
      this.matches = data.items || [];
      this.activeIdx = -1;
      this.renderDropdown();
    } catch {
      if (this.guard.is(seq)) this.closeDropdown();
    }
  }

  renderDropdown() {
    // 只移除 DOM,绝不能调用 closeDropdown():那会清空 this.matches,导致永远渲染不出内容
    this.removeDropdown();
    const rect = this.input.getBoundingClientRect();
    const dd = el('div', { class: 'dropdown', id: this.listId, role: 'listbox' });
    dd.style.left = rect.left + 'px';
    dd.style.top = (rect.bottom + 4) + 'px';
    dd.style.width = Math.max(rect.width, 300) + 'px';

    if (!this.matches.length) {                // 明确告知"检索过但无结果",而不是静默不弹
      dd.appendChild(el('div', { class: 'dd-empty', text: '未找到匹配的编码或名称' }));
    } else {
      this.matches.forEach((m, i) => {
        const flags = codeBadges(m, this.type, { primary: this.primary }).map(badgeEl);
        dd.appendChild(el('div', {
          class: 'dd-item' + (i === this.activeIdx ? ' is-hl' : ''),
          id: this.optId(i),
          role: 'option',
          'aria-selected': String(i === this.activeIdx),
          onmousedown: e => { e.preventDefault(); this.pick(m); },
          onmouseenter: () => this.highlight(i),
        }, [
          // 是否展示「国临码 → 医保码」与结果区 mapDisplayVisible 同一口径(编码版本):
          // 候选列表是"选之前"的辅助,显示转换目标是选码的依据;芯片是"选之后"的回读,保持干净。
          mapDisplayVisible() && m.mappedTo
            ? el('span', { class: 'dd-code-wrap' }, [
                el('b', { text: m.code, title: '国临版原始编码' }),
                el('span', { class: 'dd-code-arrow', text: ' → ' }),
                el('b', { text: m.mappedTo, title: '医保版标准编码' }),
              ])
            : el('b', { text: m.code }),
          el('span', { class: 'dd-name', text: mapDisplayVisible() ? (m.mappedToName || m.name) : m.name }),
          ...flags,
        ]));
      });
    }
    document.body.appendChild(dd);
    this.dropdown = dd;
    openField = this;                                        // 登记为"当前打开的下拉",供全局监听广播
    this.input.setAttribute('aria-expanded', 'true');
    this.input.setAttribute('aria-controls', this.listId);   // 下拉此刻才存在,断链消除
  }

  /** 只移除下拉 DOM,保留 matches / activeIdx 状态。 */
  removeDropdown() {
    if (this.dropdown) { this.dropdown.remove(); this.dropdown = null; }
    if (openField === this) openField = null;
    this.input.setAttribute('aria-expanded', 'false');
    this.input.removeAttribute('aria-controls');             // 目标已移除,同步摘除引用
  }

  /** 关闭下拉并清空检索状态。 */
  closeDropdown() {
    this.removeDropdown();
    this.matches = [];
    this.activeIdx = -1;
    this.input.removeAttribute('aria-activedescendant');
  }

  optId(i) { return 'cf-opt-' + this.uid + '-' + i; }

  /** 仅切换高亮项,不重建 DOM(避免闪烁与状态丢失)。 */
  highlight(idx) {
    this.activeIdx = idx;
    if (!this.dropdown) return;
    const nodes = $$('.dd-item', this.dropdown);
    nodes.forEach((n, i) => {
      const on = i === idx;
      n.classList.toggle('is-hl', on);
      n.setAttribute('aria-selected', String(on));
    });
    const cur = nodes[idx];
    if (cur && cur.scrollIntoView) cur.scrollIntoView({ block: 'nearest' });
    this.input.setAttribute('aria-activedescendant', cur ? this.optId(idx) : '');
  }

  async pick(m) {
    this.debouncedSearch.cancel();   // 丢弃待发的检索,避免选中后又被弹回
    this.input.value = '';
    this.closeDropdown();
    await this.addResolved(m);
  }

  /** 直接录入(回车未选下拉):先尝试精确查码,失败则记为未识别芯片。 */
  async commit(raw) {
    const text = raw.trim();
    if (!text) return;
    this.debouncedSearch.cancel();
    this.input.value = '';
    this.closeDropdown();
    try {
      const entry = await api.get('/api/lookup', { type: this.type, code: text, version: currentCodeVersion() });
      // lookup 未命中返回 200 空 body,readJson 兜底为 {};必须校验 code,否则空对象会渲染成无文本芯片
      if (entry && entry.code) { await this.addResolved(entry); return; }
    } catch (e) {
      // 服务端 5xx/网络故障 ≠ "码不存在":恢复输入并提示重试,不静默落"未识别"芯片误导录入人
      if (!e.status || e.status >= 500) {
        this.input.value = text;
        this.showError(e.message || '检索失败，请稍后重试');
        return;
      }
      // 4xx 客户端错误:码仍按未识别落芯片(与手工录入语义一致),但失败原因可见
      this.showError(`“${text}”检索失败：${e.message}`);
    }
    await this.addResolved({ code: text.toUpperCase(), name: '（未识别）', blocked: false, valid: null, unknown: true });
  }

  /** 芯片域瞬时错误(检索失败等):4s 自动消失,与"未识别"芯片(码表查无此码)严格区分。 */
  showError(msg) {
    if (!this.errEl) {
      this.errEl = el('p', { class: 'form-error', role: 'alert' });
      this.box.insertAdjacentElement('afterend', this.errEl);
    }
    this.errEl.textContent = msg;
    this.errEl.hidden = false;
    clearTimeout(this.errTimer);
    this.errTimer = setTimeout(() => { this.errEl.hidden = true; }, 4000);
  }

  clearError() {
    clearTimeout(this.errTimer);
    if (this.errEl) this.errEl.hidden = true;
  }

  async addResolved(item) {
    this.armedIdx = -1;   // 新码落位即撤掉待删除态(所有入口:下拉选中/HIS 提取/批量回填)
    // sourceCode 恒为「所选版本的原始录入码」:国临版模式下提交它、由引擎转医保版;
    // 医保版模式下没有映射,两者相同。code 只是这一条会落到哪个口径的展示值。
    const chip = {
      code: item.mappedTo || item.code,
      sourceCode: item.code,
      name: item.mappedToName || item.name || '',
      blocked: !!item.blocked,
      valid: item.valid ?? null,
      robot: !!item.robot,
      comp: item.comp || null,
      unmapped: !!item.unmapped,
      unknown: !!item.unknown,
    };
    if (!chip.unknown) this.clearError(); // 码成功落位即视为故障已恢复,撤掉瞬时错误条
    if (this.single) {
      this.items = [chip];
    } else {
      if (this.items.some(c => c.code === chip.code)) return; // 去重
      this.items.push(chip);
    }
    this.render();
  }

  /** 批量回填唯一入口:原始码 → /api/lookup 解析 → addResolved。
      HIS 提取 / 示例病例一律走这里,与手工下拉、手工回车共用同一份 DictEntry,
      徽标(灰码/有效/机器人/MCC-CC/无医保对应/未识别)不再因入口不同而漂移。
      未命中码表时保留来源名称并标未识别,与手工录入未识别码同款表现。 */
  async addCodes(list) {
    const version = currentCodeVersion();
    const items = await Promise.all((list || []).map(async x => {
      // 一律按「所选版本的原始码」查:国临版模式下它可能是国临码,由 lookup 带回映射目标
      const raw = String(x.icd || x.code || '').trim();
      if (!raw) return null;
      const hint = x.name2 || x.name || '';
      try {
        const e = await api.get('/api/lookup', { type: this.type, code: raw, version });
        if (e && e.code) return { ...e, name: e.name || hint };
      } catch { /* 网络/解析失败:降级为来源条目,不阻断回填 */ }
      return { code: raw, name: hint || '（未识别）', blocked: false, valid: null, robot: false, unknown: true };
    }));
    for (const it of items) if (it) await this.addResolved(it);
    return this;
  }

  removeAt(i) {
    this.items.splice(i, 1);
    this.armedIdx = -1;   // 一次按键只做一件事:删完不自动续选前一个
    this.render();
  }

  render() {
    // 记录本次重绘前已在场的芯片:它们不该重放"入场"动画(开关只改显示口径,不是新录入)
    const present = new Set($$('.chip', this.box).map(c => c.dataset.code));
    $$('.chip', this.box).forEach(c => c.remove());
    this.items.forEach((c, i) => {
      // 全部徽标:灰码+机器人辅助可并存;主要诊断位置上不给 MCC/CC(见 codeBadges)
      const badges = codeBadges(c, this.type, { primary: this.primary }).map(badgeEl);
      const id = c.sourceCode || c.code;
      const chip = el('span', {
        class: 'chip' + (c.unknown ? ' is-unknown' : '')
          + (present.has(id) ? ' no-anim' : '')
          + (i === this.armedIdx ? ' is-armed' : ''),   // 待删除态:退格键第一次只选中它
        'data-code': id,
      }, [
        // 芯片只显示「所选版本的码」,不显示映射箭头:
        // 输入区要保持"我录的是什么"的忠实回读,转换结果属于结果卡的编码标准化面板。
        // 悬停补全为「原码 名称」,国临版转码后的目标码也能看到。
        el('span', { class: 'chip-code' + (c.unmapped ? ' is-unmapped' : ''), text: id }),
        c.name ? el('span', {
          class: 'chip-name',
          // 悬停给出"这一条落到哪个医保码"的完整信息,芯片本体保持只有原码+名称
          title: id + ' ' + c.name + (c.code !== id ? `（医保版 ${c.code}）` : ''),
          text: c.name,
        }) : null,
        ...badges,
        el('button', { type: 'button', class: 'chip-x', text: '×', 'aria-label': '移除', onclick: () => this.removeAt(i) }),
      ]);
      this.box.insertBefore(chip, this.input);
    });
  }

  getCodes() { return this.items.map(c => c.sourceCode || c.code); }
  clear() { this.items = []; this.armedIdx = -1; this.render(); }
}
