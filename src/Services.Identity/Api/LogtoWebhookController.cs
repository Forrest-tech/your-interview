using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Api;

/// <summary>
/// 票241:Logto webhook 接收端(PLAN §4.2a)。
/// 四个端点都不走 JWT 鉴权,改用 Logto webhook 签名密钥验签(验签失败直接拒收);
/// 未配 Management/Webhook 凭据时回 503(投递方按重试策略重发,不是静默丢事件)。
/// 投递语义:只有审批门三步(置停用 + 打 pending + 镜像写)都成功才回 2xx;任一步失败
/// 回 5xx 由投递方重发;重复投递幂等(已停用且已打 pending 直接 2xx,仍补写镜像)。
/// 接收端同步处理(不排队后回 2xx):排队后失败投递方不再重发,只有扫表能在 15 分钟内
/// 兜住;同步 + 10 秒 Management 客户端超时 + 幂等 + 扫表,才是完整的投递语义。
/// 注意:Logto webhook 是异步通知,不能改变当次注册流程的结果 —— “待审批”
/// 靠本接收端事后置停用实现,不是注册时拦截。
/// </summary>
[ApiController]
[Route("api/webhooks/logto")]
public sealed class LogtoWebhookController(
    LogtoApprovalGate gate,
    IOptions<LogtoManagementOptions> options,
    LogtoGateHealth health) : ControllerBase
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
            // 注意:isSuspended 的值只作 presence 校验,处理器一律以 fresh GET 为准
            // (延迟/重发的旧事件靠 fresh 现态 + 排序水位挡下,见 LogtoApprovalGate)。
            await gate.HandleSuspensionStatusUpdatedAsync(userId, isSuspended, ct);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "审批门执行失败,投递方应重发", detail = ex.Message });
        }
    }

    [HttpPost("user-data-updated")]
    public async Task<IActionResult> UserDataUpdated(CancellationToken ct)
    {
        var (ok, body, error) = await VerifyAsync(ct);
        if (!ok) return error!;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var evt = GetString(root, "event") ?? GetString(root, "type");
            if (evt is not null && !string.Equals(evt, "User.Data.Updated", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "本端点只接受 User.Data.Updated 事件" });
            var userId = GetUserId(root);
            if (userId is null) return BadRequest(new { error = "事件缺 user id" });
            // 验证完成翻转的事件路:pending + 现态已验证 + 现态未停用 → approved。
            // 本事件不是“专用验证事件”(Logto 没有),只是验证完成最常见的触发器;
            // 事件丢了仍有扫表翻转兜底,两者条件不同(见 LogtoApprovalGate 类注释第 6 条)。
            await gate.HandleUserDataUpdatedAsync(userId, ct);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "审批门执行失败,投递方应重发", detail = ex.Message });
        }
    }

    [HttpPost("user-deleted")]
    public async Task<IActionResult> UserDeleted(CancellationToken ct)
    {
        var (ok, body, error) = await VerifyAsync(ct);
        if (!ok) return error!;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var evt = GetString(root, "event") ?? GetString(root, "type");
            if (evt is not null && !string.Equals(evt, "User.Deleted", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "本端点只接受 User.Deleted 事件" });
            var userId = GetUserId(root);
            if (userId is null) return BadRequest(new { error = "事件缺 user id" });
            // 删号快传:镜像置 deleted + 旧侧停用;事件丢了仍有扫表存在检查兜底。
            await gate.HandleUserDeletedAsync(userId, ct);
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
        // 票252(B8-A):逐 key 试,任一 hook 的 key 通过即放行(单 key 旧配置由
        // EffectiveWebhookSigningKeys 回落为单元素列表,行为与票241 一致)。
        if (!LogtoWebhookSignature.VerifyAny(options.Value.EffectiveWebhookSigningKeys(), raw, signature))
            return (false, [], Unauthorized(new { error = "webhook 签名校验失败" }));
        // 验签通过即记一次 webhook 心跳(健康端点据此判断断流;失败投递不记)。
        health.RecordWebhookReceived();
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
