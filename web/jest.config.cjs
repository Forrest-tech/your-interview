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
    'practice-api\\.service$': '<rootDir>/test/jest-stub.ts',
    'ocr-api\\.service$': '<rootDir>/test/jest-stub.ts',
    'i18n\\.service$': '<rootDir>/test/jest-stub.ts',
  },
  collectCoverageFrom: [
    'src/app/features/playbook/components/brief/brief.helpers.ts',
    'src/app/features/playbook/redesign/intake/intake-panel.component.ts',
    'src/app/features/playbook/redesign/v2.helpers.ts',
    'src/app/core/models/radar.helpers.ts',
  ],
  coverageDirectory: '<rootDir>/coverage',
  testEnvironment: 'node',
};
