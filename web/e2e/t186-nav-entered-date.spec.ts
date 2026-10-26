import { expect, test } from '@playwright/test';
import { API, apiLogin, createEntry, deleteEntry, seedAuth } from './helpers';

/**
 * 票186 · 左栏条目“进入 In Progress 的日期”。
 *
 * 断言(定稿 §10,不断言日期具体值 —— 时区依赖,格式化正确性由 Jest 覆盖):
 *  - In Progress 组每项含 .nav-entered 行,且文本含“进入 In Progress”三语标签之一;
 *  - Completed 组各项无 .nav-entered 行。
 * 日期值本身(账号时区、仅日期)由 formatEnteredDate 的 Jest 用例锁定。
 */
const ACTIVE_COMPANY = 'NavDateE2E186Active';
const DONE_COMPANY = 'NavDateE2E186Done';
const ROLE = '票186 E2E 进入日期';
const LABELS = ['进入 In Progress', 'Entered In Progress', 'Entré en cours'];

test.describe('票186 · 左栏进入 In Progress 日期行', () => {
  test('In Progress 组每项含日期行,Completed 组无日期行', async ({
    page,
    request,
  }) => {
    const auth = await apiLogin(request);
    const activeId = await createEntry(request, auth.token, {
      companyName: ACTIVE_COMPANY,
      role: ROLE,
    });
    const doneId = await createEntry(request, auth.token, {
      companyName: DONE_COMPANY,
      role: ROLE,
    });

    try {
      // Completed 形态:基本信息 Result 写终态词 → 派生 Passed → 落 Completed 组。
      const put = await request.put(`${API}/api/interviews/${doneId}`, {
        headers: { Authorization: `Bearer ${auth.token}` },
        data: { companyName: DONE_COMPANY, role: ROLE, roundNo: 1, result: '通过' },
      });
      expect(put.ok(), `基本信息 Result 置终态词失败: HTTP ${put.status()}`).toBeTruthy();

      await seedAuth(page, auth);

      // ---- In Progress 组 ----
      await page.goto(`/playbook/${activeId}`);
      const nav = page.locator('[data-testid="entry-nav"]');
      const activeGroup = nav.locator('.nav-group').first();
      const currentActive = activeGroup.locator('.nav-item.on');
      await expect(currentActive, '当前条目应高亮落在 In Progress 组').toHaveCount(1);
      await expect(currentActive).toContainText(ACTIVE_COMPANY);
      // 每项都有日期行(后端字段非空、DTO 必带,迁移已回填存量行)。
      const activeItems = activeGroup.locator('.nav-item');
      const activeEntered = activeGroup.locator('.nav-entered');
      await expect(activeEntered).toHaveCount(await activeItems.count());
      const activeText = ((await currentActive.locator('.nav-entered').textContent()) ?? '').trim();
      expect(
        LABELS.some((l) => activeText.includes(l)),
        `In Progress 日期行应含三语标签之一,实际为「${activeText}」`
      ).toBeTruthy();

      // ---- Completed 组 ----
      await page.goto(`/playbook/${doneId}`);
      const completedGroup = nav.locator('.nav-group').nth(1);
      const currentDone = completedGroup.locator('.nav-item.on');
      await expect(currentDone, '当前条目应高亮落在 Completed 组').toHaveCount(1);
      await expect(currentDone).toContainText(DONE_COMPANY);
      await expect(completedGroup.locator('.nav-entered')).toHaveCount(0);
    } finally {
      await deleteEntry(request, auth.token, activeId);
      await deleteEntry(request, auth.token, doneId);
    }
  });
});
