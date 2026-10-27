using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票251:Logto Management 客户端对 1.45.0 实测行为的回归测试(不连真实 Logto,
/// 用 stub HttpMessageHandler 回放 1.45.0 形状的响应)。
/// B1 token 的 resource 必须是租户 Management API indicator,不是 {Endpoint}/api;
/// B2 createdAt/updatedAt 是数字毫秒时间戳,必须解析成功;
/// B3 custom-data 体必须是 {"customData": {...}}。
/// </summary>
public class LogtoManagementClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public string? TokenFormBody;
        public string? CustomDataBody;
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Responder is not null) return Task.FromResult(Responder(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    // 1.45.0 形状的用户 JSON:日期是数字毫秒时间戳,且没有任何邮箱验证字段。
    private const string UserJson = """
        {"id":"u1","isSuspended":false,
         "customData":{"approval":"pending","legacyUserId":"00000000-0000-0000-0000-000000000001"},
         "primaryEmail":"u@example.com",
         "createdAt":1793116025369,"updatedAt":1793116025369}
        """;

    private static LogtoManagementClient Client(StubHandler handler) =>
        new(new StubFactory(handler),
            Options.Create(new LogtoManagementOptions
            {
                Endpoint = "http://localhost:3001",
                M2mClientId = "m2m",
                M2mClientSecret = "secret"
            }));

    [Fact]
    public async Task TokenRequest_UsesTenantManagementApiIndicator_NotEndpointDerived()
    {
        var handler = new StubHandler();
        handler.Responder = req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/oidc/token"))
            {
                handler.TokenFormBody = req.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"tok","expires_in":3600}""",
                        Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(UserJson, Encoding.UTF8, "application/json")
            };
        };

        var user = await Client(handler).GetUserAsync("u1", default);

        user.Should().NotBeNull();
        handler.TokenFormBody.Should().Contain("resource=https%3A%2F%2Fdefault.logto.app%2Fapi");
        handler.TokenFormBody.Should().NotContain("localhost");
    }

    [Fact]
    public async Task GetUser_ParsesNumericMillisecondDates_AndIgnoresMissingVerificationField()
    {
        var handler = new StubHandler();
        handler.Responder = req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/oidc/token"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"tok","expires_in":3600}""",
                        Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(UserJson, Encoding.UTF8, "application/json")
            };
        };

        var user = await Client(handler).GetUserAsync("u1", default);

        user.Should().NotBeNull();
        user!.Id.Should().Be("u1");
        user.PrimaryEmail.Should().Be("u@example.com");
        user.CustomData["approval"]?.GetValue<string>().Should().Be("pending");
        user.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1793116025369));
        user.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1793116025369));
    }

    [Fact]
    public async Task SetCustomData_WrapsBodyInCustomDataObject()
    {
        var handler = new StubHandler();
        handler.Responder = req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/oidc/token"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"tok","expires_in":3600}""",
                        Encoding.UTF8, "application/json")
                };
            if (req.Method == HttpMethod.Patch && req.RequestUri!.AbsolutePath.EndsWith("/custom-data"))
            {
                handler.CustomDataBody = req.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        await Client(handler).SetCustomDataAsync("u1",
            new JsonObject { ["approval"] = "approved" }, default);

        handler.CustomDataBody.Should().NotBeNull();
        var doc = JsonDocument.Parse(handler.CustomDataBody!);
        doc.RootElement.GetProperty("customData").GetProperty("approval").GetString()
            .Should().Be("approved");
        doc.RootElement.TryGetProperty("approval", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ParseLogtoDate_AcceptsIsoString_AndNumericString()
    {
        var iso = JsonDocument.Parse("""{"createdAt":"2026-10-27T10:00:00Z"}""").RootElement;
        LogtoManagementClient.ParseLogtoDate(iso, "createdAt")
            .Should().Be(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        var numeric = JsonDocument.Parse("""{"createdAt":"1793116025369"}""").RootElement;
        LogtoManagementClient.ParseLogtoDate(numeric, "createdAt")
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1793116025369));
        var missing = JsonDocument.Parse("""{}""").RootElement;
        LogtoManagementClient.ParseLogtoDate(missing, "createdAt")
            .Should().Be(DateTimeOffset.MinValue);
    }
}
