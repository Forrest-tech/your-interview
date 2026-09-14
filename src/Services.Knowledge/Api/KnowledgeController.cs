using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Knowledge.Application;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Knowledge.Api;

/// <summary>
/// 技术栈知识库 API。
///
/// 权限模型:每个端点挂一个权限策略("perm:knowledge.read" 这种),
/// 而不是判断角色名 —— 新增角色时后端一行代码都不用改(开闭原则在权限上的落地)。
/// </summary>
[ApiController]
[Route("api/knowledge")]
[Authorize]
public sealed class KnowledgeController(ISender sender) : ControllerBase
{
    // ---------------- 查询 ----------------

    /// <summary>分页查询知识点。dueOnly=true 只返回今天该复习的。</summary>
    [HttpGet]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? topic = null, [FromQuery] string? mastery = null,
        [FromQuery] string? source = null, [FromQuery] string? tag = null,
        [FromQuery] string? search = null, [FromQuery] bool dueOnly = false,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new ListKnowledgeQuery(page, pageSize, topic, mastery, source,
            tag, search, dueOnly), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>知识点详情(含复习流水与关联)。</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Get(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetKnowledgeItemQuery(id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>所有技术分类 + 每类数量(前端左侧目录树)。</summary>
    [HttpGet("topics")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Topics(CancellationToken ct)
    {
        var r = await sender.Send(new GetTopicsQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>统计:掌握度分布、按主题分布、今日待复习、最薄弱主题。</summary>
    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetKnowledgeStatsQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>未来 N 天的复习计划(前端日历视图)。</summary>
    [HttpGet("due")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeRead)]
    public async Task<IResult> Due([FromQuery] int days = 7, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetDuePlanQuery(days), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------- 命令 ----------------

    [HttpPost]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Create([FromBody] CreateKnowledgeCommand body, CancellationToken ct)
    {
        var r = await sender.Send(body, ct);
        return r.IsSuccess
            ? Results.Created($"/api/knowledge/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Update(Guid id, [FromBody] UpdateKnowledgeBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateKnowledgeCommand(id, body.Title, body.Topic,
            body.SubTopic, body.Question, body.Difficulty, body.Importance, body.TagsJson,
            body.ConceptExplanation, body.MyAnswer, body.BetterAnswer, body.KeyPointsJson,
            body.CommonMistakesJson, body.FollowUpsJson, body.ReferencesJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeDelete)]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteKnowledgeCommand(id), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>记录一次复习 —— 会按 SM-2 算法重算下次复习时间。</summary>
    [HttpPost("{id:guid}/review")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Review(Guid id, [FromBody] ReviewBody body, CancellationToken ct)
    {
        var r = await sender.Send(new RecordReviewCommand(id, body.Result, body.ConfidenceBefore,
            body.ConfidenceAfter, body.Note, body.DurationSeconds), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>手工调整掌握度。</summary>
    [HttpPost("{id:guid}/mastery")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> ChangeMastery(Guid id, [FromBody] MasteryBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ChangeMasteryCommand(id, body.Mastery), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>建立知识点之间的关联(前置/对比/延伸)—— 用于知识图谱。</summary>
    [HttpPost("{id:guid}/relations")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> AttachRelation(Guid id, [FromBody] RelationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AttachRelationCommand(id, body.RelatedItemId,
            body.RelationType, body.Note), ct);
        return r.IsSuccess
            ? Results.Created($"/api/knowledge/{id}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    /// <summary>批量导入(前端可一次粘贴一堆题目)。幂等:同 topic+title 不重复插。</summary>
    [HttpPost("import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.KnowledgeWrite)]
    public async Task<IResult> Import([FromBody] ImportBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ImportKnowledgeCommand(body.Items), ct);
        return r.IsSuccess ? Results.Ok(new { imported = r.Value }) : r.ToProblemDetails();
    }
}

// ---------------- 请求体 ----------------
// 与命令分开定义:请求体是 HTTP 契约(可变),命令是应用层契约。分开了以后改 API 不动命令。

public sealed record UpdateKnowledgeBody(
    string Title, string Topic, string? SubTopic, string Question, int Difficulty, int Importance,
    string? TagsJson, string? ConceptExplanation, string? MyAnswer, string? BetterAnswer,
    string? KeyPointsJson, string? CommonMistakesJson, string? FollowUpsJson, string? ReferencesJson);

public sealed record ReviewBody(string Result, int? ConfidenceBefore = null,
    int? ConfidenceAfter = null, string? Note = null, int? DurationSeconds = null);

public sealed record MasteryBody(string Mastery);

public sealed record RelationBody(Guid RelatedItemId, string RelationType, string? Note = null);

public sealed record ImportBody(List<ImportKnowledgeRow> Items);
