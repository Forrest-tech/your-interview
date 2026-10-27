// 票241:Logto Custom JWT 脚本(access token,Console → Custom JWT 原样粘贴)。
//
// 分层定位(必读):本脚本只是 Logto 侧的快速拒绝,不是应用侧的权威门。
// 应用侧权威门是各服务共享的 claims 转换层 LogtoClaimsGate(按 identity.logto_user_mapping
// 镜像行每请求判定)。脚本漏掉或未部署时,claims 门仍按镜像零权限拒绝未批账号。
// 所以本脚本的拒绝条件故意收窄:只拒绝 approval === 'pending' 的显式待审批用户;
// approval 缺失的用户不由脚本拒绝(由 claims 门按镜像判定),避免脚本误伤
// Console 创建的账号或字段形态差异导致的全体拒登。
//
// 字段读取做了多形态兼容(脚本上下文在不同 Logto 版本/配置下字段位置有差异):
//   customData: context.user.customData(Logto 1.45.0 实测) / data.user.customData / data.customData
//   roles: context.user.roles(Logto 1.45.0 实测,{name} 对象数组) / data.user.roles / data.roles / token.roles
// 上线前必须在 Console 测试页跑通三组样本(pending → 拒发;approved → 放行带声明;
// migrated → 放行带声明),字段名以测试页实测为准,不要猜。
// 票 257:1.45.0 的 worker 只传 {token, context, environmentVariables},data 是
// undefined——旧版只解构 {token, data} 时脚本恒返回 {},claims 与 pending 拒绝全丢。
// 另注意:worker 把 api 作为参数注入({ ...payload, api }),不是全局变量——
// 旧脚本只写裸 `api` 时 typeof 恒为 undefined,拒绝被静默跳过(claims 却正常,
// 这正是 live token 实测暴露的第二处形态差)。故 api 必须随参数解构。

const getCustomJwtClaims = async ({ token, context, data, api }) => {
  const user = context?.user ?? data?.user ?? data ?? {};
  const customData = user?.customData ?? data?.customData ?? {};

  const approval = customData?.approval;
  // 显式待审批:Logto 侧直接拒发 token(用户在登录页即被挡下,不必等到调业务接口)。
  if (approval === 'pending' && typeof api !== 'undefined' && api?.denyAccess) {
    api.denyAccess();
  }

  // roles:只透传,不做权限判定(权限判定在 claims 门里按 scope claim 做)。
  const rawRoles = user?.roles ?? data?.roles ?? token?.roles ?? [];
  const roles = (Array.isArray(rawRoles) ? rawRoles : [rawRoles])
    .map((r) => (typeof r === 'string' ? r : r?.name))
    .filter(Boolean);

  const claims = {};
  if (customData?.legacyUserId) {
    claims.legacy_user_id = customData.legacyUserId;
  }
  if (roles.length > 0) {
    claims.roles = roles;
  }
  return claims;
};
