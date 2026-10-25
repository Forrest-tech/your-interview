import { expect, test } from '@playwright/test';
import path from 'node:path';
import {
  apiLogin,
  createApplicationViaSmartAdd,
  deleteApplication,
  findApplicationByRole,
  openDetailDialog,
  seedAuth,
} from './helpers';

/**
 * 票 147 E2E:详情抽屉 Documents 页签简历缩放。
 *
 * 缩放机制断言:页的计算 max-width(缩放控制值) + 实测显示宽
 * (= min(容器宽, max-width),容器封顶时显示宽跟容器走)。
 * 上传路径:Documents 页签的投递版本上传框,喂夹具 PDF。
 * 清晰度断言:200% 档重渲染后图片源自然宽应为 612pt × 3.2 ≈ 1958px。
 * 位置断言:翻到第 2 页后缩放,重渲染后仍在第 2 页首附近。
 */
const COMPANY = 'ZoomE2E147Co';
const JD_TEXT = `Senior Backend Engineer\n\nCompany: ${COMPANY}\nSalary: $140,000 - $180,000 per year\nLocation: Toronto, ON (Hybrid)\n\nWe are building the commerce platform of the future.`;
const ONE_PAGE = path.join(process.cwd(), 'e2e', 'fixtures', 't147-one-page.pdf');
const TWO_PAGE = path.join(process.cwd(), 'e2e', 'fixtures', 't147-two-page.pdf');

async function setupAppWithPdf(
  page: import('@playwright/test').Page,
  role: string,
  fixture: string
): Promise<import('@playwright/test').Locator> {
  await createApplicationViaSmartAdd(page, role, JD_TEXT);
  await expect(page.getByText(COMPANY).first(), '保存后列表应出现新投递').toBeVisible({
    timeout: 30_000,
  });
  const dialog = await openDetailDialog(page, role);
  await dialog.locator('.tab', { hasText: /文档|Documents/ }).click();
  await dialog.locator('input[type="file"]').setInputFiles(fixture);
  await expect(page.locator('.pdfv-page').first(), '上传后简历页应渲染').toBeVisible({
    timeout: 30_000,
  });
  return dialog;
}

async function firstPageSize(page: import('@playwright/test').Page): Promise<{
  maxWidth: string;
  clientWidth: number;
  containerWidth: number;
}> {
  return await page
    .locator('.pdfv-page')
    .first()
    .evaluate((el) => {
      const parent = el.parentElement as HTMLElement;
      return {
        maxWidth: getComputedStyle(el).maxWidth,
        clientWidth: el.clientWidth,
        containerWidth: parent.clientWidth,
      };
    });
}

async function expectZoom(
  page: import('@playwright/test').Page,
  pct: string,
  maxWidth: string
): Promise<void> {
  await expect(page.locator('.pdfv-zoom-pct'), `百分比应显示 ${pct}`).toHaveText(pct);
  await expect.poll(async () => (await firstPageSize(page)).maxWidth, { message: `页 max-width 应为 ${maxWidth}` })
    .toBe(maxWidth);
  const size = await firstPageSize(page);
  expect(
    size.clientWidth,
    `显示宽应为 min(容器宽, ${maxWidth})`
  ).toBe(Math.min(size.containerWidth, parseInt(maxWidth, 10)));
}

test.describe('票147 · 简历缩放', () => {
  test('缩放栏逐档改宽、200% 高清重渲染、适应宽度回 100%、Resume 页无缩放栏', async ({
    page,
    request,
  }) => {
    const auth = await apiLogin(request);
    const role = 'E2E147ZoomRoleX';
    try {
      await seedAuth(page, auth);
      await setupAppWithPdf(page, role, ONE_PAGE);

      await expect(page.locator('.pdfv-zoombar'), 'PDF 简历区应出现缩放栏').toBeVisible();
      await expectZoom(page, '100%', '800px');

      await page.locator('.pdfv-zoom-in').click();
      await expectZoom(page, '125%', '1000px');

      await page.locator('.pdfv-zoom-in').click();
      await expectZoom(page, '150%', '1200px');

      await page.locator('.pdfv-zoom-in').click();
      await expectZoom(page, '175%', '1400px');

      await page.locator('.pdfv-zoom-in').click();
      await expectZoom(page, '200%', '1600px');
      // 200% 档停手 300ms 后应以 3.2 倍重渲染:图片源自然宽 ≈ 1958px(612pt × 3.2)
      await expect.poll(
          async () =>
            await page
              .locator('.pdfv-page')
              .first()
              .evaluate((el) => (el as HTMLImageElement).naturalWidth),
          { timeout: 15_000 }
        )
        .toBeGreaterThan(1900);

      await page.locator('.pdfv-zoom-fit').click();
      await expectZoom(page, '100%', '800px');

      await page.locator('.pdfv-zoom-out').click();
      await expectZoom(page, '75%', '600px');

      // 隔离:Resume 页不传 showZoomControls,不得出现缩放栏
      await page.goto('/resume');
      await expect(page.locator('.pdfv-zoombar'), 'Resume 页不得出现缩放栏').toHaveCount(0);
    } finally {
      const app = await findApplicationByRole(request, auth.token, role);
      if (app) await deleteApplication(request, auth.token, app.id);
    }
  });

  test('翻到第 2 页后缩放,重渲染后仍在第 2 页首附近', async ({ page, request }) => {
    const auth = await apiLogin(request);
    const role = 'E2E147ZoomPosRoleX';
    try {
      await seedAuth(page, auth);
      await setupAppWithPdf(page, role, TWO_PAGE);

      const pages = page.locator('.pdfv-pages');
      const second = page.locator('.pdfv-page').nth(1);
      const target = await second.evaluate((el) => (el as HTMLElement).offsetTop);
      await pages.evaluate((el, t) => {
        el.scrollTop = t;
      }, target);
      await expect.poll(async () => {
          const top = await pages.evaluate((el) => el.scrollTop);
          return Math.abs(top - target);
        }, { message: '应滚到第 2 页首' })
        .toBeLessThan(5);

      await page.locator('.pdfv-zoom-in').click();
      await page.locator('.pdfv-zoom-in').click();
      await expect(page.locator('.pdfv-zoom-pct'), '百分比应显示 150%').toHaveText('150%');
      // 等重渲染 + 位置恢复:滚动条应回到新布局下第 2 页首附近
      await expect
        .poll(
          async () => {
            const top = await pages.evaluate((el) => el.scrollTop);
            const off = await second.evaluate((el) => (el as HTMLElement).offsetTop);
            return Math.abs(top - off);
          },
          { timeout: 15_000 }
        )
        .toBeLessThan(5);
    } finally {
      const app = await findApplicationByRole(request, auth.token, role);
      if (app) await deleteApplication(request, auth.token, app.id);
    }
  });
});
