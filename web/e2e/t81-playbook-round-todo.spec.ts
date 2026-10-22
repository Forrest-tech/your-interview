import { expect, test } from '@playwright/test';
import {
  addRound,
  apiLogin,
  createEntry,
  deleteEntry,
  seedAuth,
} from './helpers';

/**
 * E2E-3:Playbook 条目 → 轮次 → 待办勾选 → 持久化(黄金路径)。
 *
 * 轮次经 API 预置(轮次的 UI 入口是"贴邮件→建轮次"的 intake 流,属于更重的
 * 独立路径);本用例聚焦 UI 侧的两件用户必做事:
 *   1. 轮次建好后详情页要真的显示出来(R1 步骤卡);
 *   2. 待办的「添加 → 勾选 → 刷新后仍是勾选状态」——勾选走 PATCH,
 *      票21 曾在这里修过吞错假成功,所以持久化必须用刷新后的服务端真值断言。
 */
const COMPANY = 'E2E3Co';
const ROLE = 'E2E3RoleX';
const TODO_TITLE = 'E2E3 待办:准备系统设计';

test.describe('票89 E2E-3 · Playbook 轮次与待办', () => {
  test('轮次在详情页可见;待办添加→勾选→刷新保持', async ({ page, request }) => {
    const auth = await apiLogin(request);
    const entryId = await createEntry(request, auth.token, {
      companyName: COMPANY,
      role: ROLE,
    });

    try {
      // 预置一轮(服务端 Order 自动递增)
      await addRound(request, auth.token, entryId, 'Technical');

      await seedAuth(page, auth);
      await page.goto(`/playbook/${entryId}`);

      // ---- 轮次在 UI 可见:R1 步骤卡 ----
      await expect(
        page.locator('.step', { hasText: 'R1' }).first(),
        '加轮次后详情页应出现 R1 步骤卡'
      ).toBeVisible({ timeout: 20_000 });

      // ---- 待办:添加 ----
      const addInput = page.locator('.action-add input.fld').first();
      await addInput.fill(TODO_TITLE);
      await addInput.press('Enter');
      const row = page.locator('.action-row', { hasText: TODO_TITLE }).first();
      await expect(row).toBeVisible({ timeout: 20_000 });

      // ---- 勾选 ----
      const box = row.locator('input[type="checkbox"]');
      await box.check();
      await expect(box).toBeChecked();

      // ---- 刷新后仍是勾选(服务端真值,不许假成功) ----
      await page.reload();
      const rowAfter = page.locator('.action-row', { hasText: TODO_TITLE }).first();
      await expect(rowAfter).toBeVisible({ timeout: 20_000 });
      await expect(
        rowAfter.locator('input[type="checkbox"]'),
        '刷新后勾选状态必须保持(走服务端真值)'
      ).toBeChecked();
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
