using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.BuildingBlocks.Ai;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.BuildingBlocks.Tests;

// ============================================================================
//  票40 ARC-15:LLM 客户端实现自 SharedContracts/Ai 迁入 BuildingBlocks/Ai
//  后的行为特征测试。实现此前在契约项目内零专属测试(仅 AiGateway 活体覆盖),
//  本套件按改前方法体逐字语义锁定:三协议的请求形态/鉴权头/响应解析/
//  配置错误分类,以及工厂派发与 LlmJson 纯函数 boundary。
//  boundary 类别:空/null(无 key/无 model/无 endpoint、空响应内容、
//  LlmJson 空输入与无 JSON 输入)、特殊字符/Unicode(围栏 JSON、中文文本)。
//  日期/文件类不适用:本组类型无日期/文件入参。
// ============================================================================
public sealed class LlmClientTests
{
    private sealed class StubHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, LastBody);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LlmConnection Conn(string protocol, string key = "sk-test", string? baseUrl = "https://llm.example/v1",
        string model = "test-model", string? endpoint = null, string? apiVersion = null) =>
        new(protocol, key, baseUrl, model, endpoint, apiVersion);

    // ---------- OpenAiCompatibleClient ----------

    [Fact] // 成功解析 + Bearer 鉴权 + 请求体口径(model/messages/temperature)
    public async Task OpenAi_Success_ParsesAndSendsBearer()
    {
        var handler = new StubHandler((_, _) => Json(
            """{"model":"test-model","choices":[{"message":{"content":"hello"}}],"usage":{"prompt_tokens":11,"completion_tokens":7}}"""));
        var client = new OpenAiCompatibleClient(new HttpClient(handler),
            Conn(LlmProviders.OpenAiCompatible), NullLogger.Instance);

        var resp = await client.CompleteAsync(new LlmRequest("Say hi", "sys", 0.5, 64), default);

        Assert.Equal("hello", resp.Text);
        Assert.Equal("test-model", resp.Model);
        Assert.Equal(11, resp.PromptTokens);
        Assert.Equal(7, resp.CompletionTokens);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("https://llm.example/v1/chat/completions", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"model\":\"test-model\"", handler.LastBody);
        Assert.Contains("\"temperature\":0.5", handler.LastBody);
        Assert.Contains("\"max_tokens\":64", handler.LastBody);
    }

    [Fact] // boundary:空/null 配置 —— 无 key / 无 model 均 IsConfigurationError
    public async Task OpenAi_MissingKeyOrModel_IsConfigurationError()
    {
        var noKey = new OpenAiCompatibleClient(new HttpClient(new StubHandler((_, _) => Json("{}"))),
            Conn(LlmProviders.OpenAiCompatible, key: " "), NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<LlmCallException>(
            () => noKey.CompleteAsync(new LlmRequest("x"), default));
        Assert.True(ex.IsConfigurationError);

        var noModel = new OpenAiCompatibleClient(new HttpClient(new StubHandler((_, _) => Json("{}"))),
            Conn(LlmProviders.OpenAiCompatible, model: ""), NullLogger.Instance);
        ex = await Assert.ThrowsAsync<LlmCallException>(
            () => noModel.CompleteAsync(new LlmRequest("x"), default));
        Assert.True(ex.IsConfigurationError);
    }

    [Fact] // 非 2xx:状态码与上游片段原样带出,非配置错误
    public async Task OpenAi_UpstreamError_CarriesStatusAndSnippet()
    {
        var handler = new StubHandler((_, _) => Json("quota exhausted", HttpStatusCode.TooManyRequests));
        var client = new OpenAiCompatibleClient(new HttpClient(handler),
            Conn(LlmProviders.OpenAiCompatible), NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<LlmCallException>(
            () => client.CompleteAsync(new LlmRequest("x"), default));
        Assert.False(ex.IsConfigurationError);
        Assert.Contains("429", ex.Message);
        Assert.Contains("quota exhausted", ex.Message);
    }

    [Fact] // boundary:200 但内容为空 —— 抛空内容异常(不假装成功)
    public async Task OpenAi_EmptyContent_Throws()
    {
        var handler = new StubHandler((_, _) => Json(
            """{"model":"m","choices":[{"message":{"content":""}}]}"""));
        var client = new OpenAiCompatibleClient(new HttpClient(handler),
            Conn(LlmProviders.OpenAiCompatible), NullLogger.Instance);
        await Assert.ThrowsAsync<LlmCallException>(
            () => client.CompleteAsync(new LlmRequest("x"), default));
    }

    // ---------- AnthropicClient ----------

    [Fact] // 成功解析(content[] 文本块拼接)+ x-api-key / anthropic-version 头 + system 顶层字段
    public async Task Anthropic_Success_ParsesBlocksAndHeaders()
    {
        var handler = new StubHandler((_, _) => Json(
            """{"model":"claude-x","content":[{"type":"text","text":"Hello "},{"type":"text","text":"world"}],"usage":{"input_tokens":5,"output_tokens":3}}"""));
        var client = new AnthropicClient(new HttpClient(handler), Conn(LlmProviders.Anthropic));

        var resp = await client.CompleteAsync(new LlmRequest("hi", "be nice"), default);

        Assert.Equal("Hello world", resp.Text);
        Assert.Equal(5, resp.PromptTokens);
        Assert.Equal(3, resp.CompletionTokens);
        Assert.Equal("sk-test", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("https://llm.example/v1/messages", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"system\":\"be nice\"", handler.LastBody);
        Assert.Contains("\"max_tokens\":4096", handler.LastBody); // 未给 MaxTokens 时的改前默认
    }

    [Fact] // boundary:无 key —— 配置错误
    public async Task Anthropic_MissingKey_IsConfigurationError()
    {
        var client = new AnthropicClient(new HttpClient(new StubHandler((_, _) => Json("{}"))),
            Conn(LlmProviders.Anthropic, key: ""));
        var ex = await Assert.ThrowsAsync<LlmCallException>(
            () => client.CompleteAsync(new LlmRequest("x"), default));
        Assert.True(ex.IsConfigurationError);
    }

    // ---------- AzureOpenAiClient ----------

    [Fact] // 成功解析 + api-key 头 + deployment 路径与 api-version 口径
    public async Task Azure_Success_ParsesAndBuildsDeploymentUrl()
    {
        var handler = new StubHandler((_, _) => Json(
            """{"choices":[{"message":{"content":"azure-ok"}}],"usage":{"prompt_tokens":2,"completion_tokens":1}}"""));
        var client = new AzureOpenAiClient(new HttpClient(handler),
            Conn(LlmProviders.AzureOpenAi, endpoint: "https://res.openai.azure.com/", model: "dep-1"));

        var resp = await client.CompleteAsync(new LlmRequest("hi"), default);

        Assert.Equal("azure-ok", resp.Text);
        Assert.Equal("dep-1", resp.Model); // Azure 口径:回连接里的部署名,不取响应 model
        Assert.Equal("sk-test", handler.LastRequest!.Headers.GetValues("api-key").Single());
        Assert.Equal(
            "https://res.openai.azure.com/openai/deployments/dep-1/chat/completions?api-version=2024-10-21",
            handler.LastRequest.RequestUri!.ToString());
    }

    [Fact] // boundary:无 endpoint —— 配置错误
    public async Task Azure_MissingEndpoint_IsConfigurationError()
    {
        var client = new AzureOpenAiClient(new HttpClient(new StubHandler((_, _) => Json("{}"))),
            Conn(LlmProviders.AzureOpenAi, endpoint: null));
        var ex = await Assert.ThrowsAsync<LlmCallException>(
            () => client.CompleteAsync(new LlmRequest("x"), default));
        Assert.True(ex.IsConfigurationError);
    }

    // ---------- LlmClientFactory(实现迁 BuildingBlocks,接口仍 SharedContracts 契约) ----------

    [Theory] // 协议派发:三协议 + 未知协议回落 OpenAI 兼容(改前 switch 口径)
    [InlineData(LlmProviders.AzureOpenAi, nameof(AzureOpenAiClient))]
    [InlineData(LlmProviders.Anthropic, nameof(AnthropicClient))]
    [InlineData(LlmProviders.OpenAiCompatible, nameof(OpenAiCompatibleClient))]
    [InlineData("something-else", nameof(OpenAiCompatibleClient))]
    public void Factory_DispatchesByProtocol(string protocol, string expectedType)
    {
        var factory = new LlmClientFactory(
            new StubHttpClientFactory(new HttpClient(new StubHandler((_, _) => Json("{}")))),
            NullLoggerFactory.Instance);
        var client = factory.Create(Conn(protocol, endpoint: "https://res.openai.azure.com"));
        Assert.Equal(expectedType, client.GetType().Name);
    }

    // ---------- LlmJson(纯函数,随实现迁入;改前零测试,本次补特征锁) ----------

    [Theory] // boundary:空/null/纯文本无 JSON → null(不假装成功)
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    public void LlmJson_ExtractJson_NoJson_ReturnsNull(string? raw) =>
        Assert.Null(LlmJson.ExtractJson(raw));

    [Fact] // 围栏剥壳 + 前后解释文字剥离 + Unicode 保留
    public void LlmJson_ExtractJson_FencedAndProse()
    {
        var fenced = "好的,结果如下:\n```json\n{\"a\":\"中文 🎉\"}\n```\n以上。";
        Assert.Equal("{\"a\":\"中文 🎉\"}", LlmJson.ExtractJson(fenced));

        var prose = "prefix [1, 2, 3] suffix";
        Assert.Equal("[1, 2, 3]", LlmJson.ExtractJson(prose));
    }

    [Fact] // TryParse:可解析 → 对象;损坏 JSON → null
    public void LlmJson_TryParse_RoundTripsAndFailsSoft()
    {
        var ok = LlmJson.TryParse<Dictionary<string, int>>("```json\n{\"n\":42}\n```");
        Assert.NotNull(ok);
        Assert.Equal(42, ok!["n"]);
        Assert.Null(LlmJson.TryParse<Dictionary<string, int>>("{broken"));
    }
}
