using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using YourInterview.Services.Analytics.Application;

namespace YourInterview.Services.Analytics.Infrastructure.Services;

/// <summary>
/// 经 Knowledge 既有 API 拉当前用户条目全集(票34 对账/重建的真源)。
///
/// 归属口径:Knowledge 端点一律以调用方 JWT 的 Me 收口,没有「代查他人」通道 ——
/// 故这里转发调用方自己的 Authorization 头,查到的天然只是自己的条目,
/// 与 Analytics 投影的 UserId 同人,不引入任何跨用户读取面。
///
/// 失败语义:非 200 / 网络异常 / 分页未拉满 total / 超过页数上限 → 一律返回 null
/// (真源不完整),对账/重建据此中止且不写库。配置 Services:Knowledge 指向
/// Knowledge 服务地址(默认与网关集群一致的 127.0.0.1:5265)。
/// </summary>
public sealed class HttpKnowledgeTruthSource(
    IHttpClientFactory httpFactory,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    ILogger<HttpKnowledgeTruthSource> logger) : IKnowledgeTruthSource
{
    private const int PageSize = 200;
    private const int MaxPages = 100; // 上限 2 万条:超出视为不完整,中止而非半截对齐

    public async Task<IReadOnlyList<KnowledgeItemTruth>?> GetItemsAsync(CancellationToken ct)
    {
        var auth = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth))
        {
            logger.LogWarning("对账/重建缺少调用方 Authorization 头,无法向 Knowledge 取真源");
            return null;
        }

        var baseUrl = (configuration["Services:Knowledge"] ?? "http://127.0.0.1:5265")
            .TrimEnd('/');
        var http = httpFactory.CreateClient(nameof(HttpKnowledgeTruthSource));
        var items = new List<KnowledgeItemTruth>();

        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/api/knowledge?page={page}&pageSize={PageSize}");
                req.Headers.Authorization = AuthenticationHeaderValue.Parse(auth);

                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogWarning("Knowledge 真源请求失败 HTTP {Status}", (int)resp.StatusCode);
                    return null;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;
                var total = root.GetProperty("total").GetInt32();

                foreach (var el in root.GetProperty("items").EnumerateArray())
                {
                    items.Add(new KnowledgeItemTruth(
                        el.GetProperty("id").GetGuid(),
                        el.GetProperty("topic").GetString() ?? string.Empty,
                        el.GetProperty("mastery").GetString() ?? string.Empty));
                }

                if (items.Count >= total) return items;
                if (root.GetProperty("items").GetArrayLength() == 0) break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Knowledge 真源读取异常,对账/重建中止");
            return null;
        }

        logger.LogWarning("Knowledge 真源分页未拉满(已拉 {Count} 条),对账/重建中止", items.Count);
        return null;
    }
}
