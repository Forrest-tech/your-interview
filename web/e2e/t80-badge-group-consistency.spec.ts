import { expect, test } from '@playwright/test';
import { apiLogin, createEntry, deleteEntry, seedAuth } from './helpers';

/**
 * E2E-1(票 89 首个回归用例,场景取自票 80)。
 *
 * 场景:造一个**无轮次、无 Result** 的条目(用户实测的 Randstad 同形态),
 *       打开详情页,断言左侧分组归属。
 *
 * 票 178 变更:用户令详情页头状态胶囊整个藏掉(先隐藏不删除,模板层不渲染),
 *       故原“头部徽章文案”断言退役,改为藏起契约断言(徽章不存在);
 *       条目状态归属仍由左侧分组断言覆盖,左导航本身一字未动。
 *       绝不许为修测试把胶囊放回来。
 */
const COMPANY = 'RandstadE2E80';
const ROLE = '票80 E2E 无终局条目';

test.describe('票80 · 详情页头部徽章藏起与左侧分组一致', () => {
  test('无轮次无 Result 的条目:页头徽章不渲染，且必须落在 In Progress 组', async ({
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

      // ---- 头部徽章（票 178：已藏起） ----
      // 藏起是模板层不渲染，故断言“不存在”而非“不可见”。
      await expect(page.locator('.status-badge')).toHaveCount(0);

      // ---- 与左侧分组一致 ----
      // 左侧第一个 nav-group 是 In Progress(Active)组;当前浏览的条目应带 .on 高亮落在其中
      const activeGroup = page.locator('[data-testid="entry-nav"] .nav-group').first();
      const currentInActive = activeGroup.locator('.nav-item.on');
      await expect(
        currentInActive,
        `当前条目应高亮落在 In Progress 组`
      ).toHaveCount(1);
      await expect(currentInActive).toContainText(COMPANY);
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
