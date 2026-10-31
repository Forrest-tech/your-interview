using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票 240：Gmail 实现（方案 §1，标准 Gmail API 语义）。
/// scope：gmail.readonly + openid email profile（只读，不发信、不删改、不标已读）。
/// 增量：history.list(startHistoryId)；historyId 过期返回 404 → CursorExpired，由调用方回落 30 天窗口重拉。
/// 原文：messages.get(format=raw) 拿真正的原文 MIME；结构化字段另取 format=full（不自写 MIME 解析）。
/// 票 294：流式拉取（IIncrementalMailboxProvider）+ 节流与配额退避。
/// 配额表（新 cohort 口径）：6000 单位/分钟/用户；list 5、full/raw 60、history 2、getProfile 1。
/// 计量调用（full/raw）之间默认 700ms 间隔（≈5143 单位/分钟，余量容纳列单与命中封原文）；
/// 403-quota / 429 在 run 内指数退避 + 抖动重试至多 3 次，耗尽 → MailboxRateLimitedException。
/// </summary>
public sealed class GmailMailboxProvider : IMailboxProvider, IIncrementalMailboxProvider
{
    private readonly HttpClient _http;
    private readonly TimeSpan _pacingDelay;
    private readonly TimeSpan _retryBaseDelay;
    private DateTimeOffset _lastMeteredCallUtc = DateTimeOffset.MinValue;

    public GmailMailboxProvider(HttpClient http, TimeSpan? pacingDelay = null, TimeSpan? retryBaseDelay = null)
    {
        _http = http;
        _pacingDelay = pacingDelay ?? TimeSpan.FromMilliseconds(700);
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
    }

    public MailboxProvider Kind => MailboxProvider.Gmail;

    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string ApiBase = "https://gmail.googleapis.com/gmail/v1/users/me";

    public string GetAuthorizationUrl(string clientId, string redirectUri, string state)
    {
        var scope = Uri.EscapeDataString("https://www.googleapis.com/auth/gmail.readonly openid email profile");
        return $"{AuthUrl}?client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               "&response_type=code" +
               $"&scope={scope}&access_type=offline&prompt=consent&state={Uri.EscapeDataString(state)}";
    }

    public async Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code,
        string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        };
        return await PostTokenAsync(form, ct);
    }

    public async Task<MailboxTokens> RefreshAccessTokenAsync(string clientId, string clientSecret,
        string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        return await PostTokenAsync(form, ct);
    }

    public async Task RevokeAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, RevokeUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken })
            };
            using var resp = await _http.SendAsync(req, ct);
            // 撤销是尽力而为：失败不抛（断开语义 = 本地删 token 即失效）。
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 忽略 */ }
    }

    // ============================ 票 294：流式拉取 + 节流与配额退避 ============================

    private static HttpRequestMessage GetRequest(string url, string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    /// <summary>
    /// 计量调用节流：60 单位级调用（full/raw）之间至少间隔 _pacingDelay。
    /// provider 实例由单例 factory 持有，节流状态跨用户共享是保守方向（安全）。
    /// </summary>
    private async Task PaceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var wait = _pacingDelay - (now - _lastMeteredCallUtc);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        _lastMeteredCallUtc = DateTimeOffset.UtcNow;
    }

    private static bool IsQuotaErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("errors", out var errors))
            {
                foreach (var e in errors.EnumerateArray())
                {
                    if (e.TryGetProperty("reason", out var reason)
                        && reason.ValueKind == JsonValueKind.String
                        && reason.GetString() is "quotaExceeded" or "rateLimitExceeded")
                        return true;
                }
            }
        }
        catch { /* 非 JSON 体 → 回落子串 */ }
        return body.Contains("quotaExceeded", StringComparison.Ordinal)
            || body.Contains("rateLimitExceeded", StringComparison.Ordinal);
    }

    /// <summary>
    /// 票 294 统一发送路径（流式方法与逐封取数共用）。
    /// 成功或 404 直通（调用方按“不在”处理）；401 → MailboxAuthException；
    /// 403-quota / 429 → 指数退避 + 抖动重试至多 3 次，耗尽 → MailboxRateLimitedException；
    /// 其余 → MailboxProviderException（与旧 ThrowIfErrorAsync 同文）。
    /// metered 调用先节流；重试只走退避，不重复节流。
    /// </summary>
    private async Task<HttpResponseMessage> SendGmailAsync(
        Func<HttpRequestMessage> requestFactory, bool metered, CancellationToken ct)
    {
        if (metered) await PaceAsync(ct);
        var backoff = _retryBaseDelay;
        for (var attempt = 0; ; attempt++)
        {
            using var req = requestFactory();
            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound) return resp;
            var status = resp.StatusCode;
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            if (status == HttpStatusCode.Unauthorized)
            {
                resp.Dispose();
                throw new MailboxAuthException("Gmail 授权失效（401），请重新连接邮箱。");
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            var retryable = status == (HttpStatusCode)429
                || (status == HttpStatusCode.Forbidden && IsQuotaErrorBody(body));
            if (!retryable)
                throw new MailboxProviderException($"Gmail API 错误 {(int)status}：{Truncate(body)}");
            if (attempt >= 3)
                throw new MailboxRateLimitedException("Gmail 配额已耗尽，请稍后重试。", retryAfter);
            var wait = backoff > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : backoff;
            wait += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            await Task.Delay(wait, ct);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
    }

    /// <summary>票 294 流式列单页（50 封/页）。</summary>
    public async Task<(IReadOnlyList<string> Ids, string? NextPageToken)> ListIdsPageAsync(
        string accessToken, string? pageToken, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        var after = sinceUtc.UtcDateTime.ToString("yyyy/MM/dd");
        var url = $"{ApiBase}/messages?q={Uri.EscapeDataString($"after:{after}")}&maxResults=50"
            + (pageToken is null ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");
        using var resp = await SendGmailAsync(() => GetRequest(url, accessToken), metered: false, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var ids = new List<string>();
        if (root.TryGetProperty("messages", out var msgs))
        {
            foreach (var m in msgs.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
            }
        }
        var next = root.TryGetProperty("nextPageToken", out var nt) ? nt.GetString() : null;
        return (ids, next);
    }

    /// <summary>票 294 流式增量页。404 = 锚点过期 → MailboxCursorExpiredException。</summary>
    public async Task<(IReadOnlyList<string> Ids, string? NextPageToken, string? NewHistoryId)> ListHistoryPageAsync(
        string accessToken, string startHistoryId, string? pageToken, CancellationToken ct)
    {
        var url = $"{ApiBase}/history?startHistoryId={Uri.EscapeDataString(startHistoryId)}&historyTypes=messageAdded"
            + (pageToken is null ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");
        using var resp = await SendGmailAsync(() => GetRequest(url, accessToken), metered: false, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new MailboxCursorExpiredException("Gmail 增量锚点已过期（historyId 超出保留期），须回落回填重拉。");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var ids = new List<string>();
        if (root.TryGetProperty("history", out var history))
        {
            foreach (var h in history.EnumerateArray())
            {
                if (!h.TryGetProperty("messagesAdded", out var added)) continue;
                foreach (var a in added.EnumerateArray())
                {
                    if (a.TryGetProperty("message", out var m)
                        && m.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String)
                        ids.Add(id.GetString()!);
                }
            }
        }
        var next = root.TryGetProperty("nextPageToken", out var nt) ? nt.GetString() : null;
        var hid = root.TryGetProperty("historyId", out var h2) ? h2.GetString() : null;
        return (ids, next, hid);
    }

    /// <summary>票 294：取当前 history 锚点（users.getProfile，1 单位）。</summary>
    public async Task<string?> GetProfileHistoryIdAsync(string accessToken, CancellationToken ct)
    {
        using var resp = await SendGmailAsync(() => GetRequest($"{ApiBase}/profile", accessToken),
            metered: false, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("historyId", out var h) ? h.GetString() : null;
    }

    public async Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor,
        DateTimeOffset sinceUtc, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cursor))
        {
            // 增量路径：history.list。404 = historyId 过期 → 回落。
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{ApiBase}/history?startHistoryId={Uri.EscapeDataString(cursor)}&historyTypes=messageAdded");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new([], null, CursorExpired: true);
            await ThrowIfErrorAsync(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var ids = new List<string>();
            if (root.TryGetProperty("history", out var history))
            {
                foreach (var h in history.EnumerateArray())
                {
                    if (!h.TryGetProperty("messagesAdded", out var added)) continue;
                    foreach (var a in added.EnumerateArray())
                    {
                        if (a.TryGetProperty("message", out var m)
                            && m.TryGetProperty("id", out var id)
                            && id.ValueKind == JsonValueKind.String)
                            ids.Add(id.GetString()!);
                    }
                }
            }
            var nextHistoryId = root.TryGetProperty("historyId", out var hid) ? hid.GetString() : null;
            var messages = await FetchMessagesAsync(accessToken, ids.Distinct().ToList(), ct);
            return new(messages, nextHistoryId, CursorExpired: false);
        }

        // 首刷 / 回落路径：日期窗口（Gmail q 的 after: 按账号日历日，取 UTC 日期近似）。
        var after = sinceUtc.UtcDateTime.ToString("yyyy/MM/dd");
        var pageIds = new List<string>();
        string? pageToken = null;
        do
        {
            var url = $"{ApiBase}/messages?q={Uri.EscapeDataString($"after:{after}")}&maxResults=100"
                + (pageToken is null ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            await ThrowIfErrorAsync(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("messages", out var msgs))
            {
                foreach (var m in msgs.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        pageIds.Add(id.GetString()!);
                }
            }
            pageToken = root.TryGetProperty("nextPageToken", out var nt) ? nt.GetString() : null;
        } while (pageToken is not null);

        var result = await FetchMessagesAsync(accessToken, pageIds.Distinct().ToList(), ct);
        // 日期窗口路径无增量游标：调用方不推进 cursor（下次仍走窗口 + 去重键兜底）。
        return new(result, null, CursorExpired: false);
    }

    public async Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId,
        CancellationToken ct)
    {
        var full = await GetMessageFullAsync(accessToken, providerMessageId, ct);
        if (full is null) return null;
        var raw = await GetMessageRawAsync(accessToken, providerMessageId, ct);
        return full with { RawMimeBase64Url = raw };
    }

    public async Task<string?> GetAccountEmailAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
    }

    public async Task<byte[]?> GetAttachmentBytesAsync(string accessToken, string providerMessageId,
        string providerAttachmentId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/messages/{Uri.EscapeDataString(providerMessageId)}/attachments/{Uri.EscapeDataString(providerAttachmentId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
        return data is null ? null : DecodeBase64Url(data);
    }

    /// <summary>整线程拉取：threads.get(format=full)，每封另取 format=raw（原文）。</summary>
    public async Task<IReadOnlyList<MailboxMessage>> GetThreadAsync(string accessToken, string threadId,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/threads/{Uri.EscapeDataString(threadId)}?format=full");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return [];
        await ThrowIfErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var list = new List<MailboxMessage>();
        if (!root.TryGetProperty("messages", out var msgs)) return list;
        foreach (var m in msgs.EnumerateArray())
        {
            var parsed = ParseFullMessage(m);
            if (parsed is null) continue;
            var raw = await GetMessageRawAsync(accessToken, parsed.ProviderMessageId, ct);
            list.Add(parsed with { RawMimeBase64Url = raw });
        }
        return list;
    }

    private async Task<IReadOnlyList<MailboxMessage>> FetchMessagesAsync(string accessToken,
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        var list = new List<MailboxMessage>();
        foreach (var id in ids)
        {
            var m = await GetMessageAsync(accessToken, id, ct);
            if (m is not null) list.Add(m);
        }
        return list;
    }

    /// <summary>票 294 流式取全文（format=full；不含原文）。旧私有方法改公开，走统一发送路径。</summary>
    public async Task<MailboxMessage?> GetMessageFullAsync(string accessToken, string id, CancellationToken ct)
    {
        using var resp = await SendGmailAsync(
            () => GetRequest($"{ApiBase}/messages/{Uri.EscapeDataString(id)}?format=full", accessToken),
            metered: true, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParseFullMessage(doc.RootElement);
    }

    /// <summary>票 294 流式取原文（format=raw）。旧私有方法改公开，走统一发送路径。</summary>
    public async Task<string?> GetMessageRawAsync(string accessToken, string id, CancellationToken ct)
    {
        using var resp = await SendGmailAsync(
            () => GetRequest($"{ApiBase}/messages/{Uri.EscapeDataString(id)}?format=raw", accessToken),
            metered: true, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("raw", out var raw) ? raw.GetString() : null;
    }

    /// <summary>票 240：公开静态以便测试直接断言（227 同款先例；程序集无 InternalsVisibleTo）。</summary>
    public static MailboxMessage? ParseFullMessage(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (id is null) return null;
        var threadId = root.TryGetProperty("threadId", out var t) ? t.GetString() : null;
        var payload = root.TryGetProperty("payload", out var p) ? p : (JsonElement?)null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload is { } pl && pl.TryGetProperty("headers", out var hs))
        {
            foreach (var h in hs.EnumerateArray())
            {
                var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = h.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (name is not null && value is not null) headers[name] = value;
            }
        }
        var (fromEmail, fromName) = ParseAddress(headers.GetValueOrDefault("From"));
        var to = SplitAddresses(headers.GetValueOrDefault("To"));
        var cc = SplitAddresses(headers.GetValueOrDefault("Cc"));
        var subject = headers.GetValueOrDefault("Subject") ?? string.Empty;
        var dateHeader = headers.GetValueOrDefault("Date");
        var receivedAt = ParseDateHeader(dateHeader)
            ?? (root.TryGetProperty("internalDate", out var ms) && long.TryParse(ms.GetString(), out var epoch)
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.UtcNow);
        var bodyText = string.Empty;
        string? bodyHtml = null;
        var attachments = new List<MailboxAttachment>();
        if (payload is { } pl2) WalkParts(pl2, ref bodyText, ref bodyHtml, attachments);
        return new MailboxMessage(
            id, threadId, fromEmail, fromName, to, cc, subject, receivedAt,
            bodyText, bodyHtml, RawMimeBase64Url: null, attachments,
            headers.GetValueOrDefault("Message-ID"));
    }

    private static void WalkParts(JsonElement part, ref string bodyText, ref string? bodyHtml,
        List<MailboxAttachment> attachments)
    {
        var mime = part.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
        var fileName = part.TryGetProperty("filename", out var f) ? f.GetString() : null;
        if (!string.IsNullOrEmpty(fileName))
        {
            var body = part.TryGetProperty("body", out var b) ? b : (JsonElement?)null;
            var attId = body is { } bb && bb.TryGetProperty("attachmentId", out var a) ? a.GetString() : null;
            var size = body is { } bb2 && bb2.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            attachments.Add(new(fileName, mime ?? "application/octet-stream", size, attId, null));
            return;
        }
        if (mime == "text/plain" && bodyText.Length == 0)
        {
            var data = part.TryGetProperty("body", out var b) && b.TryGetProperty("data", out var d) ? d.GetString() : null;
            if (data is not null) bodyText = Encoding.UTF8.GetString(DecodeBase64Url(data));
        }
        else if (mime == "text/html" && bodyHtml is null)
        {
            var data = part.TryGetProperty("body", out var b) && b.TryGetProperty("data", out var d) ? d.GetString() : null;
            if (data is not null) bodyHtml = Encoding.UTF8.GetString(DecodeBase64Url(data));
        }
        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var sub in parts.EnumerateArray())
                WalkParts(sub, ref bodyText, ref bodyHtml, attachments);
        }
    }

    private static (string Email, string? Name) ParseAddress(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return (string.Empty, null);
        var lt = header.LastIndexOf('<');
        var gt = header.LastIndexOf('>');
        if (lt >= 0 && gt > lt) return (header[(lt + 1)..gt].Trim(), header[..lt].Trim().Trim('"'));
        return (header.Trim(), null);
    }

    private static IReadOnlyList<string> SplitAddresses(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return [];
        // 地址列表按逗号切分（显示名里的逗号极少见；取 <> 内地址，兜底整段）。
        return header.Split(',').Select(s =>
        {
            var lt = s.LastIndexOf('<');
            var gt = s.LastIndexOf('>');
            return lt >= 0 && gt > lt ? s[(lt + 1)..gt].Trim() : s.Trim();
        }).Where(s => s.Length > 0).ToList();
    }

    private static DateTimeOffset? ParseDateHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        return DateTimeOffset.TryParse(header, out var dto) ? dto : null;
    }

    private async Task<MailboxTokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var resp = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (access is null) throw new MailboxProviderException("Gmail token 响应缺 access_token。");
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var scope = root.TryGetProperty("scope", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        DateTimeOffset? expires = null;
        if (root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var secs))
            expires = DateTimeOffset.UtcNow.AddSeconds(secs);
        return new(access, refresh, scope, expires);
    }

    internal static async Task ThrowIfErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new MailboxAuthException("Gmail 授权失效（401），请重新连接邮箱。");
        if (resp.StatusCode == (HttpStatusCode)429)
        {
            var retry = resp.Headers.RetryAfter?.Delta;
            throw new MailboxRateLimitedException("Gmail 限流（429），请稍后重试。", retry);
        }
        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* 忽略 */ }
        throw new MailboxProviderException($"Gmail API 错误 {(int)resp.StatusCode}：{Truncate(body)}");
    }

    internal static byte[] DecodeBase64Url(string data)
    {
        var s = data.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
