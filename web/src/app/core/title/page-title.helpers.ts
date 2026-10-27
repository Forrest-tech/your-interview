/**
 * 页签标题(REQ-12)的纯函数层:URL → i18n 键映射与标题拼装。
 *
 * 口径(票44 定稿):
 * - 格式:「模块名 · Your Interview」;无匹配路由/根路径回退裸「Your Interview」。
 * - 模块名一律复用既有三语 i18n 键(与界面可见文案逐字一致),本文件只存键不存文案。
 * - 匹配规则:先剥 query/hash,再按「路径段边界 + 最长前缀」匹配
 *   (/playbook/calendar 必须压过 /playbook;/playbookxyz 不算命中 /playbook)。
 */

export const PRODUCT_NAME = 'Your Interview';

/** 路由前缀 → i18n 键。顺序无关,命中取最长前缀。 */
export const ROUTE_TITLE_KEYS: ReadonlyArray<readonly [prefix: string, key: string]> = [
  ['/playbook/calendar', 'pb.cal.title'],
  ['/settings/voice', 'setting.voiceTitle'],
  ['/settings/llm', 'ai.pageTitle'],
  ['/profile', 'account.profile'],
  ['/settings', 'settings.title'],
  ['/dashboard', 'nav.dashboard'],
  ['/tracker', 'nav.tracker'],
  ['/playbook', 'nav.playbook'],
  ['/tech-stack', 'nav.techstack'],
  ['/practice', 'nav.practice'],
  ['/mock', 'nav.mock'],
  ['/analytics', 'nav.analytics'],
  ['/admin', 'nav.admin'],
  ['/resume', 'nav.resume'],
  ['/terms', 'shell.terms'],
  ['/privacy', 'shell.privacy'],
  ['/copyright', 'shell.copyright'],
  ['/forbidden', 'page.forbidden'],
  ['/login', 'account.login']
];

/** URL → 标题 i18n 键;无匹配(含根路径/空值)返回 null,表示用裸产品名。 */
export function titleKeyForUrl(url: string | null | undefined): string | null {
  if (!url) return null;
  const path = url.split(/[?#]/)[0];
  let bestKey: string | null = null;
  let bestLen = -1;
  for (const [prefix, key] of ROUTE_TITLE_KEYS) {
    const hit = path === prefix || path.startsWith(prefix + '/');
    if (hit && prefix.length > bestLen) {
      bestKey = key;
      bestLen = prefix.length;
    }
  }
  return bestKey;
}

/** 拼装最终标题:模块名为空/纯空白时回退裸产品名。 */
export function formatPageTitle(moduleName: string | null | undefined): string {
  const name = (moduleName ?? '').trim();
  return name ? `${name} · ${PRODUCT_NAME}` : PRODUCT_NAME;
}
