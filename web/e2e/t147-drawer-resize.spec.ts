import { expect, test } from '@playwright/test';
import {
  apiLogin,
  createApplicationViaSmartAdd,
  deleteApplication,
  findApplicationByRole,
  openDetailDialog,
  seedAuth,
} from './helpers';

/**
 * 票 147 E2E:详情抽屉左边缘拖拽改宽 + 键盘调宽 + 窄屏规则。
 *
 * 断言口径:overlay pane(.detail-drawer-panel)的实测宽度。
 * 默认视口 1280x720(Desktop Chrome):初始宽 66vw=845px 钳制到拖拽范围
 * [900, min(1600, 92vw)=1177] → 900px;拖到 x=100 → 1180 钳制到 1177;
 * 拖到 x=1000 → 280 钳制到 900。
 */
const ROLE = 'E2E147DragRoleX';
const COMPANY = 'DragE2E147Co';
const JD_TEXT = `Senior Backend Engineer\n\nCompany: ${COMPANY}\nSalary: $140,000 - $180,000 per year\nLocation: Toronto, ON (Hybrid)\n\nWe are building the commerce platform of the future.`;

async function paneWidth(page: import('@playwright/test').Page): Promise<number> {
  const box = await page.locator('.detail-drawer-panel').boundingBox();
  if (!box) throw new Error('[e2e147] 找不到抽屉面板');
  return Math.round(box.width);
}

async function dragHandleTo(page: import('@playwright/test').Page, targetX: number): Promise<void> {
  // 票 156：抽屉有 0.25s 滑入动画（.detail-drawer-panel drawerSlideIn）；
  // 滑入期间面板宽已是目标宽、但 x 还在移动，初始宽断言拦不住，
  // 此时量手柄会量到半路位置、mouse.down 落到面板内容上、拖动无声无息失败（CI 首跑抖动）。
  // 故先等面板 x 与宽连续两次读数一致（100ms 间隔）再动手柄；断言目标值本身不动。
  await expect.poll(async () => {
    const pane = page.locator('.detail-drawer-panel');
    const b1 = await pane.boundingBox();
    if (!b1) throw new Error('[e2e147] 找不到抽屉面板');
    await page.waitForTimeout(100);
    const b2 = await pane.boundingBox();
    if (!b2) throw new Error('[e2e147] 找不到抽屉面板');
    return Math.round(b1.x) === Math.round(b2.x) && Math.round(b1.width) === Math.round(b2.width)
      ? 'stable' : 'moving';
  }, { message: '拖动前抽屉位置应稳定（滑入动画结束）' }).toBe('stable');
  const handle = page.locator('.drawer-resize-handle');
  const hbox = await handle.boundingBox();
  if (!hbox) throw new Error('[e2e147] 找不到拖拽手柄');
  await page.mouse.move(hbox.x + 3, hbox.y + hbox.height / 2);
  await page.mouse.down();
  await page.mouse.move(targetX, hbox.y + hbox.height / 2, { steps: 10 });
  await page.mouse.up();
}

test.describe('票147 · 详情抽屉拖拽改宽', () => {
  test('拖拽到两端钳制、松手保持、关闭重开记忆宽度', async ({ page, request }) => {
    const auth = await apiLogin(request);
    try {
      await seedAuth(page, auth);
      await createApplicationViaSmartAdd(page, ROLE, JD_TEXT);
      await expect(page.getByText(COMPANY).first(), '保存后列表应出现新投递').toBeVisible({
        timeout: 30_000,
      });

      const dialog = await openDetailDialog(page, ROLE);
      await expect(page.locator('.drawer-resize-handle'), '宽屏下手柄应可见').toBeVisible();
      await expect.poll(() => paneWidth(page), { message: '初始宽应为钳制后的 900px' }).toBe(900);

      // 拖到左端:1180 → 钳制到 1177
      await dragHandleTo(page, 100);
      await expect.poll(() => paneWidth(page), { message: '拖到左端应到 1177px' }).toBe(1177);
      await expect(dialog, '拖动全程抽屉不得关闭').toBeVisible();

      // 拖到右端:280 → 钳制到 900
      await dragHandleTo(page, 1000);
      await expect.poll(() => paneWidth(page), { message: '拖到右端应到 900px' }).toBe(900);

      // 关闭重开:记忆宽度 900 应恢复(点遮罩层关闭,拖动不得破坏关闭行为)
      await page.mouse.click(100, 360);
      await expect(page.locator('mat-dialog-container'), '点遮罩层应关闭抽屉').toHaveCount(0);
      await openDetailDialog(page, ROLE);
      await expect.poll(() => paneWidth(page), { message: '重开应恢复记忆宽度 900px' }).toBe(900);
    } finally {
      const app = await findApplicationByRole(request, auth.token, ROLE);
      if (app) await deleteApplication(request, auth.token, app.id);
    }
  });

  test('键盘 ←/→ 调宽 40px 步进、窄屏手柄隐藏且满宽', async ({ page, request }) => {
    const auth = await apiLogin(request);
    try {
      await seedAuth(page, auth);
      await createApplicationViaSmartAdd(page, ROLE, JD_TEXT);
      await expect(page.getByText(COMPANY).first(), '保存后列表应出现新投递').toBeVisible({
        timeout: 30_000,
      });

      await openDetailDialog(page, ROLE);
      await expect.poll(() => paneWidth(page), { message: '初始宽应为 900px' }).toBe(900);

      const handle = page.locator('.drawer-resize-handle');
      await handle.focus();
      await page.keyboard.press('ArrowLeft');
      await expect.poll(() => paneWidth(page), { message: '← 应加宽 40px 到 940px' }).toBe(940);
      await page.keyboard.press('ArrowRight');
      await expect.poll(() => paneWidth(page), { message: '→ 应减宽 40px 回 900px' }).toBe(900);

      // 窄屏:手柄消失、抽屉满宽 800px
      await page.setViewportSize({ width: 800, height: 720 });
      await expect(page.locator('.drawer-resize-handle'), '窄屏下手柄应隐藏').toHaveCount(0);
      await expect.poll(() => paneWidth(page), { message: '窄屏下抽屉应满宽 800px' }).toBe(800);
    } finally {
      const app = await findApplicationByRole(request, auth.token, ROLE);
      if (app) await deleteApplication(request, auth.token, app.id);
    }
  });
});
