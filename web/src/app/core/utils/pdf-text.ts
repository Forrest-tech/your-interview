/**
 * 从 PDF 文件提取纯文本(pdf.js,客户端)。
 *
 * 用途:简历 PDF 上传时提文本 —— 后端存 PDF 二进制供预览,
 * 文本供 Resume Score / 求职信生成(读文本不拖 PDF)。
 * 原来在 resume.component.ts 里私有,抽出来给 Documents tab 复用。
 */
export async function extractPdfText(file: File): Promise<string> {
  const pdfjs = await import('pdfjs-dist');
  // pdf.js v4+ 需要 worker:用本地打包的 assets,比 CDN 可靠
  const pdfjsAny = pdfjs as any;
  if (pdfjsAny.GlobalWorkerOptions && !pdfjsAny.GlobalWorkerOptions.workerSrc) {
    pdfjsAny.GlobalWorkerOptions.workerSrc = 'assets/pdf.worker.min.mjs';
  }
  const buf = await file.arrayBuffer();
  const pdf = await pdfjsAny.getDocument({ data: buf }).promise;
  const parts: string[] = [];
  for (let i = 1; i <= pdf.numPages; i++) {
    const page = await pdf.getPage(i);
    const content = await page.getTextContent();
    const line = (content.items as any[]).map((it: any) => it.str ?? '').join(' ');
    parts.push(line);
  }
  return parts.join('\n');
}
