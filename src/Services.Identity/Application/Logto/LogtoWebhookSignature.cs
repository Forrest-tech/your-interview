using System.Security.Cryptography;
using System.Text;

namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// 票241:Logto webhook 签名验签(官方口径:header logto-signature-sha-256,
/// HMAC-SHA256 hex over 原始 body)。恒时比较,防时序侧信道。
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
}
