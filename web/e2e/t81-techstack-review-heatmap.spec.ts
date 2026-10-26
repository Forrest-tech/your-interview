import { expect, test } from '@playwright/test';
import {
  apiLogin,
  createKnowledgeItem,
  deleteKnowledgeItem,
  listReviewActivity,
  localDateKey,
  seedAuth,
} from './helpers';

/**
 * E2E-4:Tech Stack 复习 + 热力图(黄金路径)。
 *
 * 同时是**票 81 的界面级证据**:票 81 修的是「Log review 成功回调漏调
 * recordReviewActivity()」。API 层 curl 只能证明端点通,证明不了
 * 「点了 Log review 之后复习活动真的被记录」——那正是本用例的服务端旁证部分。
 *
 * 票 138（用户令：热力图面板先隐藏）之后，面板不再渲染，当日格着色断言
 * 已无断言对象：本用例的界面断言改为“面板不存在”，着色逻辑本身由
 * recordReview 的服务端计数 + 刷新不虚增继续覆盖；面板恢复显示时
 * （showReviewHeatmap 改 true）须把着色断言恢复。
 */
const TITLE = 'T81E2E 复习热力图题';
const TOPIC = 'T81E2E-topic';

test.describe('票81 · Tech Stack Log review 接入复习活动', () => {
  test('Log review 后热力图面板隐藏、服务端当日记录 +1、刷新不虚增', async ({
    page,
    request,
  }) => {
    const auth = await apiLogin(request);
    const itemId = await createKnowledgeItem(request, auth.token, {
      title: TITLE,
      topic: TOPIC,
      question: 'E2E-4 复习热力图用题',
      importance: 3,
      difficulty: 3,
    });

    const today = localDateKey(new Date());
    const before =
      (await listReviewActivity(request, auth.token)).find((a) => a.date === today)?.count ?? 0;

    try {
      await seedAuth(page, auth);
      await page.goto('/tech-stack');

      // 选中刚建的知识点
      await page.locator('.list-item', { hasText: TITLE }).first().click();

      // 打开「记录复习 / Log review」菜单,选第 3 档(= Good,四档顺序 Again/Hard/Good/Easy)
      const logReview = page
        .locator('.dh-actions button')
        .filter({ hasText: /记录复习|Log review|Enregistrer une révision/ })
        .first();
      await logReview.click();
      await page.locator('button[mat-menu-item]').nth(2).click();

      // ---- 界面:票 138 后热力图面板应隐藏（不再渲染） ----
      await expect(
        page.locator('.heatmap-panel'),
        '票 138 后热力图面板应隐藏'
      ).toHaveCount(0);

      // ---- 服务端旁证:当日计数 +1 ----
      await expect
        .poll(
          async () =>
            (await listReviewActivity(request, auth.token)).find((a) => a.date === today)?.count ?? 0,
          { timeout: 15_000 }
        )
        .toBe(before + 1);

      // ---- 刷新保持（面板仍隐藏，记录不虚增也不回退） ----
      await page.reload();
      await expect(
        page.locator('.heatmap-panel'),
        '刷新后面板仍应隐藏'
      ).toHaveCount(0);
      const afterReload =
        (await listReviewActivity(request, auth.token)).find((a) => a.date === today)?.count ?? 0;
      expect(afterReload, '刷新不虚增也不回退').toBe(before + 1);
    } finally {
      await deleteKnowledgeItem(request, auth.token, itemId);
    }
  });
});
