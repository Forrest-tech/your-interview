import { expect, test } from '@playwright/test';
import { apiLogin, createEntry, deleteEntry, seedAuth } from './helpers';

/**
 * E2E-1(票 89 首个回归用例,场景取自票 80)。
 *
 * 场景:造一个**无轮次、无 Result** 的条目(用户实测的 Randstad 同形态),
 *       打开详情页,断言头部徽章与左侧分组归属一致。
 *
 * 为什么这个场景当第一个:票 80 票面已把它登记为"永久回归任务",而且它验证的是
 * **DOM 呈现**——curl 打 API 永远验不到"徽章上写的是什么字、条目落在哪一组"。
 */
const COMPANY = 'RandstadE2E80';
const ROLE = '票80 E2E 无终局条目';

test.describe('票80 · 详情页头部徽章与左侧分组一致', () => {
  test('无轮次无 Result 的条目:徽章不许被贴「已完成」,且必须落在 In Progress 组', async ({
    page,
    request,
  }) => {
    const auth = await apiLogin(request);
    const entryId = await createEntry(request, auth.token, {
      companyName: COMPANY,
      role: ROLE,
    });

    try {
      await seedAuth(page, auth);
      await page.goto(`/playbook/${entryId}`);

      // ---- 头部徽章 ----
      const badge = page.locator('.status-badge');
      await expect(badge).toBeVisible({ timeout: 20_000 });
      const text = ((await badge.textContent()) ?? '').trim();

      // 反向断言(票80 缺陷形态):无终局条目被默认贴「已完成」
      expect(text, `徽章文案 = 「${text}」`).not.toBe('已完成');
      expect(text, `徽章文案 = 「${text}」`).not.toBe('Completed');
      expect(text, `徽章文案 = 「${text}」`).not.toBe('Terminé');
      // 正向断言:应是「进行中」的三语文案之一
      expect(['进行中', 'In progress', 'En cours'], `徽章文案 = 「${text}」`).toContain(text);

      // ---- 与左侧分组一致 ----
      // 左侧第一个 nav-group 是 In Progress(Active)组;当前浏览的条目应带 .on 高亮落在其中
      const activeGroup = page.locator('[data-testid="entry-nav"] .nav-group').first();
      const currentInActive = activeGroup.locator('.nav-item.on');
      await expect(
        currentInActive,
        `当前条目应高亮落在 In Progress 组,实际徽章文案为「${text}」`
      ).toHaveCount(1);
      await expect(currentInActive).toContainText(COMPANY);
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
