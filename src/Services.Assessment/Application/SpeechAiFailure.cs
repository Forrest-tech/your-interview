namespace YourInterview.Services.Assessment.Application;

/// <summary>
/// 票117:Assessment speech/TTS 面(发音评估 / 凭据测试与保存 / 示范朗读合成)
/// 调用 Azure Speech 失败的统一对外错误口径 —— 纯逻辑,单测锁定。
/// 与票116 MockAiFailure 同型,按票47(COD-20)定版形态:
///
/// 背景:改前控制器三处 catch 把 AzureAuthException / InvalidOperationException
/// 的 Message 直传 502 detail,而这些消息里拼有 Azure 上游响应 body
/// (PronunciationAssessor.PingWithAsync / SpeechSynthesizer.SynthesizeAsync
/// 把 Azure 回应截断拼入异常)或底层异常原文(发音评估重试聚合消息嵌
/// last.Message) —— 上游/底层原文直达终端用户。PracticeSettingsHandlers
/// 两处同病:把 AzureAuthException 原文塞进 Error.Description,经控制器
/// detail 下发。
///
/// 口径:
/// - 一律 502(上游 Azure 拒绝我们的凭据 / 调用失败 = 网关类错误;
///   绝不用 401/403 —— 前端拦截器会当作本站会话失效,见控制器既有注释)。
/// - title 沿用改前现值(「密钥或区域无效」/「发音评估失败」/「语音合成失败」),
///   code 沿用 handler 侧现值(Speech.InvalidCredential /
///   Speech.CredentialRejected),detail 一律为此处固定脱敏文案,
///   绝不拼接异常原文;异常原文只由调用方 LogWarning 进服务端日志。
/// - 未配置口径(503)不在本类:tts 未配置 503 与 Tts.NotConfigured 503
///   的 detail 是本仓自有定稿文案,逐字守恒、不经本类。
/// </summary>
public static class SpeechAiFailure
{
    /// <summary>上游调用失败的 HTTP 状态码:502 Bad Gateway(改前口径守恒)。</summary>
    public const int UpstreamStatusCode = 502;

    // ---------- 凭据被拒(AzureAuthException:测试凭据 / 保存凭据 / 已存凭据连通性 / 发音评估·合成的凭据分支) ----------

    public const string InvalidCredentialCode = "Speech.InvalidCredential";
    public const string CredentialRejectedCode = "Speech.CredentialRejected";
    public const string InvalidCredentialTitle = "密钥或区域无效";
    public const string InvalidCredentialDetail =
        "Azure 拒绝了该密钥或区域,请核对密钥与区域后重试。";

    /// <summary>保存凭据被拒专用 detail:沿用改前自有定稿前缀,去掉其后拼接的异常原文。</summary>
    public const string CredentialRejectedDetail =
        "Azure 拒绝了该密钥或区域,凭据未保存。";

    // ---------- 发音评估失败(InvalidOperationException:重试聚合等) ----------

    public const string PronunciationFailedTitle = "发音评估失败";
    public const string PronunciationFailedDetail =
        "发音评估失败:暂时无法完成评分,请稍后重试。";

    // ---------- 语音合成失败(InvalidOperationException:Azure 非成功状态等) ----------

    public const string SynthesisFailedTitle = "语音合成失败";
    public const string SynthesisFailedDetail =
        "语音合成失败:暂时无法完成合成,请稍后重试。";

    /// <summary>对外 ProblemDetails 口径(status/title/detail 三元组;本面改前无 code 扩展点位不新增)。</summary>
    public sealed record Problem(int StatusCode, string Title, string Detail);

    public static readonly Problem InvalidCredentialProblem =
        new(UpstreamStatusCode, InvalidCredentialTitle, InvalidCredentialDetail);

    public static readonly Problem PronunciationFailedProblem =
        new(UpstreamStatusCode, PronunciationFailedTitle, PronunciationFailedDetail);

    public static readonly Problem SynthesisFailedProblem =
        new(UpstreamStatusCode, SynthesisFailedTitle, SynthesisFailedDetail);

    /// <summary>speech 面失败类(只按异常通道区分,绝不读取异常 Message)。</summary>
    public enum Kind
    {
        /// <summary>AzureAuthException —— 凭据被上游拒绝。</summary>
        InvalidCredential,

        /// <summary>发音评估链的 InvalidOperationException。</summary>
        Pronunciation,

        /// <summary>语音合成链的 InvalidOperationException。</summary>
        Synthesis,
    }

    /// <summary>按失败类取对外口径。绝不读取 ex.Message —— 原文由调用方写日志。</summary>
    public static Problem For(Kind kind) => kind switch
    {
        Kind.InvalidCredential => InvalidCredentialProblem,
        Kind.Pronunciation => PronunciationFailedProblem,
        Kind.Synthesis => SynthesisFailedProblem,
        _ => SynthesisFailedProblem,
    };
}
