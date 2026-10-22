import { expect, test } from '@playwright/test';
import { apiLogin, seedAuth } from './helpers';

/**
 * E2E-5:OCR 入口成卡(黄金路径)。
 *
 * ⚠️ 显式 fixme(票 89 验收要求:不许删用例、不许假绿,没验就是没验):
 *
 * 为什么 fixme 而不是 skip:
 *   - OCR 服务在本环境**根本没有落地**:网关 Clusters 里的 ocr 集群指向
 *     127.0.0.1:5300,那里没有任何服务在跑(已实测,见票 94);
 *   - 智能粘贴的图片路径当前是**诚实 Failed**(未接 OCR,UI 回落手动录入);
 *   - 修复责任在票 92(OCR 金标准回归集 + 三个入口接全共享管线)。
 * 票 92 落地后:去掉 fixme,本用例应直接转绿 —— 用例体已经写好,不是占位符。
 *
 * 届时的验收断言(不是现在跑):
 *   1. 图片粘贴不再报"未支持",而是真实解析;
 *   2. 解析出的公司/职位回填到可编辑字段;
 *   3. 保存成卡,列表可见 —— 与 E2E-2 同口径。
 */
const ROLE_FROM_OCR = 'E2E5OcrRoleX';

test.fixme('OCR 截图粘贴:上传 → 解析 → 字段回填 → 保存成卡', async ({
  page,
  request,
}) => {
  const auth = await apiLogin(request);

  await seedAuth(page, auth);
  await page.goto('/tracker');
  await page.locator('.smart-btn').first().click();

  const dialog = page.locator('mat-dialog-container');
  // 图片入口:把截图文件放进粘贴区(智能粘贴对话框监听 paste 事件接收图片)
  const png = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
    'base64'
  );
  await dialog.locator('textarea[matInput]').first().evaluate(async (el, bytes) => {
    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array(bytes as number[])], 'jd.png', { type: 'image/png' }));
    const evt = new ClipboardEvent('paste', { clipboardData: dt });
    (el as HTMLElement).dispatchEvent(evt);
  }, Array.from(png));

  // 票92 落地后,这里应解析出职位并回填(而非提示"图片暂未支持")
  const role = dialog.locator('input[name="jdRole"]');
  await expect(role, 'OCR 解析应把截图里的职位回填到可编辑字段').toHaveValue(
    /.+/,
    { timeout: 30_000 }
  );
  await role.fill(ROLE_FROM_OCR);
  await dialog
    .locator('button', { hasText: /保存为投递记录|Save as application/ })
    .first()
    .click();
  await expect(page.getByText(ROLE_FROM_OCR).first()).toBeVisible({ timeout: 30_000 });
});
