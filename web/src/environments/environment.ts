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
