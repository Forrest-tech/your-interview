import * as pdfjsLib from 'pdfjs-dist';
// @ts-ignore 副作用导入:在主线程注册 globalThis.pdfjsWorker,
// 让 pdf.js 直接用主线程 handler,不再创建 Web Worker
import 'pdfjs-dist/build/pdf.worker.js';

/**
 * 从 PDF 文件提取纯文本(pdf.js,客户端)。
 *
 * 用途:简历 PDF 上传时提文本 —— 后端存 PDF 二进制供预览,
 * 文本供 Resume Score / 求职信生成(读文本不拖 PDF)。
 * 原来在 resume.component.ts 里私有,抽出来给 Documents tab 复用。
 *
 * 注意:
 * - 用静态导入(不要 dynamic import),避免 bundler 双模块导致 worker 配置丢失
 * - pdf.js v3 兼容旧浏览器(Big Sur 时代);v6+ 用了新浏览器 API (toHex) 会炸
 * - worker 走主线程(fake worker):把 worker 代码直接 import 进主线程,
 *   pdf.js 检测到 globalThis.pdfjsWorker 后就不再创建 Web Worker,
 *   彻底避开 Worker 握手在 bundler 环境下失败的问题。简历就几页,主线程完全够用。
 */
const pdfjs: any = pdfjsLib as any;
if (pdfjs.GlobalWorkerOptions && !pdfjs.GlobalWorkerOptions.workerSrc) {
  pdfjs.GlobalWorkerOptions.workerSrc = '/assets/pdf.worker.min.js';
}

export async function extractPdfText(file: File): Promise<string> {
  const buf = await file.arrayBuffer();
  const pdf = await pdfjs.getDocument({ data: buf }).promise;
  const parts: string[] = [];
  for (let i = 1; i <= pdf.numPages; i++) {
    const page = await pdf.getPage(i);
    const content = await page.getTextContent();
    const line = (content.items as any[]).map((it: any) => it.str ?? '').join(' ');
    parts.push(line);
  }
  return parts.join('\n');
}

/** 供 PDF 内容渲染器复用的 pdf.js 实例。 */
export function getPdfjs(): any {
  return pdfjs;
}
