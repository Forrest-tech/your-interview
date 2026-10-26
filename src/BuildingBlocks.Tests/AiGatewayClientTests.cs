using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.BuildingBlocks.Ai;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.BuildingBlocks.Tests;

// ============================================================================
//  票38 ARC-10:单一 adapter AiGatewayClient 的行为特征测试。
//  方法体逐字承继 Jobs/Interviews 两副本 —— 本套件把改前语义锁死:
//  成功解析 / JWT 透传 / 错误分类(isConfig) / 文案 / 超时双文案差异。
//  boundary 类别标注:空/null(空 Text、null 体、无 HttpContext)、
//  非 JSON 错误体、配置回落(空/空白 BaseUrl)、空集合(检索结果为空)。
// ============================================================================
public sealed class AiGatewayClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastAuth { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastAuth = request.Headers.Authorization?.ToString()
                ?? (request.Headers.TryGetValues("Authorization", out var v) ? string.Join(" ", v) : null);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, LastBody);
        }
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw ex;
    }

    private static AiGatewayClient NewClient(HttpMessageHandler handler, bool withAuth = true)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5268/") };
        var accessor = new HttpContextAccessor();
        if (withAuth)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = "Bearer test-jwt";
            accessor.HttpContext = ctx;
        }
        return new AiGatewayClient(http, accessor, NullLogger<AiGatewayClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---------------- CompleteAsync:成功与透传 ----------------

    [Fact] // 正常路径 + JWT 透传 + 请求体字段口径
    public async Task Complete_Success_ReturnsDto_AndForwardsJwt()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"text":"hello","model":"m1","promptTokens":3,"completionTokens":4,"latencyMs":9}"""));
        var client = NewClient(stub);

        var r = await client.CompleteAsync("cover-letter", "prompt-x", null, 0.5, 100, default);

        Assert.Equal("hello", r.Text);
        Assert.Equal("m1", r.Model);
        Assert.Equal(3, r.PromptTokens);
        Assert.Equal("/api/ai/complete", stub.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer test-jwt", stub.LastAuth);
        Assert.Contains("\"purpose\":\"cover-letter\"", stub.LastBody);
        Assert.Contains("\"systemPrompt\":null", stub.LastBody); // boundary: null systemPrompt 原样序列化
    }

    [Fact] // boundary: 无 HttpContext(后台线程) —— 不带 Authorization 仍可调用
    public async Task Complete_NoHttpContext_SendsWithoutAuth()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"text":"t","model":"m","promptTokens":null,"completionTokens":null,"latencyMs":null}"""));
        var client = NewClient(stub, withAuth: false);

        var r = await client.CompleteAsync("p", "x", "sys", null, null, default);

        Assert.Equal("t", r.Text);
        Assert.Null(stub.LastAuth);
    }

    // ---------------- CompleteAsync:错误语义(改前口径锁定) ----------------

    [Fact] // isConfig:detail 含「尚未配置」
    public async Task Complete_NotConfiguredDetail_IsConfigurationError()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.BadRequest,
            """{"detail":"尚未配置 AI 凭据","title":"Bad Request"}"""));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).CompleteAsync("p", "x", null, null, null, default));
        Assert.True(ex.IsConfigurationError);
        Assert.Equal("尚未配置 AI 凭据", ex.Message);
    }

    [Fact] // isConfig:响应体任意处含 Ai.NotConfigured 标记
    public async Task Complete_NotConfiguredMarkerInRaw_IsConfigurationError()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.BadRequest,
            """{"detail":"gateway error","code":"Ai.NotConfigured"}"""));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).CompleteAsync("p", "x", null, null, null, default));
        Assert.True(ex.IsConfigurationError);
    }

    [Fact] // boundary: 非 JSON 错误体 → 状态码兜底文案,非配置错误
    public async Task Complete_NonJsonError_FallsBackToStatusMessage()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.InternalServerError, "boom-not-json"));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).CompleteAsync("p", "x", null, null, null, default));
        Assert.False(ex.IsConfigurationError);
        Assert.Equal("AiGateway 返回 500。", ex.Message);
    }

    [Fact] // boundary: 200 但 Text 空白 → 空内容异常
    public async Task Complete_EmptyText_Throws()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"text":"  ","model":"m","promptTokens":null,"completionTokens":null,"latencyMs":null}"""));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).CompleteAsync("p", "x", null, null, null, default));
        Assert.Equal("AI 返回了空内容。请重试,或检查所选模型是否可用。", ex.Message);
    }

    [Fact] // boundary: 200 体为 JSON null → 同空内容异常
    public async Task Complete_NullBody_Throws()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK, "null"));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).CompleteAsync("p", "x", null, null, null, default));
        Assert.Equal("AI 返回了空内容。请重试,或检查所选模型是否可用。", ex.Message);
    }

    [Fact] // 连接失败:文案前缀 + 内层异常保留
    public async Task Complete_ConnectionFailure_WrapsHttpRequestException()
    {
        var inner = new HttpRequestException("refused");
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(new ThrowingHandler(inner)).CompleteAsync("p", "x", null, null, null, default));
        Assert.StartsWith("无法连接 AiGateway:", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.False(ex.IsConfigurationError);
    }

    [Fact] // 超时(非调用方取消):Complete 路径长文案 —— 与 SendJson 路径的差异锁定
    public async Task Complete_Timeout_UsesLongMessage()
    {
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(new ThrowingHandler(new TaskCanceledException("t/o")))
                .CompleteAsync("p", "x", null, null, null, default));
        Assert.Equal("AI 生成超时。可稍后重试,或在设置里换更快的模型。", ex.Message);
    }

    // ---------------- 备考数据源(SendJsonAsync 路径,原 Interviews 语义) ----------------

    [Fact]
    public async Task GetSettings_Success_ParsesAllFields()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"questionBankEnabled":true,"companyBackgroundEnabled":false,"webExperiencesEnabled":true,"glassdoorEnabled":false,"searchProvider":"tavily","hasSearchKey":true,"maskedSearchKey":"sk-***","updatedAt":"2026-10-01T00:00:00+00:00"}"""));
        var s = await NewClient(stub).GetPrepSourceSettingsAsync(default);
        Assert.True(s.QuestionBankEnabled);
        Assert.False(s.CompanyBackgroundEnabled);
        Assert.Equal("tavily", s.SearchProvider);
        Assert.True(s.HasSearchKey);
        Assert.Equal("/api/ai/prep-sources/settings", stub.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer test-jwt", stub.LastAuth);
    }

    [Fact] // boundary: 200 体 null → 专属空配置异常
    public async Task GetSettings_NullBody_Throws()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK, "null"));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).GetPrepSourceSettingsAsync(default));
        Assert.Equal("AiGateway 返回了空的备考数据源配置。", ex.Message);
    }

    [Fact] // 差异锁定:SendJson 路径即使 detail 含「尚未配置」也**不**标 isConfig(改前 Interviews 口径)
    public async Task GetSettings_Error_NeverConfigurationError()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.BadRequest,
            """{"detail":"尚未配置搜索服务"}"""));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).GetPrepSourceSettingsAsync(default));
        Assert.False(ex.IsConfigurationError);
        Assert.Equal("尚未配置搜索服务", ex.Message);
    }

    [Fact] // 超时:SendJson 路径短文案(与 Complete 长文案的差异锁定)
    public async Task GetSettings_Timeout_UsesShortMessage()
    {
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(new ThrowingHandler(new TaskCanceledException("t/o")))
                .GetPrepSourceSettingsAsync(default));
        Assert.Equal("AiGateway 请求超时。", ex.Message);
    }

    [Fact] // boundary: 检索结果空集合原样返回
    public async Task Search_Success_EmptyResults()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"source":"web","status":"noData","provider":"tavily","results":[],"message":null}"""));
        var r = await NewClient(stub).SearchPrepSourcesAsync("web", "Acme", null, null, default);
        Assert.Equal("noData", r.Status);
        Assert.Empty(r.Results);
        Assert.Equal("/api/ai/prep-sources/search", stub.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"company\":\"Acme\"", stub.LastBody);
    }

    [Fact]
    public async Task Search_Success_WithResults()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"source":"web","status":"fetched","provider":"tavily","results":[{"title":"面经","url":"https://x.example/1","snippet":"s"}],"message":null}"""));
        var r = await NewClient(stub).SearchPrepSourcesAsync("web", "Acme", "SDE", "R1", default);
        Assert.Single(r.Results);
        Assert.Equal("面经", r.Results[0].Title);
    }

    [Fact] // boundary: 200 体 null → 专属空检索异常
    public async Task Search_NullBody_Throws()
    {
        var stub = new StubHandler((_, _) => Json(HttpStatusCode.OK, "null"));
        var ex = await Assert.ThrowsAsync<AiGatewayException>(
            () => NewClient(stub).SearchPrepSourcesAsync("web", "Acme", null, null, default));
        Assert.Equal("AiGateway 返回了空的检索结果。", ex.Message);
    }

    // ---------------- 注册口径(两服务原 AddHttpClient 的统一) ----------------

    private static (Uri Base, TimeSpan Timeout, Type Impl) ResolveClient(string? configured)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddAiGatewayClient(configured);
        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IAiGatewayClient>();
        // typed client 的 HttpClient 是主构造参数捕获字段,反射取之核 BaseAddress/Timeout
        var http = typeof(AiGatewayClient)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(f => f.FieldType == typeof(HttpClient))
            .GetValue(client) as HttpClient;
        Assert.NotNull(http);
        return (http!.BaseAddress!, http.Timeout, client.GetType());
    }

    [Fact] // boundary: 配置为 null → 回落 5268,超时 3 分钟,实现为单一 adapter
    public void Registration_NullConfig_FallsBack()
    {
        var (b, timeout, impl) = ResolveClient(null);
        Assert.Equal(new Uri("http://127.0.0.1:5268/"), b);
        Assert.Equal(TimeSpan.FromMinutes(3), timeout);
        Assert.Equal(typeof(AiGatewayClient), impl);
    }

    [Fact] // boundary: 配置为空白 → 同回落
    public void Registration_BlankConfig_FallsBack()
    {
        var (b, _, _) = ResolveClient("  ");
        Assert.Equal(new Uri("http://127.0.0.1:5268/"), b);
    }

    [Fact] // 配置无尾斜杠 → 补齐(原两处 Program 口径)
    public void Registration_ConfigWithoutSlash_Normalized()
    {
        var (b, _, _) = ResolveClient("http://aigateway:8080");
        Assert.Equal(new Uri("http://aigateway:8080/"), b);
    }
}
