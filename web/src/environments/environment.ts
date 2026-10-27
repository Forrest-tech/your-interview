export const environment = {
  production: false,
  // ★ 2026-09-19 修复(Forrest:"连不上"):改走 **ng serve 相对路径代理**。
  //
  //   真因:此前写成 http://127.0.0.1:5200,前端**直连网关**,
  //   根本没走 proxy.conf.json —— 所以改代理、重启前端都不会有任何变化。
  //   浏览器直连 5200 还会额外吃一层跨域 / 容器网络的不确定性。
  //
  //   现在用空串 = 同源相对路径(浏览器打 4200,由 ng serve 代理转发):
  //     · 同源,零 CORS 问题;
  //     · /api/assessment 被 proxy.conf.json 直指宿主机 5266,
  //       绕开 Docker 容器到宿主机的转发;
  //     · 其余 /api、/hubs 仍走网关 5200。
  //   ⚠️ 相对路径**必须配合** `ng serve --proxy-config proxy.conf.json`;
  //      生产构建请改回真实网关地址(部署时由反向代理同源收口)。
  apiBaseUrl: '',

  // 票241 Logto 接入（占位，真实值只进部署环境的 .env / 环境配置，绝不入库）：
  //   enabled=false = 双轨关闭（旧登录一字不变）；灰度期开始时填入 endpoint +
  //   clientId 并把 enabled 改 true，前端才出现 Logto 登录/注册入口。
  //   audience = Console 里建的 API resource identifier（access token 的 scope
  //   载体就靠它；空则不发 resource 参数，token 里就没有业务 scope）。
  //   apiScopes = 同一 API resource 上定义的业务 scope 清单，必须写 Permissions.*
  //   的点值（例如 interviews.write），绝不能写 C# 常量名（InterviewsWrite）——
  //   后端 claims 门按点值精确匹配，常量名匹配不上就是零权限（票251 B7；
  //   归属：scope 定义归 Console，点值清单归本配置，见 docs/logto/SETUP.md §3）。
  //   redirectUri 为空时自动取当前 origin + '/auth/logto/callback'。
  logto: {
    enabled: false,
    endpoint: '',
    clientId: '',
    audience: '',
    apiScopes: [] as string[],
    redirectUri: ''
  },

  // 票241 割接期开关：true 时旧 /admin 页只读（隐藏新建/编辑/删除/启用停用动作，
  // 显示 Logto Console 链接）；写操作走 Console 或 /api/admin/logto 端点。
  logtoCutover: false,
  logtoConsoleUrl: 'http://localhost:3002',

  // 直连模式(排查网关问题时用):把 apiBaseUrl 换成具体服务端口
  servicePorts: {
    identity: 5262,
    jobs: 5263,
    interviews: 5264,
    knowledge: 5265,
    assessment: 5266,
    analytics: 5267,
    gateway: 5200
  }
};
