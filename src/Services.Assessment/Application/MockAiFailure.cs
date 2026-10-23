using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Assessment.Application;

/// <summary>
/// 票116 F-H2:Mock 编排链(generate / score-ai)调用 AiGateway 失败的统一对外
/// 错误口径 —— 纯逻辑,单测锁定。按票47(COD-20)定版形态:
///
/// 背景:改前控制器 MapAiError 对非配置错误回 502 时 detail 直传
/// AiGatewayException.Message,而 AiGatewayClient 连接失败的消息含底层
/// HttpRequestException 原文(如代理/连接细节),内部信息直达终端用户
/// (真人化验收实测 detail 为沙盒代理原文)。
///
/// 口径:
/// - 配置错误(未保存 LLM 凭据)→ 503 + code=Mock.AiNotConfigured,文案逐字
///   沿用改前定版(前端据 code 分流「未配置」提示)。
/// - 其余上游失败(连接/超时/上游拒绝等)→ 502 + code 按失败类区分
///   (Mock.AiGenerationFailed / Mock.AiScoringFailed,沿用现值,前端只读
///   code 不读 detail),detail 一律为此处固定脱敏文案,绝不拼接异常原文;
///   异常原文只由控制器 LogWarning 进服务端日志。
/// - 不用 401/403:那在前端拦截器里意味着本站会话失效(票47/Assessment
///   speech 先例)。
/// </summary>
public static class MockAiFailure
{
    /// <summary>上游调用失败的 HTTP 状态码:502 Bad Gateway。</summary>
    public const int UpstreamStatusCode = 502;

    /// <summary>未配置凭据的 HTTP 状态码:503 Service Unavailable(改前口径守恒)。</summary>
    public const int NotConfiguredStatusCode = 503;

    public const string NotConfiguredCode = "Mock.AiNotConfigured";
    public const string NotConfiguredTitle = "AI 未配置";
    public const string NotConfiguredDetail = "AI 服务尚未配置凭据,请先在设置里保存 LLM 密钥后再试。";

    public const string GenerationFailedCode = "Mock.AiGenerationFailed";
    public const string GenerationFailedTitle = "AI 出题失败";
    public const string GenerationFailedDetail = "AI 出题失败:暂时无法完成出题,请稍后重试。";

    public const string ScoringFailedCode = "Mock.AiScoringFailed";
    public const string ScoringFailedTitle = "AI 评分失败";
    public const string ScoringFailedDetail = "AI 评分失败:暂时无法完成评分,请稍后重试。";

    /// <summary>对外 ProblemDetails 口径(status/code/title/detail 四元组)。</summary>
    public sealed record Problem(int StatusCode, string Code, string Title, string Detail);

    public static readonly Problem NotConfiguredProblem =
        new(NotConfiguredStatusCode, NotConfiguredCode, NotConfiguredTitle, NotConfiguredDetail);

    public static readonly Problem GenerationFailedProblem =
        new(UpstreamStatusCode, GenerationFailedCode, GenerationFailedTitle, GenerationFailedDetail);

    public static readonly Problem ScoringFailedProblem =
        new(UpstreamStatusCode, ScoringFailedCode, ScoringFailedTitle, ScoringFailedDetail);

    /// <summary>
    /// 把 AiGateway 异常映射为对外口径。只看 IsConfigurationError 标志与
    /// 失败类(出题/评分),绝不读取 ex.Message —— 原文由调用方写日志。
    /// </summary>
    public static Problem For(AiGatewayException ex, bool scoring)
        => ex.IsConfigurationError
            ? NotConfiguredProblem
            : scoring ? ScoringFailedProblem : GenerationFailedProblem;
}
