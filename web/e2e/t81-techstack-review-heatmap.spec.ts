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
 * 「点了 Log review 之后热力图当日格真的着色、刷新后还在」——那正是本用例。
 */
const TITLE = 'T81E2E 复习热力图题';
const TOPIC = 'T81E2E-topic';

/** 今天在热力图里的位置:12 周网格,最后一周的第 (周一=0) 个格子。 */
function todayCell(page: import('@playwright/test').Page) {
  const now = new Date();
  const dow = (now.getDay() + 6) % 7;
  return page.locator('.heatmap-week').nth(11).locator('.heatmap-day').nth(dow);
}

test.describe('票81 · Tech Stack Log review 接入复习活动', () => {
  test('Log review 后热力图当日格着色,刷新保持,服务端旁证有当日记录', async ({
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

      // ---- 界面:当日格必须着色 ----
      await expect(
        todayCell(page),
        'Log review 后热力图当日格应着色(lv-1 及以上)'
      ).toHaveClass(/lv-[1-4]/, { timeout: 20_000 });

      // ---- 服务端旁证:当日计数 +1 ----
      await expect
        .poll(
          async () =>
            (await listReviewActivity(request, auth.token)).find((a) => a.date === today)?.count ?? 0,
          { timeout: 15_000 }
        )
        .toBe(before + 1);

      // ---- 刷新保持 ----
      await page.reload();
      await expect(todayCell(page), '刷新后当日格仍应着色').toHaveClass(/lv-[1-4]/, {
        timeout: 20_000,
      });
      const afterReload =
        (await listReviewActivity(request, auth.token)).find((a) => a.date === today)?.count ?? 0;
      expect(afterReload, '刷新不虚增也不回退').toBe(before + 1);
    } finally {
      await deleteKnowledgeItem(request, auth.token, itemId);
    }
  });
});
