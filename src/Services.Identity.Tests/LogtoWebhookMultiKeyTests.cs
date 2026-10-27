using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Xunit;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票252(B8-A):Logto 每 hook 一把签名 key,验签须逐 key 试。
/// 覆盖:任一 key 通过、未知 key 失败、空列表失败、空白项忽略、
/// 列表优先于单 key、列表全空时单 key 回落、WebhookConfigured 口径。
/// </summary>
public sealed class LogtoWebhookMultiKeyTests
{
    private static string Sign(string key, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    [Fact]
    public void VerifyAny_SignedBySecondKey_Passes()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Deleted","data":{"id":"u1"}}""");
        LogtoWebhookSignature.VerifyAny(["key-hook-a", "key-hook-b"], body, Sign("key-hook-b", body))
            .Should().BeTrue();
    }

    [Fact]
    public void VerifyAny_SignedByFirstKey_Passes()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Created","data":{"id":"u1"}}""");
        LogtoWebhookSignature.VerifyAny(["key-hook-a", "key-hook-b"], body, Sign("key-hook-a", body))
            .Should().BeTrue();
    }

    [Fact]
    public void VerifyAny_SignedByUnknownKey_Fails()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Deleted","data":{"id":"u1"}}""");
        LogtoWebhookSignature.VerifyAny(["key-hook-a", "key-hook-b"], body, Sign("key-unknown", body))
            .Should().BeFalse();
    }

    [Fact]
    public void VerifyAny_EmptyList_Fails()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Deleted","data":{"id":"u1"}}""");
        LogtoWebhookSignature.VerifyAny([], body, Sign("key-hook-a", body)).Should().BeFalse();
    }

    [Fact]
    public void VerifyAny_BlankEntriesIgnored()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Deleted","data":{"id":"u1"}}""");
        LogtoWebhookSignature.VerifyAny(["", "  ", "key-hook-b"], body, Sign("key-hook-b", body))
            .Should().BeTrue();
        LogtoWebhookSignature.VerifyAny(["", "  "], body, Sign("key-hook-b", body))
            .Should().BeFalse();
    }

    [Fact]
    public void VerifyAny_MissingHeader_Fails()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Deleted","data":{"id":"u1"}}""");
        LogtoWebhookSignature.VerifyAny(["key-hook-a"], body, null).Should().BeFalse();
    }

    [Fact]
    public void EffectiveKeys_ListWinsOverSingleKey()
    {
        var opts = new LogtoManagementOptions
        {
            WebhookSigningKey = "legacy-single-key",
            WebhookSigningKeys = ["key-hook-a", "  key-hook-b  "],
        };
        // 列表非空则全量取代单 key:单 key 不在生效列表里,须把全部 hook 的 key 列入。
        opts.EffectiveWebhookSigningKeys().Should().Equal("key-hook-a", "key-hook-b");
    }

    [Fact]
    public void EffectiveKeys_EmptyListFallsBackToSingleKey()
    {
        var opts = new LogtoManagementOptions { WebhookSigningKey = "legacy-single-key" };
        opts.EffectiveWebhookSigningKeys().Should().Equal("legacy-single-key");
    }

    [Fact]
    public void EffectiveKeys_AllBlankListFallsBackToSingleKey()
    {
        var opts = new LogtoManagementOptions
        {
            WebhookSigningKey = "legacy-single-key",
            WebhookSigningKeys = ["", "  "],
        };
        opts.EffectiveWebhookSigningKeys().Should().Equal("legacy-single-key");
    }

    [Fact]
    public void WebhookConfigured_ListOnlyCountsAsConfigured()
    {
        var opts = new LogtoManagementOptions
        {
            Endpoint = "http://logto:3001",
            M2mClientId = "test-m2m",
            M2mClientSecret = "test-secret",
            WebhookSigningKeys = ["key-hook-a"],
        };
        opts.WebhookConfigured.Should().BeTrue();

        var none = new LogtoManagementOptions
        {
            Endpoint = "http://logto:3001",
            M2mClientId = "test-m2m",
            M2mClientSecret = "test-secret",
        };
        none.WebhookConfigured.Should().BeFalse();
    }
}
