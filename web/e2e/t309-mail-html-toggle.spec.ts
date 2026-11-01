import { expect, test } from '@playwright/test';
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
 * 票 309 测试 22：Load images 逐封 opt-in（默认拦截 → 按下重取 ?images=1）。
 *
 * - 桩按 URL 分支：默认返回占位图 + X-Images-Blocked: 1（toggle 出现）；
 *   ?images=1 返回完整 https 图且无该头（toggle 消失）；
 * - (b) 的 img src 断言只看 DOM 值，不主张图片字节已加载（不依赖外网）。
 */
const SUBJECT = 'T309 Toggle';
const BLOCKED = fs.readFileSync(
  path.join(
    process.cwd(),
    'e2e',
    'fixtures',
    't309-mail-html-images-blocked.html',
  ),
  'utf8',
);
const ALLOWED = fs.readFileSync(
  path.join(
    process.cwd(),
    'e2e',
    'fixtures',
    't309-mail-html-images-allowed.html',
  ),
  'utf8',
);
const REMOTE_IMG = 'https://cdn.example.com/photo.png';

test.describe('票309 测试 22 · Load images 逐封 opt-in', () => {
  test('默认拦截 + toggle 可见；按下后重取 images=1 且帧内 img 为完整 https 地址', async ({
    page,
    request,
  }) => {
    const auth = await apiLogin(request);
    const entryId = await createEntry(request, auth.token, {
      companyName: 'T309Co',
      role: 'T309Role',
    });

    try {
      await addRound(request, auth.token, entryId, 'Technical');
      await seedMailAsset(request, auth.token, entryId, {
        subject: SUBJECT,
        fromName: 'T309 Sender',
        fromEmail: 'sender@example.com',
        transcriptText: 'T309 toggle fallback transcript',
      });
      await seedAuth(page, auth);
      await page.goto(`/playbook/${entryId}`);
      await page
        .locator('button.rtab', { hasText: /材料|Materials|Documents/ })
        .click();

      const htmlRequests: string[] = [];
      await page.route('**/mailbox/assets/*/html*', (route) => {
        htmlRequests.push(route.request().url());
        if (route.request().url().includes('images=1')) {
          return route.fulfill({
            status: 200,
            contentType: 'text/html; charset=utf-8',
            body: ALLOWED,
          });
        }
        return route.fulfill({
          status: 200,
          contentType: 'text/html; charset=utf-8',
          headers: { 'X-Images-Blocked': '1' },
          body: BLOCKED,
        });
      });

      const seen: string[] = [];
      page.on('request', (req) => seen.push(req.url()));
      await page.locator('button.email-head', { hasText: SUBJECT }).click();

      const iframe = page.locator('iframe.mail-html-frame');
      await expect(iframe).toBeVisible({ timeout: 20_000 });
      const toggle = page.locator('button.mail-html-toggle');
      await expect(toggle, '默认拦截时 Load images 应可见').toBeVisible();

      // (a) 按下前：零非应用源请求。
      await page.waitForTimeout(1000);
      const pageOrigin = new URL(page.url()).origin;
      expect(
        seen.filter((u) => u.startsWith('http') && !u.startsWith(pageOrigin)),
      ).toEqual([]);

      // (b) 按下后：重取 URL 含 images=1；帧内 img src 为完整 https 地址；toggle 消失。
      await toggle.click();
      await expect
        .poll(() => htmlRequests.some((u) => u.includes('images=1')))
        .toBe(true);
      const frame = iframe.contentFrame();
      await expect
        .poll(() => frame.locator('img').first().getAttribute('src'))
        .toBe(REMOTE_IMG);
      await expect(toggle, 'opt-in 后 toggle 应消失').toHaveCount(0);
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
