// ---- 标签页导航(叶子模块:只依赖 dom,不 import 任何业务域) ----
//
// 存在意义:打断 ES Module 循环依赖。原先 switchTab 住在 main.js,而 data.js 需要它
// → data.js → main.js;同时 main.js 需要 data.js 的 ensureDataLoaded → main.js → data.js,
// 两侧互相 import 成环。把 switchTab 下沉到这里后,依赖方向变为 main/data/his → nav → dom,无环。
//
// 「进入某页才加载其数据」原先由 switchTab 直接调 ensureDataLoaded 实现(那正是成环的那条边),
// 现改为数据一览页自己的模块用 onDataTabEnter 注册钩子 —— 导航层不再反向依赖业务域。
import { $, $$, setHidden } from './dom.js';

export const TAB_VIEWS = ['single', 'batch', 'data'];

/** 「进入数据一览页」的加载钩子。当前只有这一页需要(进页才拉数据),故用单槽而非订阅表:
    等真的有第二页要注册,再改成 map 也不迟。 */
let dataEnterHook = null;

export function onDataTabEnter(fn) { dataEnterHook = fn; }

export function switchTab(name) {
  TAB_VIEWS.forEach(v => {
    const view = $('#view-' + v);
    const on = v === name;
    view.classList.toggle('is-active', on);
    setHidden(view, !on);
  });
  $$('.tab').forEach(t => {
    const on = t.dataset.tab === name;
    t.classList.toggle('is-active', on);
    t.setAttribute('aria-selected', on);
    t.setAttribute('tabindex', on ? '0' : '-1');
  });
  if (name === 'data') dataEnterHook?.();
}
