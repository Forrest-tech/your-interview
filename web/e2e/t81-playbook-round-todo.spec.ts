import { expect, test } from '@playwright/test';
import {
  addRound,
  apiLogin,
  createEntry,
  deleteEntry,
  seedAuth,
} from './helpers';

/**
 * E2E-3:Playbook 条目 → 轮次可见 + Review & to-dos 已藏起(黄金路径)。
 *
 * 轮次经 API 预置(轮次的 UI 入口是"贴邮件→建轮次"的 intake 流,属于更重的
 * 独立路径);本用例聚焦 UI 侧的两件用户必做事:
 *   1. 轮次建好后详情页要真的显示出来(第 1 轮轨道节点;票123 R-9 已把旧轮次 chip
 *      选择器换成轨道,旧 `.step` 定位不再存在,此处钉新契约);
 *   2. Review & to-dos 区块按用户决定藏起(票166:模板层 hideReviewTodos 开关,
 *      数据与保存逻辑不动):详情页不再渲染 to-do 输入与 to-do 行,此处钉新契约,
 *      不许为恢复旧断言而把功能放回来。旧"添加 → 勾选 → 刷新保持"三段随藏起
 *      一并退役:to-do 经 PATCH 的持久化逻辑本身未动,但 UI 已藏起,故不再经
 *      UI 断言,不得虚构覆盖。
 */
const COMPANY = 'E2E3Co';
const ROLE = 'E2E3RoleX';

test.describe('票89 E2E-3 · Playbook 轮次可见与 Review & to-dos 藏起', () => {
  test('轮次在详情页可见;Review & to-dos 不渲染', async ({ page, request }) => {
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

      // ---- 轮次在 UI 可见:第 1 轮轨道节点(票123 R-9 新契约) ----
      // 旧 `.step` + 'R1' 定位已被 R-9 轨道取代:节点为 [data-testid="round-track"] .rnode,
      // 文案为 i18n roundLabel(第 {n} 轮 / Round {n} / Tour {n});不恢复 .step 钩子。
      await expect(
        page
          .locator('[data-testid="round-track"] .rnode', {
            hasText: /第 1 轮|Round 1|Tour 1/,
          })
          .first(),
        '加轮次后详情页应出现第 1 轮轨道节点'
      ).toBeVisible({ timeout: 20_000 });

      // ---- Review & to-dos 已藏起(票166 新契约) ----
      // 新建条目无技术短板,外层 @if (!hideReviewTodos || weaknesses?.length) 为假,
      // 整卡不渲染:to-do 新增输入与 to-do 行必须都不存在。断言"不存在"而非
      // "不可见",因为藏起是模板层不渲染,不是 CSS 隐藏;若将来功能恢复,此处
      // 需随新界面改写。全仓核查:仅本文件引用 .action-add/.action-row,无同因他例。
      await expect(
        page.locator('.action-add input.fld'),
        '票166 已藏起 Review & to-dos:to-do 新增输入不应被渲染'
      ).toHaveCount(0);
      await expect(
        page.locator('.action-row'),
        '票166 已藏起 Review & to-dos:to-do 行不应被渲染'
      ).toHaveCount(0);
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
