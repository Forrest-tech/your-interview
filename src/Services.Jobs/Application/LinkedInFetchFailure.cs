namespace YourInterview.Services.Jobs.Application;

/// <summary>
/// 票47(COD-20):FetchLinkedIn 失败的统一错误口径 —— 纯逻辑,单测锁定。
///
/// 背景:改前抓取失败返回 HTTP 200 + 异常原文(`fetch_error: ` + ex.Message,
/// 或 fetcher 透传的 ex.Message),监控按状态码感知不到失败,内部细节还外泄,
/// 与全站 RFC 9457 ProblemDetails 口径不一致。
///
/// 口径(论证见 evidence-47/STATUS.md):
/// - 抓取失败 = 本服务作为网关调用上游(LinkedIn 页面)失败,不是本服务内部
///   故障、也不是客户端入参错误 → 502 Bad Gateway(Assessment speech 面
///   「上游拒收/未按预期响应 → 502」先例;ApiClient 亦有 502 专用兜底文案)。
///   不用 401/403:那在前端拦截器里意味着本站会话失效,会触发刷新/登出。
/// - 两类失败同为 502,以 ProblemDetails 的 code 扩展区分(COD-05 先例):
///   needs_login(目标把页面重定向到登录/校验页,用户可去服务器登录一次)
///   与一般抓取失败(网络不可达/超时/目标错误页/内容为空/未预料异常)。
/// - detail 只用此处定稿文案;fetcher 的原始 error(含异常原文)只进服务端日志,
///   绝不进入响应体。分类器对哨兵值逐字精确匹配,不做模糊/大小写折叠 ——
///   fetcher 产出什么样就认什么样,变体一律落一般失败,避免误报「需要登录」。
/// </summary>
public static class LinkedInFetchFailure
{
    /// <summary>上游抓取失败的 HTTP 状态码:502 Bad Gateway。</summary>
    public const int StatusCode = 502;

    /// <summary>ProblemDetails code:LinkedIn 要求先登录(用户可执行动作:在服务器上登录一次)。</summary>
    public const string NeedsLoginCode = "linkedin_needs_login";

    /// <summary>ProblemDetails code:一般抓取失败(网络/目标/内容/未预料异常)。</summary>
    public const string FetchFailedCode = "linkedin_fetch_failed";

    /// <summary>ProblemDetails code:URL 不是 LinkedIn 职位页(400 入参校验;值沿用改前匿名体的 error 值,不丢信息)。</summary>
    public const string NotLinkedInJobUrlCode = "not_linkedin_job_url";

    /// <summary>LinkedInFetcher 的哨兵错误值:页面被重定向到登录/校验页。</summary>
    public const string NeedsLoginSentinel = "needs_login";

    public enum Kind
    {
        NeedsLogin,
        FetchFailed
    }

    /// <summary>
    /// 把 fetcher 的原始 error 分类。只认逐字哨兵 <see cref="NeedsLoginSentinel"/>;
    /// 其余一切(含 null/空、"empty" 哨兵、任意异常原文)一律 FetchFailed。
    /// </summary>
    public static Kind Classify(string? fetcherError)
        => fetcherError == NeedsLoginSentinel ? Kind.NeedsLogin : Kind.FetchFailed;

    public static string CodeFor(Kind kind)
        => kind == Kind.NeedsLogin ? NeedsLoginCode : FetchFailedCode;

    public static string TitleFor(Kind kind)
        => kind == Kind.NeedsLogin ? "LinkedIn login required" : "LinkedIn fetch failed";

    /// <summary>用户可读的定稿文案。绝不拼接 fetcherError —— 原文只进服务端日志。</summary>
    public static string DetailFor(Kind kind)
        => kind == Kind.NeedsLogin
            ? "LinkedIn requires sign-in before this job page can be fetched. Sign in once on the server, then try again."
            : "Failed to fetch the LinkedIn job page. Please try again later, or paste the job description text manually.";
}
