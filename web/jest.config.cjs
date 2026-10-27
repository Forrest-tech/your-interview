/**
 * 票64 ARC-08c:前端 Jest 基建（最轻路线）。
 * - 只跑 src 下与源码同目录的 *.spec.ts（e2e/*.spec.ts 是 Playwright,不在此列）;
 * - ts-jest 直跑 TS,不引入组件 TestBed;被测对象均为纯函数/状态逻辑;
 * - intake-panel.component.ts 的 Angular/服务值依赖经 moduleNameMapper
 *   映射到 test/jest-stub.ts 替身（见该文件头注）,产品代码零改动;
 * - 覆盖率首批范围口径:collectCoverageFrom 只含四个点名文件,
 *   `npm run test:coverage`（jest --coverage）产出逐文件数字。
 */
module.exports = {
  rootDir: __dirname,
  roots: ['<rootDir>/src'],
  testMatch: ['**/*.spec.ts'],
  transform: {
    '^.+\\.ts$': ['ts-jest', { tsconfig: '<rootDir>/tsconfig.jest.json' }],
  },
  moduleNameMapper: {
    '^@angular/(.*)$': '<rootDir>/test/jest-stub.ts',
    'playbook-v2\\.api$': '<rootDir>/test/jest-stub.ts',
    // ★ 票124:practice-api.service 的替身**只对 intake-panel 一处生效**。
    //   原因:票124 的 F1 要测**真实** PracticeApi(URL/方法逐字、treeVersion 采纳、
    //   错误原样上抛),若全局映射到替身就测不到真身。
    //   键按**请求串**匹配 —— 这里只命中 intake-panel.component.ts 的那一条相对路径,
    //   其余引用(含 F1 spec 自己的)仍解析到真实源文件。
    '^\\.\\./\\.\\./\\.\\./\\.\\./core/api/practice-api\\.service$': '<rootDir>/test/jest-stub.ts',
    'ocr-api\\.service$': '<rootDir>/test/jest-stub.ts',
    'i18n\\.service$': '<rootDir>/test/jest-stub.ts',
  },
  collectCoverageFrom: [
    'src/app/features/playbook/components/brief/brief.helpers.ts',
    'src/app/features/playbook/redesign/intake/intake-panel.component.ts',
    'src/app/features/playbook/redesign/v2.helpers.ts',
    'src/app/core/models/radar.helpers.ts',
    // 票255 TQ-1 前端覆盖率基线 v1：收官七票（123/124/125/130/131/132/133）并集 37 文件。
    // 其中 intake-panel.component.ts 已在上方票64四条目中，36 条为新增，40 条唯一路径覆盖全部 37 并集文件。
    // 123（18 并集首批，intake-panel 已在上）：
    'src/app/features/auth/human-check.component.ts',
    'src/app/features/dashboard/dashboard.component.html',
    'src/app/features/playbook/playbook-calendar.component.html',
    'src/app/features/playbook/playbook-calendar.component.scss',
    'src/app/features/playbook/playbook-detail.component.html',
    'src/app/features/playbook/playbook-detail.component.scss',
    'src/app/features/playbook/playbook.component.html',
    'src/app/features/playbook/playbook.component.scss',
    'src/app/features/techstack/category-manager/category-manager.component.scss',
    'src/app/features/techstack/techstack.component.html',
    'src/app/features/techstack/techstack.component.scss',
    'src/app/features/tracker/application-detail-dialog.component.ts',
    'src/app/features/tracker/tracker.component.html',
    'src/app/features/tracker/tracker.component.scss',
    'src/app/layout/shell.component.scss',
    'src/app/shared/doc-viewer/doc-viewer.component.scss',
    'src/styles.scss',
    // 124（+12，styles.scss 已在 123 中）：
    'src/app/core/api/practice-api.service.ts',
    'src/app/core/i18n/i18n.service.ts',
    'src/app/features/ai-practice/ai-practice.component.html',
    'src/app/features/ai-practice/ai-practice.component.ts',
    'src/app/features/ai-practice/material-trash-dialog.component.html',
    'src/app/features/ai-practice/material-trash-dialog.component.scss',
    'src/app/features/ai-practice/material-trash-dialog.component.ts',
    'src/app/features/ai-practice/material-trash.helpers.ts',
    'src/app/features/ai-practice/practice-material-tree.ts',
    'src/app/shared/material-tree/material-tree.component.html',
    'src/app/shared/material-tree/material-tree.component.scss',
    'src/app/shared/material-tree/material-tree.component.ts',
    // 125（+1）：
    'src/app/features/ai-practice/practice-grading-session.ts',
    // 130（+2）：
    'src/app/features/ai-practice/dictionary-card.component.ts',
    'src/app/features/ai-practice/practice-dictionary.ts',
    // 131（+1）：
    'src/app/features/ai-practice/dictionary.helpers.ts',
    // 132（+0，practice-grading-session.ts 已在 125 中）：
    // 133（+3，i18n.service.ts 已在 124 中）：
    'src/app/features/playbook/redesign/round/prep-report.component.ts',
    'src/app/features/playbook/redesign/round/prep-report.helpers.ts',
    'src/app/features/playbook/redesign/v2.models.ts',
  ],
  coverageDirectory: '<rootDir>/coverage',
  testEnvironment: 'node',
};
