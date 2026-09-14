export const environment = {
  production: false,
  // 前端统一经 Gateway 访问后端,YARP 会按路径前缀转发到对应微服务。
  // 好处:前端只认一个地址,CORS / 鉴权 / 限流都在网关一处收口;
  // 服务增减端口变化时前端完全不用改。
  // ng serve 建议配合 proxy.conf.json 用相对路径;直连网关地址同样可用
  apiBaseUrl: 'http://127.0.0.1:5200',

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
