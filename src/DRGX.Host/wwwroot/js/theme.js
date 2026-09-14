// ---- 域间依赖(拆分脚本自动生成) ----
import { $ } from './dom.js';

// -------------------------------- 主题切换 --------------------------------
// 深浅主题由 html[data-theme] 驱动:localStorage 手动选择优先,未选择时跟随系统
// 色彩方案(prefers-color-scheme)。index.html 头部内联脚本在样式表前预置,
// 此处负责系统变化跟随与按钮切换。

export const THEME_KEY = 'drg-theme';

export function applyTheme(t) {
  document.documentElement.dataset.theme = t;
  const btn = $('#themeToggle');
  if (btn) {
    const dark = t === 'dark';
    btn.setAttribute('aria-label', dark ? '切换浅色主题' : '切换深色主题');
    btn.title = dark ? '当前深色,点击切换浅色' : '当前浅色,点击切换深色';
  }
}

export function systemTheme() {
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function storedTheme() {
  try {
    const t = localStorage.getItem(THEME_KEY);
    return t === 'dark' || t === 'light' ? t : null;
  } catch { return null; }
}

export function initTheme() {
  applyTheme(storedTheme() || systemTheme());
  // 用户未手动选择时跟随系统方案变化
  if (window.matchMedia) {
    try {
      matchMedia('(prefers-color-scheme: dark)').addEventListener('change', e => {
        if (!storedTheme()) applyTheme(e.matches ? 'dark' : 'light');
      });
    } catch { /* 旧实现仅支持 addListener,静默降级为只在启动时取值 */ }
  }
  const btn = $('#themeToggle');
  if (btn) btn.addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem(THEME_KEY, next); } catch { /* 同上,仅当次会话生效 */ }
    applyTheme(next);
  });
}
