/**
 * 导出文档的中间模型 + 四种格式生成器(2026-10-20 详情页 v2)。
 * 面试实录与备考报告都先组装成 ExportDoc,再按格式生成真实文件下载:
 *   Markdown —— 纯文本组装;
 *   Word(.docx) —— 手写 OOXML + 存储式 ZIP(无第三方依赖);
 *   JPG 长图 —— Canvas 逐行排版后整幅导出;
 *   PDF —— 同一 Canvas 切成 A4 页,JPEG 页图嵌入手写 PDF。
 */

export type ExportBlock =
  | { kind: 'heading'; text: string }
  | { kind: 'subheading'; text: string }
  | { kind: 'para'; text: string }
  | { kind: 'qa'; speaker: string; text: string; timeLabel?: string }
  | { kind: 'analysis'; title: string; lines: string[] }
  | { kind: 'std'; label: string; text: string }
  | { kind: 'list'; items: string[] };

export interface ExportDoc {
  title: string;
  subtitle?: string;
  blocks: ExportBlock[];
}

export type ExportFormat = 'pdf' | 'docx' | 'jpg' | 'md';

export const FORMAT_EXT: Record<ExportFormat, string> = {
  pdf: '.pdf', docx: '.docx', jpg: '.jpg', md: '.md'
};

export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 4000);
}

// ---------------- Markdown ----------------

export function docToMarkdown(doc: ExportDoc): string {
  const out: string[] = [`# ${doc.title}`];
  if (doc.subtitle) out.push('', doc.subtitle);
  for (const b of doc.blocks) {
    out.push('');
    switch (b.kind) {
      case 'heading': out.push(`## ${b.text}`); break;
      case 'subheading': out.push(`### ${b.text}`); break;
      case 'para': out.push(b.text); break;
      case 'qa': out.push(`**${b.speaker}**${b.timeLabel ? ` · ${b.timeLabel}` : ''}:${b.text}`); break;
      case 'analysis':
        out.push(`**${b.title}**`);
        for (const l of b.lines) out.push(`- ${l}`);
        break;
      case 'std': out.push(`> **${b.label}**:${b.text}`); break;
      case 'list': for (const i of b.items) out.push(`- ${i}`); break;
    }
  }
  return out.join('\n') + '\n';
}

// ---------------- DOCX(手写 OOXML + ZIP) ----------------

function escXml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function paraXml(text: string, opts: { style?: string; bold?: boolean; size?: number } = {}): string {
  const pPr = [
    opts.style ? `<w:pStyle w:val="${opts.style}"/>` : '',
    '<w:spacing w:after="120"/>'
  ].join('');
  const rPr = [
    opts.bold ? '<w:b/>' : '',
    opts.size ? `<w:sz w:val="${opts.size}"/><w:szCs w:val="${opts.size}"/>` : ''
  ].join('');
  const runs = text.split('\n').map((line, i) =>
    `${i > 0 ? '<w:br/>' : ''}<w:t xml:space="preserve">${escXml(line)}</w:t>`).join('');
  return `<w:p><w:pPr>${pPr}</w:pPr><w:r><w:rPr>${rPr}</w:rPr>${runs}</w:r></w:p>`;
}

export function docToDocx(doc: ExportDoc): Blob {
  const ps: string[] = [paraXml(doc.title, { style: 'Title' })];
  if (doc.subtitle) ps.push(paraXml(doc.subtitle));
  for (const b of doc.blocks) {
    switch (b.kind) {
      case 'heading': ps.push(paraXml(b.text, { style: 'Heading1' })); break;
      case 'subheading': ps.push(paraXml(b.text, { style: 'Heading2' })); break;
      case 'para': ps.push(paraXml(b.text)); break;
      case 'qa': ps.push(paraXml(`${b.speaker}${b.timeLabel ? ` · ${b.timeLabel}` : ''}:${b.text}`)); break;
      case 'analysis':
        ps.push(paraXml(b.title, { bold: true }));
        for (const l of b.lines) ps.push(paraXml(`• ${l}`));
        break;
      case 'std': ps.push(paraXml(`${b.label}:${b.text}`)); break;
      case 'list': for (const i of b.items) ps.push(paraXml(`• ${i}`)); break;
    }
  }
  const documentXml =
    `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>` +
    `<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">` +
    `<w:body>${ps.join('')}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/>` +
    `<w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134"/></w:sectPr></w:body></w:document>`;
  const contentTypes =
    `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>` +
    `<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">` +
    `<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>` +
    `<Default Extension="xml" ContentType="application/xml"/>` +
    `<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>` +
    `<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>`;
  const rels =
    `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>` +
    `<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">` +
    `<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>`;
  const styles =
    `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>` +
    `<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">` +
    `<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/>` +
    `<w:rPr><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:style>` +
    `<w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/>` +
    `<w:rPr><w:b/><w:sz w:val="40"/><w:szCs w:val="40"/></w:rPr></w:style>` +
    `<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/>` +
    `<w:pPr><w:spacing w:before="240"/></w:pPr><w:rPr><w:b/><w:sz w:val="30"/><w:szCs w:val="30"/></w:rPr></w:style>` +
    `<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/>` +
    `<w:pPr><w:spacing w:before="200"/></w:pPr><w:rPr><w:b/><w:sz w:val="26"/><w:szCs w:val="26"/></w:rPr></w:style></w:styles>`;
  const enc = new TextEncoder();
  const zip = buildZip([
    { name: '[Content_Types].xml', data: enc.encode(contentTypes) },
    { name: '_rels/.rels', data: enc.encode(rels) },
    { name: 'word/document.xml', data: enc.encode(documentXml) },
    { name: 'word/styles.xml', data: enc.encode(styles) }
  ]);
  return new Blob([zip], {
    type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
  });
}

// ---------------- ZIP(存储式,无压缩) ----------------

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(data: Uint8Array): number {
  let c = 0xffffffff;
  for (let i = 0; i < data.length; i++) c = CRC_TABLE[(c ^ data[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

export function buildZip(files: { name: string; data: Uint8Array }[]): Uint8Array {
  const enc = new TextEncoder();
  const chunks: Uint8Array[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;
  const push = (arr: Uint8Array[], b: Uint8Array) => { arr.push(b); return b.length; };

  for (const f of files) {
    const nameBytes = enc.encode(f.name);
    const crc = crc32(f.data);
    const local = new Uint8Array(30 + nameBytes.length);
    const lv = new DataView(local.buffer);
    lv.setUint32(0, 0x04034b50, true);
    lv.setUint16(4, 20, true);
    lv.setUint16(6, 0x0800, true); // UTF-8 文件名
    lv.setUint16(8, 0, true);      // stored
    lv.setUint32(14, crc, true);
    lv.setUint32(18, f.data.length, true);
    lv.setUint32(22, f.data.length, true);
    lv.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    offset += push(chunks, local) + push(chunks, f.data);

    const cen = new Uint8Array(46 + nameBytes.length);
    const cv = new DataView(cen.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(4, 20, true);
    cv.setUint16(6, 20, true);
    cv.setUint16(8, 0x0800, true);
    cv.setUint16(10, 0, true);
    cv.setUint32(16, crc, true);
    cv.setUint32(20, f.data.length, true);
    cv.setUint32(24, f.data.length, true);
    cv.setUint16(28, nameBytes.length, true);
    cv.setUint32(42, offset - f.data.length - local.length, true);
    cen.set(nameBytes, 46);
    central.push(cen);
  }
  const centralSize = central.reduce((a, c) => a + c.length, 0);
  const end = new Uint8Array(22);
  const ev = new DataView(end.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, files.length, true);
  ev.setUint16(10, files.length, true);
  ev.setUint32(12, centralSize, true);
  ev.setUint32(16, offset, true);
  chunks.push(...central, end);
  const total = chunks.reduce((a, c) => a + c.length, 0);
  const out = new Uint8Array(total);
  let p = 0;
  for (const c of chunks) { out.set(c, p); p += c.length; }
  return out;
}

// ---------------- Canvas 排版(JPG / PDF 共用) ----------------

interface Line { text: string; size: number; bold: boolean; color: string; gapAfter: number; }

function docToLines(doc: ExportDoc): Line[] {
  const lines: Line[] = [
    { text: doc.title, size: 30, bold: true, color: '#111', gapAfter: 10 }
  ];
  if (doc.subtitle) lines.push({ text: doc.subtitle, size: 16, bold: false, color: '#555', gapAfter: 16 });
  const body: Line = { text: '', size: 17, bold: false, color: '#222', gapAfter: 8 };
  for (const b of doc.blocks) {
    switch (b.kind) {
      case 'heading': lines.push({ text: b.text, size: 22, bold: true, color: '#111', gapAfter: 8 }); break;
      case 'subheading': lines.push({ text: b.text, size: 19, bold: true, color: '#222', gapAfter: 6 }); break;
      case 'para': lines.push({ ...body, text: b.text }); break;
      case 'qa':
        lines.push({ text: `${b.speaker}${b.timeLabel ? ` · ${b.timeLabel}` : ''}`, size: 15, bold: true, color: '#3f51b5', gapAfter: 2 });
        lines.push({ ...body, text: b.text, gapAfter: 12 });
        break;
      case 'analysis':
        lines.push({ text: b.title, size: 16, bold: true, color: '#111', gapAfter: 4 });
        for (const l of b.lines) lines.push({ ...body, text: `• ${l}`, size: 16, gapAfter: 4 });
        lines.push({ ...body, text: '', gapAfter: 6 });
        break;
      case 'std':
        lines.push({ text: b.label, size: 15, bold: true, color: '#3f51b5', gapAfter: 2 });
        lines.push({ ...body, text: b.text, gapAfter: 12 });
        break;
      case 'list':
        for (const i of b.items) lines.push({ ...body, text: `• ${i}`, gapAfter: 4 });
        lines.push({ ...body, text: '', gapAfter: 6 });
        break;
    }
  }
  return lines;
}

function wrapText(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string[] {
  const out: string[] = [];
  for (const rawLine of text.split('\n')) {
    if (!rawLine) { out.push(''); continue; }
    let cur = '';
    for (const ch of rawLine) {
      const test = cur + ch;
      if (cur && ctx.measureText(test).width > maxWidth) { out.push(cur); cur = ch; }
      else cur = test;
    }
    out.push(cur);
  }
  return out;
}

export function renderDocCanvas(doc: ExportDoc, width = 900): HTMLCanvasElement {
  const pad = 44;
  const measure = document.createElement('canvas').getContext('2d')!;
  const laid: { text: string; size: number; bold: boolean; color: string; y: number }[] = [];
  let y = pad;
  for (const line of docToLines(doc)) {
    measure.font = `${line.bold ? '700 ' : ''}${line.size}px "PingFang SC","Microsoft YaHei","Noto Sans CJK SC",sans-serif`;
    const wrapped = wrapText(measure, line.text, width - pad * 2);
    for (const w of wrapped) {
      laid.push({ text: w, size: line.size, bold: line.bold, color: line.color, y: y + line.size });
      y += Math.round(line.size * 1.5);
    }
    y += line.gapAfter;
  }
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = y + pad;
  const ctx = canvas.getContext('2d')!;
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  for (const l of laid) {
    ctx.font = `${l.bold ? '700 ' : ''}${l.size}px "PingFang SC","Microsoft YaHei","Noto Sans CJK SC",sans-serif`;
    ctx.fillStyle = l.color;
    ctx.fillText(l.text, pad, l.y);
  }
  return canvas;
}

export function docToJpg(doc: ExportDoc): Promise<Blob> {
  const canvas = renderDocCanvas(doc);
  return new Promise((resolve, reject) =>
    canvas.toBlob(b => b ? resolve(b) : reject(new Error('图片生成失败')), 'image/jpeg', 0.92));
}

// ---------------- PDF(手写,JPEG 页图嵌入) ----------------

export function docToPdf(doc: ExportDoc): Promise<Blob> {
  const canvas = renderDocCanvas(doc, 1240);
  const pageH = 1754; // A4 @150dpi
  const pageW = 1240;
  const slices: { canvas: HTMLCanvasElement; height: number }[] = [];
  for (let y0 = 0; y0 < canvas.height; y0 += pageH) {
    const h = Math.min(pageH, canvas.height - y0);
    const pc = document.createElement('canvas');
    pc.width = pageW; pc.height = h;
    const pctx = pc.getContext('2d')!;
    pctx.fillStyle = '#ffffff'; pctx.fillRect(0, 0, pageW, h);
    pctx.drawImage(canvas, 0, y0, pageW, h, 0, 0, pageW, h);
    slices.push({ canvas: pc, height: h });
  }
  return Promise.all(slices.map(s => new Promise<Blob>((res, rej) =>
    s.canvas.toBlob(b => b ? res(b) : rej(new Error('PDF 页生成失败')), 'image/jpeg', 0.9))))
    .then(async blobs => {
      const bufs = await Promise.all(blobs.map(b => b.arrayBuffer().then(a => new Uint8Array(a))));
      return new Blob([buildPdf(bufs, slices.map(s => s.height), pageW, pageH)],
        { type: 'application/pdf' });
    });
}

function buildPdf(jpegs: Uint8Array[], heights: number[], pageW: number, pageH: number): Uint8Array {
  const enc = new TextEncoder();
  const parts: Uint8Array[] = [];
  const offsets: number[] = [];
  let pos = 0;
  const push = (b: Uint8Array) => { parts.push(b); pos += b.length; };
  const pushStr = (s: string) => push(enc.encode(s));

  pushStr('%PDF-1.4\n');
  // 对象编号:1=Catalog 2=Pages 其后每页 3 个对象(Page/Contents/Image)
  const pageObjNums: number[] = [];
  for (let i = 0; i < jpegs.length; i++) pageObjNums.push(3 + i * 3);

  offsets[1] = pos;
  pushStr('1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n');
  offsets[2] = pos;
  pushStr(`2 0 obj\n<< /Type /Pages /Kids [${pageObjNums.map(n => `${n} 0 R`).join(' ')}] /Count ${jpegs.length} >>\nendobj\n`);

  const ptW = 595.28, ptH = 841.89; // A4 points
  jpegs.forEach((jpg, i) => {
    const pageNum = 3 + i * 3, contentNum = pageNum + 1, imgNum = pageNum + 2;
    offsets[pageNum] = pos;
    pushStr(`${pageNum} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${ptW} ${ptH}] ` +
      `/Resources << /XObject << /Im${i} ${imgNum} 0 R >> >> /Contents ${contentNum} 0 R >>\nendobj\n`);
    const imgHpt = ptH * (heights[i] / pageH); // 末页不足整页时按实际高度,顶部对齐
    const yOff = ptH - imgHpt;
    const content = `q\n${ptW} 0 0 ${imgHpt.toFixed(2)} 0 ${yOff.toFixed(2)} cm\n/Im${i} Do\nQ\n`;
    offsets[contentNum] = pos;
    pushStr(`${contentNum} 0 obj\n<< /Length ${content.length} >>\nstream\n${content}endstream\nendobj\n`);
    offsets[imgNum] = pos;
    push(enc.encode(`${imgNum} 0 obj\n<< /Type /XObject /Subtype /Image /Width ${pageW} /Height ${heights[i]} ` +
      `/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpg.length} >>\nstream\n`));
    push(jpg);
    pushStr('\nendstream\nendobj\n');
  });

  const xrefPos = pos;
  const maxObj = 2 + jpegs.length * 3;
  let xref = `xref\n0 ${maxObj + 1}\n0000000000 65535 f \n`;
  for (let n = 1; n <= maxObj; n++) {
    xref += `${String(offsets[n] ?? 0).padStart(10, '0')} 00000 n \n`;
  }
  pushStr(xref);
  pushStr(`trailer\n<< /Size ${maxObj + 1} /Root 1 0 R >>\nstartxref\n${xrefPos}\n%%EOF`);
  const total = parts.reduce((a, p) => a + p.length, 0);
  const out = new Uint8Array(total);
  let p = 0;
  for (const part of parts) { out.set(part, p); p += part.length; }
  return out;
}

export function generateExport(format: ExportFormat, doc: ExportDoc): Promise<Blob> {
  switch (format) {
    case 'md': return Promise.resolve(new Blob([docToMarkdown(doc)], { type: 'text/markdown;charset=utf-8' }));
    case 'docx': return Promise.resolve(docToDocx(doc));
    case 'jpg': return docToJpg(doc);
    case 'pdf': return docToPdf(doc);
  }
}
