/**
 * Resume 页保存前的**纯决策函数**(FUN-10/票37)。
 *
 * 从 ResumeComponent 抽出:「这次保存该不该拦」是无副作用的判定逻辑,
 * 单独成文件便于单测(口径同票14 tracker-preferences.helpers;前端 Jest
 * 基建接入前以纯函数形态保证逻辑可独立验证,组件只做 IO 与提示)。
 */

/** 简历文本是否为空:null/undefined/空串/纯空白(空格、换行、制表符)都算空。 */
export function isResumeTextEmpty(text: string | null | undefined): boolean {
  return !text || !text.trim();
}

/**
 * 保存拦截判定:返回 'empty-text' 表示应当前端拦截并提示,其余返回 null 放行。
 *
 * 口径:
 *  - 文本为空且没有待上传文件 → 拦截。后端对空文本必回 400(Resume.Empty),
 *    与其发一趟注定失败的请求再弹黑话,不如保存前就用人话拦下(AUDIT FUN-10)。
 *  - 有待上传文件(pendingPdf)时放行:文件简历路径靠保存动作把 PDF 送上后端,
 *    提取文字失败的扫描版 PDF 文本可以为空,不能被文本守卫误拦(文件路径回归面)。
 */
export function resumeSaveBlock(
  text: string | null | undefined,
  hasPendingFile: boolean
): 'empty-text' | null {
  if (hasPendingFile) return null;
  return isResumeTextEmpty(text) ? 'empty-text' : null;
}
