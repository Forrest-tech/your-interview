// 票 257 回归 harness:对同目录 custom-jwt.js 做 Logto 1.45.0 worker 输入模拟。
//
// 设计原则:
// - 不复制脚本逻辑:只读真实脚本源码并在 node:vm 里跑,脚本改坏立刻红。
// - 1.45.0 的 worker 只传 {token, context, environmentVariables},data 是
//   undefined——旧版脚本只解构 {token, data} 时恒返回 {},本套件在无 data 键的
//   输入下断言 claims 与拒绝,旧脚本跑本套件必红(已用 HEAD 版脚本做阴性对照)。
// - denyAccess 语义假设:Logto 在 denyAccess 被调用时中止发 token;harness
//   默认用抛错 stub 建模这一点(断言 rejects + deny 被调用),并另有一例用
//   不抛错 stub 只断言 deny 被调用——两种实现语义下“拒绝一定发生”这一负载
//   断言都成立。Console 测试页三样本仍是强制验收,harness 不能替代它。
//
// 运行:node --test docs/logto/custom-jwt.test.js (CI 前端 job 内亦跑,见 ci.yml)。

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SCRIPT = fs.readFileSync(path.join(__dirname, 'custom-jwt.js'), 'utf8');

// Worker 语义(票 257 live 实测 + 1.45.0 源码核实):worker 把 api 作为调用参数
// 注入({ ...payload, api }),不是全局变量。旧 harness 把 api 放进 vm 全局,
// 掩盖了旧脚本裸 `api` 引用在真 worker 里恒为 undefined 的问题——harness
// 必须按真 worker 的样子传参,否则它测的不是生产行为。
function loadScript({ denyThrows }) {
  let denyCalls = 0;
  const api = {
    denyAccess() {
      denyCalls += 1;
      if (denyThrows) {
        throw new Error('denied by test stub');
      }
    },
  };
  const ctx = vm.createContext({});
  const raw = vm.runInContext(`${SCRIPT}\n;getCustomJwtClaims;`, ctx);
  // vm realm 产出的对象原型与宿主不同,deepStrictEqual 会误判;claims 本就
  // 必须是 JSON 可序列化(JWT 载荷),过一次 JSON 边界既归一化原型又验了这点。
  const invoke = async (input) =>
    JSON.parse(JSON.stringify(await raw({ ...input, api })));
  return { invoke, denied: () => denyCalls };
}

const user145 = (customData, roles) => ({
  token: {},
  context: { user: { customData, roles } },
  environmentVariables: {},
});

test('1.45.0 shape (no data key): pending user is denied', async () => {
  const { invoke: fn, denied } = loadScript({ denyThrows: true });
  await assert.rejects(
    fn(
      user145(
        { approval: 'pending', legacyUserId: 'u-pending' },
        [{ id: 'r1', name: 'User', description: '', scopes: [] }],
      ),
    ),
  );
  assert.equal(denied(), 1);
});

test('1.45.0 shape: pending deny is called even if denyAccess does not throw', async () => {
  const { invoke: fn, denied } = loadScript({ denyThrows: false });
  await fn(
    user145(
      { approval: 'pending', legacyUserId: 'u-pending' },
      [{ id: 'r1', name: 'User', description: '', scopes: [] }],
    ),
  );
  assert.equal(denied(), 1);
});

test('1.45.0 shape (no data key): approved user gets claims, no deny', async () => {
  const { invoke: fn, denied } = loadScript({ denyThrows: true });
  const claims = await fn(
    user145(
      { approval: 'approved', legacyUserId: 'u-approved' },
      [
        { id: 'r1', name: 'User', description: '', scopes: [] },
        { id: 'r2', name: 'Admin', description: '', scopes: [] },
      ],
    ),
  );
  assert.deepEqual(claims, { legacy_user_id: 'u-approved', roles: ['User', 'Admin'] });
  assert.equal(denied(), 0);
});

test('1.45.0 shape: migrated user (legacyUserId, no approval flag) gets claims, no deny', async () => {
  const { invoke: fn, denied } = loadScript({ denyThrows: true });
  const claims = await fn(
    user145({ legacyUserId: 'u-migrated' }, [{ id: 'r1', name: 'User', description: '', scopes: [] }]),
  );
  assert.deepEqual(claims, { legacy_user_id: 'u-migrated', roles: ['User'] });
  assert.equal(denied(), 0);
});

test('1.45.0 shape: data === undefined never throws', async () => {
  const { invoke: fn } = loadScript({ denyThrows: true });
  const input = user145({ approval: 'approved', legacyUserId: 'u-nodata' }, []);
  assert.ok(!('data' in input));
  const claims = await fn(input);
  assert.deepEqual(claims, { legacy_user_id: 'u-nodata' });
});

test('older shape (context === undefined): data fallback still works', async () => {
  const { invoke: fn, denied } = loadScript({ denyThrows: true });
  const claims = await fn({
    token: {},
    data: {
      user: { customData: { approval: 'approved', legacyUserId: 'u-old' }, roles: ['Admin'] },
    },
  });
  assert.deepEqual(claims, { legacy_user_id: 'u-old', roles: ['Admin'] });
  assert.equal(denied(), 0);
});

test('older shape: pending via data fallback is denied', async () => {
  const { invoke: fn, denied } = loadScript({ denyThrows: true });
  await assert.rejects(
    fn({
      token: {},
      data: { user: { customData: { approval: 'pending', legacyUserId: 'u-old-pending' } } },
    }),
  );
  assert.equal(denied(), 1);
});

test('roles mapping handles mixed string and {name} objects', async () => {
  const { invoke: fn } = loadScript({ denyThrows: true });
  const claims = await fn(
    user145({ approval: 'approved' }, ['Viewer', { name: 'User' }, { id: 'x', name: 'PowerUser' }]),
  );
  assert.deepEqual(claims, { roles: ['Viewer', 'User', 'PowerUser'] });
});

test('no customData, no roles: empty claims, no deny, no throw', async () => {
  const { invoke: fn, denied } = loadScript({ denyThrows: true });
  const claims = await fn(user145({}, []));
  assert.deepEqual(claims, {});
  assert.equal(denied(), 0);
});
