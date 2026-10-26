import { defineConfig, devices } from '@playwright/test';

/**
 * E2E 冒烟回归套(票 89 / 系统提升 A2)。
 *
 * 一条命令:npm run e2e(在 web/ 下)。产物:html 报告 + 失败截图。
 *
 * 服务起法不在本套件范围(B4 落地后切到一条命令):这里只做**依赖等待与健康检查**,
 * 半起状态下不许开跑 —— 否则会跑出假绿/假红,那比不跑更糟。
 */
const WEB = process.env['E2E_WEB'] ?? 'http://127.0.0.1:4200';

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/health.setup',
  outputDir: './e2e/.results',
  // 黄金路径之间共享同一账号与后端数据,串行更可预测,也避免互相踩数据
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  reporter: [
    ['list'],
    ['html', { outputFolder: 'e2e/.report', open: 'never' }],
  ],
  use: {
    baseURL: WEB,
    ...devices['Desktop Chrome'],
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'off',
  },
});
