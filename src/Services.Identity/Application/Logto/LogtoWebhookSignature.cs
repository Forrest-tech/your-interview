using System.Security.Cryptography;
using System.Text;

namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// 票241:Logto webhook 签名验签(官方口径:header logto-signature-sha-256,
/// HMAC-SHA256 hex over 原始 body)。恒时比较,防时序侧信道。
/// 票252(B8-A):Logto 每 hook 一把 key,故验签逐 key 试(VerifyAny);每 key
/// 仍是恒时比较,首个通过即放行(提前退出只泄露“第几个 key 命中”,事件类型
/// 本就是 payload 明文,不构成新信息)。不按事件/hook 选 key——选 key 需要
/// 可信的 hook 标识,而 1.45.0 投递中是否存在该字段未经证实,故不依赖它。
/// </summary>
public static class LogtoWebhookSignature
{
    public const string HeaderName = "logto-signature-sha-256";

    public static bool Verify(string signingKey, byte[] rawBody, string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(signingKey) || string.IsNullOrWhiteSpace(headerValue)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var hex = Convert.ToHexString(hmac.ComputeHash(rawBody)).ToLowerInvariant();
        var expected = headerValue.Trim().ToLowerInvariant();
        return hex.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hex), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>票252(B8-A):任一 key 通过即通过;空列表/全空白一律失败(不回落)。</summary>
    public static bool VerifyAny(IEnumerable<string> signingKeys, byte[] rawBody, string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        foreach (var key in signingKeys)
        {
            if (!string.IsNullOrWhiteSpace(key) && Verify(key, rawBody, headerValue)) return true;
        }
        return false;
    }
}
