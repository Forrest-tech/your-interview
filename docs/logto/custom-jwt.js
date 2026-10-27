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
//   customData: data.user.customData / data.customData / context.user.customData
//   roles: data.user.roles / data.roles / token.roles(字符串或 {name} 对象数组)
// 上线前必须在 Console 测试页跑通三组样本(pending → 拒发;approved → 放行带声明;
// migrated → 放行带声明),字段名以测试页实测为准,不要猜。

const getCustomJwtClaims = async ({ token, data }) => {
  const user = data?.user ?? data ?? {};
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
