namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// 票241:旧 Identity 发行开关(配置节 Auth;灰度期与收敛日的两个硬开关)。
///   · LegacyRegistrationEnabled(默认 true):灰度期起点置 false —— 旧 POST /api/auth/register
///     服务端直接回 410(指向 Logto 注册),而不是只藏前端入口。旧注册接口签发即登录、
///     无验证无审批,灰度期留着它等于给审批门开旁门。
///   · IssuanceEnabled(默认 true):收敛日置 false —— 旧 POST /api/auth/login 与
///     /api/auth/refresh 回 410,用户在下次刷新时被迫走 Logto 重登录(不是全量踢下线,
///     上限 = access token 有效期 + 刷新周期)。回滚时翻回 true + Identity 重启即恢复签发。
/// 两个开关都只是控制器层面的门:旧 token 的校验口径由各服务的双 scheme 选择器管
/// (Logto:RejectOldIssuer),不要把两套开关混为一谈。
/// </summary>
public sealed class LegacyAuthOptions
{
    public const string SectionName = "Auth";

    public bool LegacyRegistrationEnabled { get; set; } = true;

    public bool IssuanceEnabled { get; set; } = true;
}
