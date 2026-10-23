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
public sealed class DictionaryController(DictionaryService dictionary, AudioResolver audio) : ControllerBase
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

    /// <summary>发音链单点解析（票126 §8.2）：只供卡内播放入口；链耗尽回 200 {available:false}，不再有 device/neural 兜底。</summary>
    [HttpGet("{word}/pronounce")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Pronounce(string word, [FromQuery] string accent = "us", CancellationToken ct = default)
    {
        var result = await dictionary.PronounceAsync(word, accent, ct);
        return Results.Json(result, JsonOptions);
    }

    /// <summary>音频流：key 必须命中某层映射（核心包 manifest / 牛津包 / 懒缓存账本 / audio-index），禁止路径直拼。</summary>
    [HttpGet("audio/{key}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Audio(string key, CancellationToken ct)
    {
        var path = await audio.GetAudioFileAsync(key, ct);
        if (path is null) return Results.NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(path, "audio/mpeg");
    }
}
