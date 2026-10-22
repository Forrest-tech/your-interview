using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Services.Analytics.Application;
using YourInterview.Services.Analytics.Infrastructure.Services;
using Xunit;

namespace YourInterview.Services.Analytics.Tests;

/// <summary>
/// 票34:HttpKnowledgeTruthSource 协议层测试(沙盒 other_tcp 策略拦截服务间直连 HTTP,
/// 活体层级止于「Analytics 发起请求被网关拦 + 失败语义正确」——见 evidence-34/STATUS;
/// 本类以 stub handler 替身套接字,覆盖解析/分页/令牌转发/失败回 null 的全部逻辑)。
/// boundary 类别:EmptyAuth(无令牌)、IncompletePages(分页未拉满)、HttpError(非 200)、
/// MalformedJson(损坏响应)。
/// </summary>
public sealed class Ticket34HttpKnowledgeTruthSourceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(respond(request, ++_calls));
        }
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static HttpKnowledgeTruthSource NewSource(StubHandler handler,
        string? authorization = "Bearer test-token")
    {
        var accessor = new HttpContextAccessor();
        if (authorization is not null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = authorization;
            accessor.HttpContext = ctx;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:Knowledge"] = "http://knowledge.test:5265"
            })
            .Build();

        return new HttpKnowledgeTruthSource(
            new StubFactory(new HttpClient(handler)),
            accessor, config, NullLogger<HttpKnowledgeTruthSource>.Instance);
    }

    private static HttpResponseMessage Page(int total, IEnumerable<(Guid Id, string Topic, string Mastery)> items)
    {
        var arr = string.Join(",", items.Select(i =>
            $"{{\"id\":\"{i.Id}\",\"topic\":\"{i.Topic}\",\"mastery\":\"{i.Mastery}\"}}"));
        var json = $"{{\"items\":[{arr}],\"total\":{total},\"page\":1,\"pageSize\":200}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task GetItems_TwoPages_CollectsAll_AndForwardsAuthorization()
    {
        var page1 = Enumerable.Range(0, 200)
            .Select(_ => (Guid.NewGuid(), "C#", "Mastered")).ToList();
        var last = (Guid.NewGuid(), "SQL", "NeedsReview");
        var handler = new StubHandler((req, call) => call == 1
            ? Page(201, page1)
            : Page(201, [last]));

        var items = await NewSource(handler).GetItemsAsync(default);

        items.Should().NotBeNull();
        items!.Should().HaveCount(201);
        items.Should().Contain(i => i.Id == last.Item1 && i.Topic == "SQL" && i.Mastery == "NeedsReview");
        handler.Authorizations.Should().HaveCount(2)
            .And.AllSatisfy(a => a.Should().Be("Bearer test-token"));
    }

    [Fact]
    public async Task GetItems_NoAuthorization_ReturnsNullWithoutCalling_BoundaryEmptyAuth()
    {
        var handler = new StubHandler((_, _) => Page(0, []));
        var items = await NewSource(handler, authorization: null).GetItemsAsync(default);
        items.Should().BeNull();
        handler.Authorizations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetItems_HttpError_ReturnsNull_BoundaryHttpError()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        (await NewSource(handler).GetItemsAsync(default)).Should().BeNull();
    }

    [Fact]
    public async Task GetItems_PaginationIncomplete_ReturnsNull_BoundaryIncompletePages()
    {
        // total 声称 500,第二页却空 —— 半截真源不许用于对账/重建
        var page1 = Enumerable.Range(0, 200)
            .Select(_ => (Guid.NewGuid(), "C#", "New")).ToList();
        var handler = new StubHandler((_, call) => call == 1 ? Page(500, page1) : Page(500, []));
        (await NewSource(handler).GetItemsAsync(default)).Should().BeNull();
    }

    [Fact]
    public async Task GetItems_MalformedJson_ReturnsNull_BoundaryMalformedJson()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json")
        });
        (await NewSource(handler).GetItemsAsync(default)).Should().BeNull();
    }

    [Fact]
    public async Task GetItems_EmptyTruth_ReturnsEmptyList_NotNull()
    {
        var handler = new StubHandler((_, _) => Page(0, []));
        var items = await NewSource(handler).GetItemsAsync(default);
        items.Should().NotBeNull().And.BeEmpty("真源为空是合法状态,与不可达(null)必须可区分");
    }
}
