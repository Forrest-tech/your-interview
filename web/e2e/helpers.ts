import type { APIRequestContext, Page } from '@playwright/test';

/** 后端网关(gateway)。E2E 直连网关,不经前端代理 —— 失败时能直接区分是 API 挂了还是页面挂了。 */
export const API = process.env['E2E_API'] ?? 'http://127.0.0.1:5200';

/**
 * E2E 专用种子账号。
 *
 * ⚠️ 纪律:测试内建的数据跑完必须清理(见 deleteEntry),不许污染真实数据,
 *    也不许依赖他人遗留数据 —— 否则套件换个环境就红。
 */
export const E2E_USER = {
  email: process.env['E2E_EMAIL'] ?? 'admin@your-interview.local',
  password: process.env['E2E_PASSWORD'] ?? 'Admin@2026',
};

export interface Auth {
  token: string;
  refresh: string;
  user: unknown;
}

/** 走 API 登录拿令牌(绕开登录页的人机校验,登录页 UI 由其它用例单独覆盖)。 */
export async function apiLogin(request: APIRequestContext): Promise<Auth> {
  const r = await request.post(`${API}/api/auth/login`, { data: E2E_USER });
  if (!r.ok()) {
    throw new Error(`[e2e] 登录失败: HTTP ${r.status()} ${(await r.text()).slice(0, 200)}`);
  }
  const j = (await r.json()) as {
    tokens: { accessToken: string; refreshToken: string };
    profile: unknown;
  };
  return { token: j.tokens.accessToken, refresh: j.tokens.refreshToken, user: j.profile };
}

/**
 * 把会话种进 localStorage。
 *
 * 应用在 localStorage 里存 yi.accessToken / yi.refreshToken / yi.user(AuthService),
 * 拦截器从那里取 Bearer。这里用 addInitScript 在页面脚本执行**之前**写入,
 * 等价于"已登录用户直接打开页面",跳过的只是登录表单本身。
 */
export async function seedAuth(page: Page, auth: Auth): Promise<void> {
  const payload = {
    token: auth.token,
    refresh: auth.refresh,
    user: JSON.stringify(auth.user),
  };
  await page.addInitScript((a: { token: string; refresh: string; user: string }) => {
    localStorage.setItem('yi.accessToken', a.token);
    localStorage.setItem('yi.refreshToken', a.refresh);
    localStorage.setItem('yi.user', a.user);
  }, payload);
}

/** 建一个 Playbook 面试条目。不传 outcome / result —— 即"无轮次、无终局结果"形态。 */
export async function createEntry(
  request: APIRequestContext,
  token: string,
  body: { companyName: string; role: string; notes?: string }
): Promise<string> {
  const r = await request.post(`${API}/api/interviews`, {
    headers: { Authorization: `Bearer ${token}` },
    data: body,
  });
  if (!r.ok()) {
    throw new Error(`[e2e] 建条目失败: HTTP ${r.status()} ${(await r.text()).slice(0, 200)}`);
  }
  const j = (await r.json()) as { id: string };
  return j.id;
}

/** 清理:E2E 造的数据不留残渣。失败也吞掉 —— 清理失败不该掩盖主断言的结果。 */
export async function deleteEntry(
  request: APIRequestContext,
  token: string,
  id: string
): Promise<void> {
  try {
    await request.delete(`${API}/api/interviews/${id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  } catch {
    /* 清理失败不阻断 */
  }
}

/** 建一个 Tech Stack 知识点。 */
export async function createKnowledgeItem(
  request: APIRequestContext,
  token: string,
  body: { title: string; topic: string; question: string; importance?: number; difficulty?: number }
): Promise<string> {
  const r = await request.post(`${API}/api/knowledge`, {
    headers: { Authorization: `Bearer ${token}` },
    data: body,
  });
  if (!r.ok()) {
    throw new Error(`[e2e] 建知识点失败: HTTP ${r.status()} ${(await r.text()).slice(0, 200)}`);
  }
  const j = (await r.json()) as { id: string };
  return j.id;
}

/** 清理知识点。 */
export async function deleteKnowledgeItem(
  request: APIRequestContext,
  token: string,
  id: string
): Promise<void> {
  try {
    await request.delete(`${API}/api/knowledge/${id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  } catch {
    /* 清理失败不阻断 */
  }
}

/** 读复习活动(GET /api/knowledge/review-activity)。票81 用它做服务端旁证。 */
export async function listReviewActivity(
  request: APIRequestContext,
  token: string
): Promise<{ date: string; count: number }[]> {
  const r = await request.get(`${API}/api/knowledge/review-activity`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!r.ok()) {
    throw new Error(`[e2e] 读复习活动失败: HTTP ${r.status()}`);
  }
  return (await r.json()) as { date: string; count: number }[];
}

/** 本地日历日期键 YYYY-MM-DD —— 与前端 localDateKey、后端存储口径一致(不用 UTC)。 */
export function localDateKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** 按 role 找投递记录(E2E 清理用)。 */
export async function findApplicationByRole(
  request: APIRequestContext,
  token: string,
  role: string
): Promise<{ id: string } | null> {
  try {
    const r = await request.get(`${API}/api/jobs/applications?page=1&pageSize=100`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    if (!r.ok()) return null;
    const j = (await r.json()) as { items?: { id: string; role?: string }[] };
    return (j.items ?? []).find((a) => a.role === role) ?? null;
  } catch {
    return null;
  }
}

/** 清理投递记录。 */
export async function deleteApplication(
  request: APIRequestContext,
  token: string,
  id: string
): Promise<void> {
  try {
    await request.delete(`${API}/api/jobs/applications/${id}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  } catch {
    /* 清理失败不阻断 */
  }
}

/** 票 147:经智能粘贴建一个投递记录(复用 t81 的 UI 路径,CI 已验证)。 */
export async function createApplicationViaSmartAdd(
  page: Page,
  role: string,
  jdText: string
): Promise<void> {
  await page.addInitScript(() => {
    try {
      localStorage.setItem('yi-tracker-view', 'list');
    } catch {
      /* 忽略 */
    }
  });
  await page.goto('/tracker');
  await page.locator('.smart-btn').first().click();
  const dialog = page.locator('mat-dialog-container');
  await dialog.locator('.ai-toggle input[type="checkbox"]').uncheck().catch(() => undefined);
  await dialog.locator('textarea[matInput]').first().fill(jdText);
  await dialog
    .locator('button', { hasText: /解析|Parse|Analyser/ })
    .first()
    .click();
  await dialog.locator('input[name="jdRole"]').fill(role);
  await dialog
    .locator('button', { hasText: /保存为投递记录|Save as application|Enregistrer comme candidature/ })
    .first()
    .click();
}

/** 票 147:在列表视图点行打开详情抽屉,返回抽屉定位器。 */
export async function openDetailDialog(page: Page, role: string) {
  const row = page.locator('.list-row', { hasText: role }).first();
  await row.click();
  const dialog = page.locator('mat-dialog-container');
  await dialog.locator('.detail-header').waitFor({ state: 'visible', timeout: 30_000 });
  return dialog;
}

/** 给 Playbook 条目加一轮(Order 自动递增;stage 缺省 Technical)。 */
export async function addRound(
  request: APIRequestContext,
  token: string,
  entryId: string,
  stage = 'Technical'
): Promise<void> {
  const r = await request.post(`${API}/api/interviews/${entryId}/rounds`, {
    headers: { Authorization: `Bearer ${token}` },
    data: { stage },
  });
  if (!r.ok()) {
    throw new Error(`[e2e] 加轮次失败: HTTP ${r.status()} ${(await r.text()).slice(0, 200)}`);
  }
}
