import { expect, test, type Page, type APIRequestContext, type Route } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  addRound,
  apiLogin,
  createEntry,
  deleteEntry,
  seedAuth,
  seedMailAsset,
} from './helpers';

/**
 * 票 309 前端 DOM 断言 spec（Jest 无法承载的部分，见 test 17/19/24/24b）：
 * Jest 为 node 环境、无 DOM、无 TestBed（且不得新增 jsdom 依赖），故
 * DOMPurify 的真实浏览器行为（尤其 mXSS 系重解析）只能在 Chromium 中证明。
 * 本文件只证明客户端渲染与回落；服务端行为由后端测试证明（诚实边界）。
 */
const PLACEHOLDER =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8Xw8AAwMCAO+ip1sAAAAASUVORK5CYII=';

function readFixture(name: string): string {
  return fs.readFileSync(
    path.join(process.cwd(), 'e2e', 'fixtures', name),
    'utf8',
  );
}
const HOSTILE = readFixture('t309-mail-html-hostile.html');
const UNMAPPED = readFixture('t309-mail-html-unmapped-cid.html');
const EMPTY = readFixture('t309-mail-html-empty.html');
const BLOCKED = readFixture('t309-mail-html-images-blocked.html');

type RouteHandler = (route: Route) => Promise<void> | void;

async function openSeededMail(
  page: Page,
  request: APIRequestContext,
  subject: string,
  transcriptText: string,
  routeHandler: RouteHandler,
): Promise<{ token: string; entryId: string }> {
  const auth = await apiLogin(request);
  const entryId = await createEntry(request, auth.token, {
    companyName: 'T309Co',
    role: 'T309Role',
  });
  try {
    await addRound(request, auth.token, entryId, 'Technical');
    await seedMailAsset(request, auth.token, entryId, {
      subject,
      fromName: 'T309 Sender',
      fromEmail: 'sender@example.com',
      transcriptText,
    });
    await seedAuth(page, auth);
    await page.goto(`/playbook/${entryId}`);
    await page
      .locator('button.rtab', { hasText: /材料|Materials|Documents/ })
      .click();
    await page.route('**/mailbox/assets/*/html*', routeHandler);
    await page.locator('button.email-head', { hasText: subject }).click();
  } catch (e) {
    await deleteEntry(request, auth.token, entryId).catch(() => undefined);
    throw e;
  }
  return { token: auth.token, entryId };
}

async function assertTextFallback(page: Page, transcript: string): Promise<void> {
  // 先断言回落文本出现（它要求 noHtml 已发出），再断言无 iframe 与无 toggle。
  await expect(
    page.locator('.email-open', { hasText: transcript }),
    '回落时应渲染纯文本正文',
  ).toBeVisible({ timeout: 20_000 });
  await expect(page.locator('iframe.mail-html-frame')).toHaveCount(0);
  await expect(page.locator('button.mail-html-toggle')).toHaveCount(0);
}

test.describe('票309 客户端消毒与回落（Chromium DOM 断言）', () => {
  test('测试 17：敌意 HTML 经 DOMPurify 后危险项全灭、合法排版存活', async ({
    page,
    request,
  }) => {
    const ctx = await openSeededMail(page, request, 'T309 Hostile', 'T309 hostile fallback', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'text/html; charset=utf-8',
        body: HOSTILE,
      }),
    );
    try {
      const iframe = page.locator('iframe.mail-html-frame');
      await expect(iframe).toBeVisible({ timeout: 20_000 });
      const frame = iframe.contentFrame();
      await expect(frame.locator('script')).toHaveCount(0);
      await expect(frame.locator('form')).toHaveCount(0);
      await expect(frame.locator('input')).toHaveCount(0);
      await expect(frame.locator('style')).toHaveCount(0);
      const onclick = await frame.locator('[onclick]').count();
      const jsHref = await frame
        .locator('a[href]')
        .evaluateAll((els) =>
          els.filter((a) =>
            (a.getAttribute('href') ?? '')
              .toLowerCase()
              .startsWith('javascript:'),
          ).length,
        );
      const urlStyle = await frame
        .locator('[style]')
        .evaluateAll((els) =>
          els.filter((el) => /url\(/i.test(el.getAttribute('style') ?? '')).length,
        );
      expect({ onclick, jsHref, urlStyle }).toEqual({
        onclick: 0,
        jsHref: 0,
        urlStyle: 0,
      });
      const good = frame.locator('a', { hasText: 'Good link' });
      await expect(good).toHaveAttribute('href', 'https://ok.example.com/page');
      await expect(good).toHaveAttribute('target', '_blank');
      await expect(good).toHaveAttribute('rel', 'noopener noreferrer');
      await expect(frame.locator('body')).toContainText('real content');
      await expect(frame.locator('body')).toContainText('Keep this table');
    } finally {
      await deleteEntry(request, ctx.token, ctx.entryId);
    }
  });

  test('测试 19：未映射 CID 到客户端一律占位图、永无 cid: 与破图', async ({
    page,
    request,
  }) => {
    const ctx = await openSeededMail(page, request, 'T309 CID', 'T309 cid fallback', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'text/html; charset=utf-8',
        body: UNMAPPED,
      }),
    );
    try {
      const iframe = page.locator('iframe.mail-html-frame');
      await expect(iframe).toBeVisible({ timeout: 20_000 });
      const frame = iframe.contentFrame();
      await expect(frame.locator('img').first()).toHaveAttribute(
        'src',
        PLACEHOLDER,
      );
      const hasCid = await frame
        .locator('body')
        .evaluate((el) => el.innerHTML.includes('cid:'));
      expect(hasCid).toBe(false);
      await expect(frame.locator('body')).toContainText(
        'Body text after the image.',
      );
    } finally {
      await deleteEntry(request, ctx.token, ctx.entryId);
    }
  });

  test('测试 24：消毒为空 → 无 iframe、纯文本回落', async ({ page, request }) => {
    const transcript = 'T309 empty fallback transcript';
    const ctx = await openSeededMail(page, request, 'T309 Empty', transcript, (route) =>
      route.fulfill({
        status: 200,
        contentType: 'text/html; charset=utf-8',
        body: EMPTY,
      }),
    );
    try {
      await assertTextFallback(page, transcript);
    } finally {
      await deleteEntry(request, ctx.token, ctx.entryId);
    }
  });

  test('测试 24 重渲染：Load-images 重取消毒为空 → 同样回落（toggle 消失）', async ({
    page,
    request,
  }) => {
    const transcript = 'T309 rerender fallback transcript';
    const ctx = await openSeededMail(page, request, 'T309 Rerender', transcript, (route) => {
      if (route.request().url().includes('images=1')) {
        return route.fulfill({
          status: 200,
          contentType: 'text/html; charset=utf-8',
          body: EMPTY,
        });
      }
      return route.fulfill({
        status: 200,
        contentType: 'text/html; charset=utf-8',
        headers: { 'X-Images-Blocked': '1' },
        body: BLOCKED,
      });
    });
    try {
      const iframe = page.locator('iframe.mail-html-frame');
      await expect(iframe).toBeVisible({ timeout: 20_000 });
      const toggle = page.locator('button.mail-html-toggle');
      await expect(toggle).toBeVisible();
      await toggle.click();
      await assertTextFallback(page, transcript);
    } finally {
      await deleteEntry(request, ctx.token, ctx.entryId);
    }
  });

  test('测试 24b（500）：/html 500 → 纯文本回落、无 iframe、无错误态', async ({
    page,
    request,
  }) => {
    const transcript = 'T309 500 fallback transcript';
    const ctx = await openSeededMail(page, request, 'T309 Fail500', transcript, (route) =>
      route.fulfill({ status: 500, body: 'internal error' }),
    );
    try {
      await assertTextFallback(page, transcript);
    } finally {
      await deleteEntry(request, ctx.token, ctx.entryId);
    }
  });

  test('测试 24b（网络错误）：/html 取信失败 → 纯文本回落、无 iframe、无错误态', async ({
    page,
    request,
  }) => {
    const transcript = 'T309 net fallback transcript';
    const ctx = await openSeededMail(page, request, 'T309 FailNet', transcript, (route) =>
      route.abort('failed'),
    );
    try {
      await assertTextFallback(page, transcript);
    } finally {
      await deleteEntry(request, ctx.token, ctx.entryId);
    }
  });
});
