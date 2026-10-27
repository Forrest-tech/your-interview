using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Api;

/// <summary>
/// 票241:Logto webhook 接收端(PLAN §4.2a)。
/// 两个端点都不走 JWT 鉴权,改用 Logto webhook 签名密钥验签(验签失败直接拒收);
/// 未配 Management/Webhook 凭据时回 503(投递方按重试策略重发,不是静默丢事件)。
/// 投递语义:只有审批门两步(置停用 + 打 pending)都成功才回 2xx;任一步失败
/// 回 5xx 由投递方重发;重复投递幂等(已停用且已打 pending 直接 2xx)。
/// 注意:Logto webhook 是异步通知,不能改变当次注册流程的结果 —— “待审批”
/// 靠本接收端事后置停用实现,不是注册时拦截。
/// </summary>
[ApiController]
[Route("api/webhooks/logto")]
public sealed class LogtoWebhookController(
    LogtoApprovalGate gate,
    IOptions<LogtoManagementOptions> options) : ControllerBase
{
    [HttpPost("user-created")]
    public async Task<IActionResult> UserCreated(CancellationToken ct)
    {
        var (ok, body, error) = await VerifyAsync(ct);
        if (!ok) return error!;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var evt = GetString(root, "event") ?? GetString(root, "type");
            if (evt is not null && !string.Equals(evt, "User.Created", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "本端点只接受 User.Created 事件" });
            var userId = GetUserId(root);
            if (userId is null) return BadRequest(new { error = "事件缺 user id" });
            await gate.HandleUserCreatedAsync(userId, ct);
            return Ok();
        }
        catch (Exception ex)
        {
            // 5xx = 请投递方重发;审批门本身是幂等的,重发安全。
            return StatusCode(500, new { error = "审批门执行失败,投递方应重发", detail = ex.Message });
        }
    }

    [HttpPost("suspension-status-updated")]
    public async Task<IActionResult> SuspensionStatusUpdated(CancellationToken ct)
    {
        var (ok, body, error) = await VerifyAsync(ct);
        if (!ok) return error!;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var evt = GetString(root, "event") ?? GetString(root, "type");
            if (evt is not null && !string.Equals(evt, "User.SuspensionStatus.Updated", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "本端点只接受 User.SuspensionStatus.Updated 事件" });
            var userId = GetUserId(root);
            if (userId is null) return BadRequest(new { error = "事件缺 user id" });
            var data = GetData(root);
            if (data is null || !TryGetBool(data.Value, "isSuspended", out var isSuspended))
                return BadRequest(new { error = "事件缺 isSuspended" });
            await gate.HandleSuspensionStatusUpdatedAsync(userId, isSuspended, ct);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "审批门执行失败,投递方应重发", detail = ex.Message });
        }
    }

    private async Task<(bool Ok, byte[] Body, IActionResult? Error)> VerifyAsync(CancellationToken ct)
    {
        if (!options.Value.WebhookConfigured)
            return (false, [], StatusCode(503, new { error = "Logto webhook 未配置(缺 Management 凭据或签名密钥)" }));
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var raw = ms.ToArray();
        var signature = Request.Headers[LogtoWebhookSignature.HeaderName].FirstOrDefault();
        if (!LogtoWebhookSignature.Verify(options.Value.WebhookSigningKey, raw, signature))
            return (false, [], Unauthorized(new { error = "webhook 签名校验失败" }));
        return (true, raw, null);
    }

    private static JsonElement? GetData(JsonElement root) =>
        root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;

    private static string? GetUserId(JsonElement root)
    {
        var data = GetData(root);
        return GetString(data, "id") ?? GetString(data, "userId")
            ?? (root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                ? GetString(u, "id") : null)
            ?? GetString(root, "userId");
    }

    private static string? GetString(JsonElement? el, string name) =>
        el is { ValueKind: JsonValueKind.Object }
        && el.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool TryGetBool(JsonElement el, string name, out bool value)
    {
        value = false;
        if (el.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = v.GetBoolean();
            return true;
        }
        return false;
    }
}
