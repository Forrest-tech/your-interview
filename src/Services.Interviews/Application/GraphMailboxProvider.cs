using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票 240：Microsoft Graph 实现（方案 §1，Outlook / Microsoft 365 个人账号）。
/// scope：Mail.Read + offline_access + openid email profile（delegated，只读）。
/// 增量：/me/messages/delta，跟随 @odata.nextLink 至 @odata.deltaLink；
/// deltaLink 过期返回 410 → CursorExpired，由调用方回落 30 天窗口重拉。
/// delta 不支持 $search，公司过滤放本地做（方案 §1 已定）。
/// 原文：GET /me/messages/{id}/$value（message/rfc822）；失败则该封计 failed，不存假原文。
/// 撤销：Graph 无集中撤销端点——断开 = 本地删 token（即失效），RevokeAsync 为 no-op。
/// </summary>
public sealed class GraphMailboxProvider(HttpClient http) : IMailboxProvider
{
    public MailboxProvider Kind => MailboxProvider.Graph;

    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private const string Select =
        "id,conversationId,from,toRecipients,ccRecipients,subject,receivedDateTime,body,hasAttachments,internetMessageId";

    public string GetAuthorizationUrl(string clientId, string redirectUri, string state)
    {
        var scope = Uri.EscapeDataString("Mail.Read offline_access openid email profile");
        return $"{Authority}/authorize?client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               "&response_type=code" +
               $"&scope={scope}&state={Uri.EscapeDataString(state)}";
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

    public Task RevokeAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        // Graph 无集中撤销端点：断开 = 本地删 token 即失效（方案 §4 已定）。
        return Task.CompletedTask;
    }

    public async Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor,
        DateTimeOffset sinceUtc, CancellationToken ct)
    {
        // cursor 有值 = 存量 deltaLink（直接 GET，勿再拼 $filter）；null = 首刷/回落：delta + receivedDateTime 窗口。
        var url = cursor ?? $"{GraphBase}/me/messages/delta?$filter={Uri.EscapeDataString($"receivedDateTime ge {sinceUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ss'Z'}")}&$select={Select}";
        var messages = new List<MailboxMessage>();
        string? deltaLink = null;
        var current = url;
        var isDeltaLinkCall = cursor is not null;
        while (current is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Gone && isDeltaLinkCall)
                return new([], null, CursorExpired: true);
            await ThrowIfErrorAsync(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var values))
            {
                foreach (var v in values.EnumerateArray())
                {
                    // delta 的 tombstone（@removed）不是新邮件，跳过。
                    if (v.TryGetProperty("@removed", out _)) continue;
                    var parsed = ParseGraphMessage(v);
                    if (parsed is not null) messages.Add(parsed);
                }
            }
            deltaLink = root.TryGetProperty("@odata.deltaLink", out var dl) ? dl.GetString() : null;
            current = root.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
            isDeltaLinkCall = false;
        }
        // 每封补取原文（$value）与附件清单：delta 页不含附件字节，附件在导入时按需取。
        var withRaw = new List<MailboxMessage>();
        foreach (var m in messages)
        {
            var raw = await GetRawAsync(accessToken, m.ProviderMessageId, ct);
            withRaw.Add(m with { RawMimeBase64Url = raw });
        }
        return new(withRaw, deltaLink, CursorExpired: false);
    }

    public async Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{GraphBase}/me/messages/{Uri.EscapeDataString(providerMessageId)}?$select={Select}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var parsed = ParseGraphMessage(doc.RootElement);
        if (parsed is null) return null;
        var raw = await GetRawAsync(accessToken, providerMessageId, ct);
        return parsed with { RawMimeBase64Url = raw };
    }

    public async Task<byte[]?> GetAttachmentBytesAsync(string accessToken, string providerMessageId,
        string providerAttachmentId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{GraphBase}/me/messages/{Uri.EscapeDataString(providerMessageId)}/attachments/{Uri.EscapeDataString(providerAttachmentId)}?$select=contentBytes");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowIfErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var b64 = doc.RootElement.TryGetProperty("contentBytes", out var b) ? b.GetString() : null;
        return b64 is null ? null : Convert.FromBase64String(b64);
    }

    /// <summary>整组拉取：conversationId eq（Graph 的 conversation 等价于 Gmail thread）。</summary>
    public async Task<IReadOnlyList<MailboxMessage>> GetConversationAsync(string accessToken, string conversationId,
        CancellationToken ct)
    {
        var list = new List<MailboxMessage>();
        string? next = $"{GraphBase}/me/messages?$filter={Uri.EscapeDataString($"conversationId eq '{conversationId.Replace("'", "''")}'")}&$select={Select}&$orderby=receivedDateTime asc";
        while (next is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) break;
            await ThrowIfErrorAsync(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var values))
            {
                foreach (var v in values.EnumerateArray())
                {
                    var parsed = ParseGraphMessage(v);
                    if (parsed is not null) list.Add(parsed);
                }
            }
            next = root.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
        var withRaw = new List<MailboxMessage>();
        foreach (var m in list)
        {
            var raw = await GetRawAsync(accessToken, m.ProviderMessageId, ct);
            withRaw.Add(m with { RawMimeBase64Url = raw });
        }
        return withRaw;
    }

    private async Task<string?> GetRawAsync(string accessToken, string id, CancellationToken ct)
    {
        // $value 取原始 MIME：失败（某些消息类型不支持）→ null，调用方计 failed，不存假原文。
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{GraphBase}/me/messages/{Uri.EscapeDataString(id)}/$value");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static MailboxMessage? ParseGraphMessage(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (id is null) return null;
        var conversationId = root.TryGetProperty("conversationId", out var c) ? c.GetString() : null;
        var from = root.TryGetProperty("from", out var f) ? f : (JsonElement?)null;
        var (fromEmail, fromName) = ParseRecipient(from);
        var to = ParseRecipients(root.TryGetProperty("toRecipients", out var t) ? t : null);
        var cc = ParseRecipients(root.TryGetProperty("ccRecipients", out var ccEl) ? ccEl : null);
        var subject = root.TryGetProperty("subject", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        var receivedAt = root.TryGetProperty("receivedDateTime", out var r)
            && DateTimeOffset.TryParse(r.GetString(), out var dto) ? dto : DateTimeOffset.UtcNow;
        var body = root.TryGetProperty("body", out var b) ? b : (JsonElement?)null;
        var contentType = body is { } bb && bb.TryGetProperty("contentType", out var ctEl) ? ctEl.GetString() : null;
        var content = body is { } bb2 && bb2.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
        var bodyText = contentType == "html" ? StripHtml(content) : content;
        var bodyHtml = contentType == "html" ? content : null;
        // 附件清单：delta/列表页只给 hasAttachments 标志；清单在导入时按需取（见导入 Handler）。
        return new MailboxMessage(
            id, conversationId, fromEmail, fromName, to, cc, subject, receivedAt,
            bodyText, bodyHtml, RawMimeBase64Url: null, [],
            root.TryGetProperty("internetMessageId", out var mid) ? mid.GetString() : null);
    }

    /// <summary>附件清单（导入时按需取）：fileAttachment 才有 contentBytes，itemAttachment 跳过。</summary>
    public async Task<IReadOnlyList<MailboxAttachment>> ListAttachmentsAsync(string accessToken, string id,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{GraphBase}/me/messages/{Uri.EscapeDataString(id)}/attachments?$select=id,name,contentType,size");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return [];
        await ThrowIfErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<MailboxAttachment>();
        if (!doc.RootElement.TryGetProperty("value", out var values)) return list;
        foreach (var v in values.EnumerateArray())
        {
            var type = v.TryGetProperty("@odata.type", out var t) ? t.GetString() : null;
            if (type is not null && !type.EndsWith("fileAttachment", StringComparison.Ordinal)) continue;
            var name = v.TryGetProperty("name", out var n) ? n.GetString() : null;
            var attId = v.TryGetProperty("id", out var a) ? a.GetString() : null;
            if (name is null || attId is null) continue;
            var size = v.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            var ctype = v.TryGetProperty("contentType", out var c) ? c.GetString() : null;
            list.Add(new(name, ctype ?? "application/octet-stream", size, attId, null));
        }
        return list;
    }

    private static (string Email, string? Name) ParseRecipient(JsonElement? recipient)
    {
        if (recipient is not { } r) return (string.Empty, null);
        var addr = r.TryGetProperty("emailAddress", out var ea) ? ea : (JsonElement?)null;
        if (addr is not { } a) return (string.Empty, null);
        return (a.TryGetProperty("address", out var ad) ? ad.GetString() ?? string.Empty : string.Empty,
            a.TryGetProperty("name", out var nm) ? nm.GetString() : null);
    }

    private static IReadOnlyList<string> ParseRecipients(JsonElement? array)
    {
        if (array is not { } arr || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var (email, _) = ParseRecipient(item);
            if (email.Length > 0) list.Add(email);
        }
        return list;
    }

    private static string StripHtml(string html)
    {
        // HTML 回落去标签：只做展示用正文，原文仍以 $value 为准，不影响原文完整性。
        var s = System.Text.RegularExpressions.Regex.Replace(html, @"<script[\s\S]*?</script>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<style[\s\S]*?</style>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"</(p|div|tr|li|h[1-6])>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }

    private async Task<MailboxTokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Authority}/token")
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var resp = await http.SendAsync(req, ct);
        await ThrowIfErrorAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (access is null) throw new MailboxProviderException("Graph token 响应缺 access_token。");
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
            throw new MailboxAuthException("Outlook 授权失效（401），请重新连接邮箱。");
        if (resp.StatusCode == (HttpStatusCode)429)
        {
            var retry = resp.Headers.RetryAfter?.Delta;
            throw new MailboxRateLimitedException("Outlook 限流（429），请稍后重试。", retry);
        }
        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* 忽略 */ }
        throw new MailboxProviderException($"Graph API 错误 {(int)resp.StatusCode}：{Truncate(body)}");
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
