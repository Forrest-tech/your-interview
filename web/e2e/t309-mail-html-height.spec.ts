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
 * 票 309 测试 21：iframe 高度封顶 1600px（超高内容帧内滚动）。
 */
const SUBJECT = 'T309 Tall';
const FIXTURE = fs.readFileSync(
  path.join(process.cwd(), 'e2e', 'fixtures', 't309-mail-html-tall.html'),
  'utf8',
);

test.describe('票309 测试 21 · 邮件 HTML 高度封顶', () => {
  test('超高邮件帧高恰为 1600px 且帧内可滚动', async ({ page, request }) => {
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
        transcriptText: 'T309 tall fallback transcript',
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
      await page.locator('button.email-head', { hasText: SUBJECT }).click();

      const iframe = page.locator('iframe.mail-html-frame');
      await expect(iframe).toBeVisible({ timeout: 20_000 });
      await expect(iframe).toHaveCSS('height', '1600px', { timeout: 10_000 });

      // 帧内文档高于 1600px → 帧内滚动成立（overflow: auto）。
      const frame = iframe.contentFrame();
      await expect
        .poll(() =>
          frame.locator('body').evaluate(() => document.documentElement.scrollHeight),
        )
        .toBeGreaterThan(1600);
    } finally {
      await deleteEntry(request, auth.token, entryId);
    }
  });
});
