using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Assessment.Api;

/// <summary>
/// 票119 词典端点（SPEC §2.3/§4.3）：查词、发音链解析、音频流。
/// 静态数据（嵌入资源 + 本地数据目录），不落库、不按用户隔离（与练习素材的私有数据不同）。
/// </summary>
[ApiController]
[Route("api/assessment/dictionary")]
[Authorize]
public sealed class DictionaryController(DictionaryService dictionary, AudioResolver audio, ICurrentUser currentUser) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>查词：命中任一层返回 schema v2；全层未命中回 404 空结果体（SPEC §4.1 第 4 步）。</summary>
    [HttpGet("{word}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public IResult Lookup(string word)
    {
        var result = dictionary.Lookup(word);
        if (result is null)
            return Results.NotFound(new { query = DictionaryService.NormalizeQuery(word), found = false });
        return Results.Json(result, JsonOptions);
    }

    /// <summary>
    /// 发音链单点解析（票254 §4）：只供卡内播放入口；链耗尽回 200 {available:false}。
    /// excludeKeys：逗号分隔的本次请求排除键（§4.2 懒层失败回落用；合成键永不可排除）。
    /// humanOnly：true 时走 Shadow 面（真人层 1–6，绝不合成；跟读专用）。
    /// </summary>
    [HttpGet("{word}/pronounce")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Pronounce(string word, [FromQuery] string accent = "us",
        [FromQuery] string? excludeKeys = null, [FromQuery] bool humanOnly = false, CancellationToken ct = default)
    {
        var result = await dictionary.PronounceAsync(word, accent, ct, excludeKeys, humanOnly);
        return Results.Json(result, JsonOptions);
    }

    /// <summary>
    /// 音频流：key 必须命中某层映射（核心包 manifest / 牛津包 / 懒缓存账本 / audio-index / 合成缓存），禁止路径直拼。
    /// 票254 §1 serve-time 物化校验：合成键 + word/accent 齐全时，key 必须等于 SyntheticKeyFor(word, accent)，
    /// 否则回 400（防缓存投毒：错词音频顶替正词键）；word/accent 缺省时只 serve 已落盘文件，不物化。
    /// </summary>
    [HttpGet("audio/{key}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Audio(string key, [FromQuery] string? word = null,
        [FromQuery] string? accent = null, CancellationToken ct = default)
    {
        if (word is not null && accent is not null && AudioResolver.IsSyntheticKey(key))
        {
            var normalizedAccent = accent == "uk" ? "uk" : "us";
            if (!string.Equals(key, AudioResolver.SyntheticKeyFor(word, normalizedAccent), StringComparison.Ordinal))
                return Results.BadRequest(new { error = "synthetic key does not match word/accent" });
        }
        var userId = currentUser.UserId ?? Guid.Empty;
        var path = await audio.GetAudioFileAsync(key, ct, userId, word, accent);
        if (path is null) return Results.NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(path, "audio/mpeg");
    }
}
