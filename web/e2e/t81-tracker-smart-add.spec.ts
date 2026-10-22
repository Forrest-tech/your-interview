import { expect, test } from '@playwright/test';
import {
  apiLogin,
  deleteApplication,
  findApplicationByRole,
  seedAuth,
} from './helpers';

/**
 * E2E-2:Tracker 建单(智能粘贴 + 手工回落)→ 编辑 → 列表可见(黄金路径)。
 *
 * 两条用例共用同一个智能粘贴对话框 —— 它就是实际 UX:
 *   - 有 JD 可贴 → 解析自动填表,保存前可逐项修改;
 *   - 没得解析/解析失败 → 字段照样渲染,手工填(票 6.2.2 的"保证手动回落"验收点)。
 */
const JD_TEXT = `Senior Backend Engineer

Company: Acme Corp
Salary: $140,000 - $180,000 per year
Location: Toronto, ON (Hybrid)

We are building the commerce platform of the future.`;

const PARSED_COMPANY = 'Acme Corp';
const EDITED_ROLE_A = 'E2E2ParsedRoleX';
const GARBAGE = 'asdkjhqweoinmxnasd 9812h3';
const MANUAL_COMPANY = 'ManualE2E2Co';
const EDITED_ROLE_B = 'E2E2ManualRoleX';

const SAVE_BTN = /保存为投递记录|Save as application|Enregistrer comme candidature/;
const PARSE_BTN = /解析|Parse|Analyser/;

test.describe('票89 E2E-2 · Tracker 智能粘贴建档', () => {
  test('智能粘贴:解析自动填表 → 保存前可改 → 保存 → 列表可见', async ({ page, request }) => {
    const auth = await apiLogin(request);
    try {
      await seedAuth(page, auth);
      await page.goto('/tracker');
      await page.locator('.smart-btn').first().click();

      const dialog = page.locator('mat-dialog-container');
      // 关掉 AI(沙盒无凭据,开着会静默等 30s 降级,拖慢且无意义)
      await dialog.locator('.ai-toggle input[type="checkbox"]').uncheck().catch(() => undefined);

      await dialog.locator('textarea[matInput]').first().fill(JD_TEXT);
      await dialog.locator('button', { hasText: PARSE_BTN }).first().click();

      // 解析结果自动填表
      const company = dialog.locator('input[name="jdCompany"]');
      await expect(company, '解析应自动填出公司').toHaveValue(PARSED_COMPANY, { timeout: 30_000 });

      // 保存前可编辑:改职位再存
      await dialog.locator('input[name="jdRole"]').fill(EDITED_ROLE_A);

      await dialog.locator('button', { hasText: SAVE_BTN }).first().click();
      await expect(
        page.getByText(PARSED_COMPANY).first(),
        '保存后列表应出现新投递'
      ).toBeVisible({ timeout: 30_000 });
    } finally {
      const app = await findApplicationByRole(request, auth.token, EDITED_ROLE_A);
      if (app) await deleteApplication(request, auth.token, app.id);
    }
  });

  test('手工回落:解析失败字段仍可手工填写并保存(不许静默丢)', async ({ page, request }) => {
    const auth = await apiLogin(request);
    try {
      await seedAuth(page, auth);
      await page.goto('/tracker');
      await page.locator('.smart-btn').first().click();

      const dialog = page.locator('mat-dialog-container');
      await dialog.locator('.ai-toggle input[type="checkbox"]').uncheck().catch(() => undefined);

      await dialog.locator('textarea[matInput]').first().fill(GARBAGE);
      await dialog.locator('button', { hasText: PARSE_BTN }).first().click();

      // 解析失败(或部分失败)后,字段必须仍然渲染且为空 —— 手动回落的核心保证
      const company = dialog.locator('input[name="jdCompany"]');
      await expect(company).toBeVisible({ timeout: 30_000 });
      await expect(company).toHaveValue('');

      await dialog.locator('input[name="jdCompany"]').fill(MANUAL_COMPANY);
      await dialog.locator('input[name="jdRole"]').fill(EDITED_ROLE_B);

      await dialog.locator('button', { hasText: SAVE_BTN }).first().click();
      await expect(
        page.getByText(MANUAL_COMPANY).first()
      ).toBeVisible({ timeout: 30_000 });
    } finally {
      const app = await findApplicationByRole(request, auth.token, EDITED_ROLE_B);
      if (app) await deleteApplication(request, auth.token, app.id);
    }
  });
});
