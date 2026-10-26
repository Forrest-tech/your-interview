import { expect, test } from '@playwright/test';
import type { APIRequestContext } from '@playwright/test';
import {
  addRound,
  apiLogin,
  API,
  createEntry,
  deleteEntry,
  seedAuth,
} from './helpers';

/**
 * 票 183:轮次轨道节点重设计(定稿 §3–§5)。
 *
 * 节点第三行不再是「结果 / 材料」两事实,改为状态裸词 + 综合分 k/v:
 *   终态 outcome 优先(通过/未通过/已取消,不受日期影响);
 *   非终态看日期:无日期 → 未安排,未来/今天 → 等待面试,过去 → 面试结束;
 *   综合分仅当轮次有复盘 overall 时显示,无值整段不渲染。
 * 轮次经 API 预置(PUT 轮次改 outcome/日期、PUT review 写 overall),
 * 本用例只断言轨道节点,不碰详情卡与实录页签。
 * 状态词按界面语言三语归一后断言(管理账号语言不固定,此处不硬编码单语)。
 */
const COMPANY = 'E2E183Co';
const ROLE = 'E2E183RoleX';

/** 状态词(三语) → 规范档,未命中则原样返回以便断言报错时看得见原文。 */
const WORD: Record<string, string> = {
  '通过': 'passed', 'Passed': 'passed', 'Réussi': 'passed',
  '未通过': 'failed', 'Failed': 'failed', 'Échoué': 'failed',
  '已取消': 'cancelled', 'Cancelled': 'cancelled', 'Annulé': 'cancelled',
  '未安排': 'unscheduled', 'Unscheduled': 'unscheduled', 'Non planifié': 'unscheduled',
  '等待面试': 'scheduled', 'Scheduled': 'scheduled', 'Planifié': 'scheduled',
  '面试结束': 'completed', 'Completed': 'completed', 'Terminé': 'completed',
};

interface RoundRow {
  id: string;
  order: number;
  stage: string;
  scheduledDate?: string | null;
  scheduledTime?: string | null;
  interviewers?: string | null;
  format?: string | null;
  location?: string | null;
  meetingLink?: string | null;
  outcome: string;
  feedback?: string | null;
  version?: number | null;
}

async function getRounds(
  request: APIRequestContext,
  token: string,
  entryId: string
): Promise<RoundRow[]> {
  const r = await request.get(`${API}/api/interviews/${entryId}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!r.ok()) {
    throw new Error(`[e2e] 取条目详情失败: HTTP ${r.status()} ${(await r.text()).slice(0, 200)}`);
  }
  const j = (await r.json()) as { rounds?: RoundRow[] };
  return [...(j.rounds ?? [])].sort((a, b) => a.order - b.order);
}

async function putRound(
  request: APIRequestContext,
  token: string,
  entryId: string,
  round: RoundRow,
  patch: { outcome?: string; scheduledDate?: string | null }
): Promise<void> {
  const r = await request.put(`${API}/api/interviews/${entryId}/rounds/${round.id}`, {
    headers: { Authorization: `Bearer ${token}` },
    data: {
      stage: round.stage,
      scheduledDate: patch.scheduledDate !== undefined ? patch.scheduledDate : (round.scheduledDate ?? null),
      scheduledTime: round.scheduledTime ?? null,
      interviewers: round.interviewers ?? null,
      format: round.format ?? null,
      location: round.location ?? null,
      meetingLink: round.meetingLink ?? null,
      outcome: patch.outcome ?? round.outcome,
      feedback: round.feedback ?? null,
      version: round.version ?? null,
    },
  });
  if (!r.ok()) {
    throw new Error(`[e2e] 改轮次失败: HTTP ${r.status()} ${(await r.text()).slice(0, 200)}`);
  }
}

test.describe('票 183 · 轮次轨道节点状态与评分', () => {
  test('六档状态各一例;综合分有值显示、无值不渲染;无旧事实行', async ({ page, request }) => {
    const auth = await apiLogin(request);
    const entryId = await createEntry(request, auth.token, {
      companyName: COMPANY,
      role: ROLE,
    });

    try {
      // 建单自带首轮(Technical)，再新增五轮，共六轮(创建序即 order 序)
      for (let i = 0; i < 5; i++) {
        await addRound(request, auth.token, entryId, 'Technical');
      }
      const rounds = await getRounds(request, auth.token, entryId);
      expect(rounds).toHaveLength(6);

      // 六档:终态优先(日期故意反向),非终态按日期
      await putRound(request, auth.token, entryId, rounds[0]!, { outcome: 'Passed', scheduledDate: '2020-01-01' });
      await putRound(request, auth.token, entryId, rounds[1]!, { outcome: 'Failed', scheduledDate: '2099-01-01' });
      await putRound(request, auth.token, entryId, rounds[2]!, { outcome: 'Cancelled', scheduledDate: '2099-01-01' });
      await putRound(request, auth.token, entryId, rounds[3]!, { outcome: 'Pending', scheduledDate: null });
      await putRound(request, auth.token, entryId, rounds[4]!, { outcome: 'Pending', scheduledDate: '2099-01-01' });
      await putRound(request, auth.token, entryId, rounds[5]!, { outcome: 'Pending', scheduledDate: '2020-01-01' });

      // 给第 1 轮写复盘综合分 87(其余五轮无复盘 → 无评分段)
      const fresh = await getRounds(request, auth.token, entryId);
      const review = await request.put(
        `${API}/api/interviews/${entryId}/rounds/${fresh[0]!.id}/review`,
        {
          headers: { Authorization: `Bearer ${auth.token}` },
          data: { reviewJson: JSON.stringify({ overall: 87 }), version: fresh[0]!.version ?? null },
        }
      );
      if (!review.ok()) {
        throw new Error(`[e2e] 写复盘失败: HTTP ${review.status()} ${(await review.text()).slice(0, 200)}`);
      }

      await seedAuth(page, auth);
      await page.goto(`/playbook/${entryId}`);

      const track = page.locator('[data-testid="round-track"]');
      const nodes = track.locator('.rnode');
      await expect(nodes.first(), '六轮预置后轨道节点应可见').toBeVisible({ timeout: 20_000 });
      await expect(nodes, '六轮应渲染六个节点').toHaveCount(6);

      // ---- 旧事实行已删,新状态行已立(定稿 §3) ----
      await expect(
        track.locator('.rfacts'),
        '票 183 已删旧事实行:节点内不应再有 .rfacts'
      ).toHaveCount(0);
      await expect(
        track.locator('.rstate'),
        '票 183 新状态行:每节点应有一行 .rstate'
      ).toHaveCount(6);

      // ---- 六档状态 + 评分有无(定稿 §4–§5) ----
      // 节点在轨道内按日期序排,不按创建序,故按 .st 文案归一后断言集合,
      // 再按“通过节点”定位评分归属,不依赖 nth 顺序。
      const seen: string[] = [];
      for (let i = 0; i < 6; i++) {
        const node = nodes.nth(i);
        const st = (await node.locator('.st').innerText()).trim();
        const canon = WORD[st] ?? `未知:${st}`;
        seen.push(canon);
        if (canon === 'passed') {
          await expect(
            node,
            '通过节点应显示综合分 87'
          ).toContainText(/综合分|Overall score|Note globale/);
          await expect(node, '通过节点综合分值应为 87').toContainText('87');
        } else {
          await expect(
            node,
            `非通过节点(${canon})不应渲染评分段`
          ).not.toContainText(/综合分|Overall score|Note globale/);
        }
      }
      expect(
        seen.sort(),
        '六档状态应齐全:通过/未通过/已取消/未安排/等待面试/面试结束'
      ).toEqual(['cancelled', 'completed', 'failed', 'passed', 'scheduled', 'unscheduled']);
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
