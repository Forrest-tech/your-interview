import { expect, test, type Page } from '@playwright/test';
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
 * 票 309 测试 20：零外站请求 E2E（客户端渲染 + 隔离证明；非服务端证明）。
 *
 * - /html 由 page.route 桩提供（不依赖后端、不新增播种接口）；
 * - 桩文档分两层：(a) 服务端契约层（远端图已替换为占位图）+
 *   (b) 客户端鲁棒层（刻意不是服务端输出的 <style> 追踪块与追踪内联样式）；
 * - 断言：(i) 渲染期间零非应用源请求；(ii) 帧内 DOM 无 <style> 元素、
 *   无含 url( 的 style 属性；另断言真实 iframe 的 sandbox 属性等于计划精确值
 *   （测试 18 在真实浏览器中的复核）。
 */
const SUBJECT = 'T309 Network';
const FIXTURE = fs.readFileSync(
  path.join(process.cwd(), 'e2e', 'fixtures', 't309-mail-html-network.html'),
  'utf8',
);
const SANDBOX =
  'allow-same-origin allow-popups allow-popups-to-escape-sandbox';

test.describe('票309 测试 20 · 邮件 HTML 零外站请求', () => {
  test('渲染期间无外站请求；帧内无 style 元素与 url( 样式；sandbox 精确', async ({
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
        transcriptText: 'T309 network fallback transcript',
      });
      await seedAuth(page, auth);
      await page.goto(`/playbook/${entryId}`);
      await page
        .locator('button.rtab', { hasText: /材料|Materials|Documents/ })
        .click();
      await page.route('**/mailbox/assets/*/html*', (route) =>
        route.fulfill({
          status: 200,
          contentType: 'text/html; charset=utf-8',
          body: FIXTURE,
        }),
      );

      // 渲染期请求采集（自展开前开始，页面加载期的字体等请求不在此列）。
      const seen: string[] = [];
      const onRequest = (req: { url(): string }) => seen.push(req.url());
      page.on('request', onRequest);
      await page.locator('button.email-head', { hasText: SUBJECT }).click();

      const iframe = page.locator('iframe.mail-html-frame');
      await expect(iframe, '邮件 HTML 应渲染为 iframe').toBeVisible({
        timeout: 20_000,
      });
      const frame = iframe.contentFrame();
      await expect(
        frame.locator('p', { hasText: 'Interview invitation' }),
        '格式化正文应出现在帧内',
      ).toBeVisible();
      await page.waitForTimeout(1000);
      page.off('request', onRequest);

      // (i) 零非应用源请求（data: 不发请求；桩 /html 与页面同源）。
      const pageOrigin = new URL(page.url()).origin;
      const external = seen.filter(
        (u) => u.startsWith('http') && !u.startsWith(pageOrigin),
      );
      expect(external).toEqual([]);

      // (ii) 帧内无 <style> 元素、无含 url( 的 style 属性、无追踪域文本残留。
      await expect(frame.locator('style')).toHaveCount(0);
      const styleUrls = await frame
        .locator('[style]')
        .evaluateAll((els) =>
          els
            .map((el) => el.getAttribute('style') ?? '')
            .filter((v) => /url\(/i.test(v)),
        );
      expect(styleUrls).toEqual([]);
      const frameText = await frame.locator('body').innerText();
      expect(frameText).not.toContain('tracker.example.invalid');

      // 真实 iframe 的 sandbox 属性等于计划精确值。
      await expect(iframe).toHaveAttribute('sandbox', SANDBOX);
      await expect(iframe).toHaveAttribute('referrerpolicy', 'no-referrer');
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
